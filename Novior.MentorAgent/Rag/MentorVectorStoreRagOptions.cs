using System.Linq.Expressions;
using Microsoft.Extensions.VectorData;

namespace Novior.MentorAgent.Rag;

/// <summary>
/// How a <see cref="MentorVectorStoreRagSource{TRecord}"/> reads a collection of <c>Microsoft.Extensions.VectorData</c>:
/// what of a record becomes the document and its citation, which records a request may see, and the collection's distance
/// function, which says what its scores mean.
/// </summary>
/// <typeparam name="TRecord">The collection's record type.</typeparam>
public sealed class MentorVectorStoreRagOptions<TRecord> where TRecord : class
{
    /// <summary>The text of the document, the part the model reads. A record with no text is left out.</summary>
    public required Func<TRecord, string?> Content { get; init; }

    /// <summary>The link of the citation chip, if the document has one.</summary>
    public Func<TRecord, string?>? SourceUrl { get; init; }

    /// <summary>The title shown to the model above the document and on its citation chip.</summary>
    public Func<TRecord, string?>? Title { get; init; }

    /// <summary>
    /// Facts about the document the model should weigh with it — a date, a version, the product it applies to
    /// (<see cref="MentorRagResult.Metadata"/>).
    /// </summary>
    public Func<TRecord, IReadOnlyDictionary<string, string>?>? Metadata { get; init; }

    /// <summary>
    /// The records this request may see, as the collection's filter: the tenant's own
    /// (<see cref="MentorRagRequest.TenantId"/>), those the user may read (<see cref="MentorRagRequest.User"/>).
    /// <see langword="null"/>, or a filter that returns <see langword="null"/>, searches the whole collection. With an
    /// <c>IMentorTenantResolver</c> and no filter, the first search with a tenant is logged as a warning: every tenant
    /// would be shown every tenant's documents.
    /// </summary>
    public Func<MentorRagRequest, Expression<Func<TRecord, bool>>?>? Filter { get; init; }

    /// <summary>
    /// The collection's distance function, one of the <see cref="Microsoft.Extensions.VectorData.DistanceFunction"/>
    /// values. It says what a score means: with <c>CosineDistance</c>, the Postgres default, a lower score is nearer,
    /// and the source turns it into a similarity (1 − distance) so that <c>RagMinScore</c> keeps the nearest documents.
    /// <see langword="null"/>: read from the <see cref="VectorStoreVectorAttribute"/> of the record's vector property.
    /// When neither names one, registering the source fails: each store has a default of its own.
    /// <c>CosineSimilarity</c>, <c>CosineDistance</c>, <c>DotProductSimilarity</c> and
    /// <c>NegativeDotProductSimilarity</c> are accepted; a Euclidean, Manhattan or Hamming distance has no scale a
    /// threshold could be set on, and is refused.
    /// </summary>
    public string? DistanceFunction { get; init; }

    /// <summary>The vector property to search, for a record with more than one; the only one otherwise.</summary>
    public Expression<Func<TRecord, object?>>? VectorProperty { get; init; }

    /// <summary>
    /// The threshold for this source's scores, used when the host does not set <c>RagMinScore</c>
    /// (<see cref="IMentorRagSource.DefaultMinScore"/>). Default: 0.5, a loose cosine similarity.
    /// </summary>
    public float DefaultMinScore { get; init; } = 0.5f;

    /// <summary>
    /// The model that embeds the user's message, which must be the one the collection's documents were embedded with.
    /// <see langword="null"/>: <c>MentorOptions.EmbeddingGenerator</c>, whose vector of the message is computed once
    /// per turn and shared with routing, tool filtering and memory. When neither is set, the message's text goes to the
    /// collection, which embeds it with its own generator.
    /// </summary>
    public IEmbeddingGenerator<string, Embedding<float>>? EmbeddingGenerator { get; init; }
}
