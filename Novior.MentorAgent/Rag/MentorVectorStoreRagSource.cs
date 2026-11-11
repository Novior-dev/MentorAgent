using System.Linq.Expressions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.VectorData;

namespace Novior.MentorAgent.Rag;

/// <summary>
/// An <see cref="IMentorRagSource"/> over a collection of <c>Microsoft.Extensions.VectorData</c> — pgvector, Qdrant,
/// Azure AI Search, an in-memory one: any <see cref="IVectorSearchable{TRecord}"/>. Register it with
/// <c>AddMentorVectorStoreRag</c>, and say in <see cref="MentorVectorStoreRagOptions{TRecord}"/> what of a record is the
/// document and its citation.
/// <code>
/// builder.Services.AddMentorVectorStoreRag(
///     sp => sp.GetRequiredService&lt;VectorStore&gt;().GetCollection&lt;Guid, Doc&gt;("docs"),
///     new MentorVectorStoreRagOptions&lt;Doc&gt;
///     {
///         Content = d =&gt; d.Text,
///         Title = d =&gt; d.Title,
///         SourceUrl = d =&gt; d.Url,
///         Filter = request =&gt; d =&gt; d.Tenant == request.TenantId,
///     });
/// </code>
/// <para>
/// The scores are turned into similarities, higher meaning nearer, whatever the collection's distance function: with
/// <c>CosineDistance</c>, the Postgres default, a raw score would keep the farthest documents and drop the nearest. The
/// message is embedded with <see cref="MentorVectorStoreRagOptions{TRecord}.EmbeddingGenerator"/>, else with
/// <c>MentorOptions.EmbeddingGenerator</c>, once per turn for each model (<see cref="IMentorQueryEmbedding"/>), else
/// by the collection itself. The source keeps no state of its own.
/// </para>
/// </summary>
/// <typeparam name="TRecord">The collection's record type.</typeparam>
public sealed class MentorVectorStoreRagSource<TRecord> : IMentorRagSource where TRecord : class
{
    private readonly IVectorSearchable<TRecord> _collection;
    private readonly MentorVectorStoreRagOptions<TRecord> _options;
    private readonly VectorScoreScale _scale;
    private readonly VectorStoreRagWarnings _warnings;
    private readonly ILogger _logger;
    private readonly IEmbeddingGenerator<string, Embedding<float>>? _mentorGenerator;
    private readonly IMentorQueryEmbedding? _queryEmbedding;

    /// <summary>
    /// A source built by hand. It embeds the message with <see cref="MentorVectorStoreRagOptions{TRecord}.EmbeddingGenerator"/>,
    /// or passes its text to the collection; registered with <c>AddMentorVectorStoreRag</c>, it shares the turn's vector.
    /// </summary>
    /// <param name="collection">The collection to search.</param>
    /// <param name="options">What of a record is the document, and what the collection's scores mean.</param>
    /// <param name="logger">Where its warnings go.</param>
    /// <exception cref="InvalidOperationException">The distance function is unknown, or has no fixed scale.</exception>
    public MentorVectorStoreRagSource(
        IVectorSearchable<TRecord> collection,
        MentorVectorStoreRagOptions<TRecord> options,
        ILogger<MentorVectorStoreRagSource<TRecord>>? logger = null)
        : this(collection, options, logger, ScaleOf(options), new VectorStoreRagWarnings(), mentorGenerator: null, queryEmbedding: null)
    {
    }

    internal MentorVectorStoreRagSource(
        IVectorSearchable<TRecord> collection,
        MentorVectorStoreRagOptions<TRecord> options,
        ILogger? logger,
        VectorScoreScale scale,
        VectorStoreRagWarnings warnings,
        IEmbeddingGenerator<string, Embedding<float>>? mentorGenerator,
        IMentorQueryEmbedding? queryEmbedding)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(options);
        _collection = collection;
        _options = options;
        _scale = scale;
        _warnings = warnings;
        _logger = logger ?? NullLogger.Instance;
        _mentorGenerator = mentorGenerator;
        _queryEmbedding = queryEmbedding;
    }

    /// <inheritdoc/>
    public float? DefaultMinScore => _options.DefaultMinScore;

    /// <inheritdoc/>
    public Task<IReadOnlyList<MentorRagResult>> SearchAsync(string query, int maxResults, CancellationToken ct = default)
        => SearchAsync(new MentorRagRequest { Query = query, MaxResults = maxResults }, ct);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<MentorRagResult>> SearchAsync(MentorRagRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Query) || request.MaxResults <= 0) return [];

        if (request.TenantId is not null && _options.Filter is null && _warnings.FirstTenant())
            _logger.LogWarning(
                "[MentorAgent:RAG] MentorVectorStoreRagSource<{Record}> has no Filter, and an IMentorTenantResolver is " +
                "registered: it searches every tenant's documents. Set MentorVectorStoreRagOptions.Filter to keep the " +
                "records of MentorRagRequest.TenantId.",
                typeof(TRecord).Name);

        var search = new VectorSearchOptions<TRecord>
        {
            Filter = _options.Filter?.Invoke(request),
            VectorProperty = _options.VectorProperty,
            IncludeVectors = false,
        };
        var hits = await QueryVectorAsync(request.Query, ct) is { } vector
            ? _collection.SearchAsync(vector, request.MaxResults, search, ct)
            : _collection.SearchAsync(request.Query, request.MaxResults, search, ct);

        // In the collection's order, which is by relevance whatever its scores mean.
        var results = new List<MentorRagResult>();
        await foreach (var hit in hits.WithCancellation(ct))
        {
            var content = _options.Content(hit.Record);
            if (string.IsNullOrWhiteSpace(content)) continue;
            results.Add(new MentorRagResult(content, _options.SourceUrl?.Invoke(hit.Record), _options.Title?.Invoke(hit.Record), ScoreOf(hit.Score))
            {
                Metadata = _options.Metadata?.Invoke(hit.Record),
            });
            if (results.Count == request.MaxResults) break;
        }

        return results;
    }

    /// <summary>
    /// The message's vector, or <see langword="null"/> when there is no generator and the collection embeds the text.
    /// Through the turn's cache with either generator: it keeps each model's vector apart.
    /// </summary>
    private async Task<ReadOnlyMemory<float>?> QueryVectorAsync(string query, CancellationToken ct)
    {
        var generator = _options.EmbeddingGenerator ?? _mentorGenerator;
        if (generator is null) return null;
        if (_queryEmbedding is not null)
            return await _queryEmbedding.GetAsync(query, generator, ct);

        var generated = await generator.GenerateAsync([query], cancellationToken: ct);
        return generated[0].Vector;
    }

    /// <summary>The collection's score as a similarity: higher is nearer, as <c>RagMinScore</c> reads it.</summary>
    private float ScoreOf(double? score)
    {
        if (score is not { } s)
        {
            if (_warnings.FirstUnscored())
                _logger.LogWarning(
                    "[MentorAgent:RAG] The collection returned a result without a score: MentorVectorStoreRagSource<{Record}> " +
                    "keeps it, and RagMinScore cannot filter it.",
                    typeof(TRecord).Name);
            return 1f;
        }

        return _scale switch
        {
            VectorScoreScale.Distance => (float)(1d - s),
            VectorScoreScale.Negated => (float)-s,
            _ => (float)s,
        };
    }

    /// <summary>
    /// What the collection's scores mean, from <see cref="MentorVectorStoreRagOptions{TRecord}.DistanceFunction"/> or the
    /// record's <see cref="VectorStoreVectorAttribute"/>. Called when the source is registered, so that a wrong one stops
    /// the application at startup rather than ranking documents backwards.
    /// </summary>
    /// <exception cref="InvalidOperationException">No distance function is named, or it has no fixed scale.</exception>
    internal static VectorScoreScale ScaleOf(MentorVectorStoreRagOptions<TRecord> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Content);
        var why = "";
        var function = options.DistanceFunction ?? DistanceFunctionOfTheRecord(options.VectorProperty, out why);
        if (function is null)
            throw new InvalidOperationException(
                $"Set MentorVectorStoreRagOptions<{typeof(TRecord).Name}>.DistanceFunction to the collection's distance " +
                $"function (DistanceFunction.CosineSimilarity, DistanceFunction.CosineDistance, …): {why}. Its scores " +
                "mean opposite things — with a distance a lower score is nearer — and RagMinScore keeps the higher ones.");

        return function switch
        {
            Microsoft.Extensions.VectorData.DistanceFunction.CosineSimilarity
                or Microsoft.Extensions.VectorData.DistanceFunction.DotProductSimilarity => VectorScoreScale.Similarity,
            Microsoft.Extensions.VectorData.DistanceFunction.CosineDistance => VectorScoreScale.Distance,
            Microsoft.Extensions.VectorData.DistanceFunction.NegativeDotProductSimilarity => VectorScoreScale.Negated,
            _ => throw new InvalidOperationException(
                $"The distance function {function} of MentorVectorStoreRagSource<{typeof(TRecord).Name}> has no fixed scale " +
                "a RagMinScore could be set on: use DistanceFunction.CosineSimilarity or DistanceFunction.CosineDistance " +
                "(or DotProductSimilarity on normalised embeddings)."),
        };
    }

    private static string? DistanceFunctionOfTheRecord(Expression<Func<TRecord, object?>>? vectorProperty, out string why)
    {
        var vectors = typeof(TRecord).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => (Property: p, Attribute: p.GetCustomAttribute<VectorStoreVectorAttribute>()))
            .Where(v => v.Attribute is not null)
            .ToList();

        if (vectorProperty is not null)
        {
            var body = vectorProperty.Body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert
                ? convert.Operand
                : vectorProperty.Body;
            var name = (body as MemberExpression)?.Member.Name;
            vectors = vectors.Where(v => v.Property.Name == name).ToList();
        }

        switch (vectors.Count)
        {
            case 0:
                why = $"{typeof(TRecord).Name} has no [VectorStoreVector] property to read it from";
                return null;
            case > 1:
                why = $"{typeof(TRecord).Name} has several [VectorStoreVector] properties: set VectorProperty as well";
                return null;
        }

        why = $"the [VectorStoreVector] attribute of {typeof(TRecord).Name}.{vectors[0].Property.Name} names none, and each " +
              "store has a default of its own";
        return vectors[0].Attribute!.DistanceFunction;
    }
}

/// <summary>What a collection's scores mean, as <see cref="MentorVectorStoreRagSource{TRecord}"/> reads them.</summary>
internal enum VectorScoreScale
{
    /// <summary>Higher is nearer: the score as it is.</summary>
    Similarity,

    /// <summary>A cosine distance, lower is nearer: 1 − score.</summary>
    Distance,

    /// <summary>A negated similarity: −score.</summary>
    Negated,
}

/// <summary>The warnings said once per registration of a source, shared by the scoped instances of every session.</summary>
internal sealed class VectorStoreRagWarnings
{
    private int _tenant;
    private int _unscored;

    internal bool FirstTenant() => Interlocked.Exchange(ref _tenant, 1) == 0;

    internal bool FirstUnscored() => Interlocked.Exchange(ref _unscored, 1) == 0;
}
