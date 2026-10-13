using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Options;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Novior.MentorAgent.Core;

/// <summary>
/// Thread-safe, process-wide implementation of <see cref="IMentorMetrics"/> that feeds the admin
/// token &amp; cost dashboard (roadmap #9). Usage is bucketed <b>per model</b> — the cheap chat model,
/// the strong routing model and the embedding model are tracked separately (tokens, requests, latency)
/// with an hourly time series for the temporal charts. Cost is computed per model, only for models
/// present in <see cref="MentorOptions.ModelPricing"/>. Conversation-level counters (turns, errors,
/// guardrail blocks, top actions) are model-independent.
/// </summary>
public sealed class MentorMetrics : IMentorMetrics
{
    private const int BucketMinutes = 60; // hourly time-series buckets

    private readonly MentorTokenUsage _tokens;
    private readonly MentorOptions _options;

    private long _turns;
    private long _served;
    private long _errors;
    private long _guardrailBlocks;
    private readonly ConcurrentDictionary<string, long> _actions = new(StringComparer.OrdinalIgnoreCase);

    // Per-model usage buckets, keyed by model id.
    private readonly ConcurrentDictionary<string, ModelBucket> _models = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Primary (cheap) chat model id — set once by the orchestrator. Also used for pricing lookup.</summary>
    public string? ModelId { get; set; }

    /// <summary>Strong routing model id (roadmap #11), set by the orchestrator when configured.</summary>
    public string? StrongModelId { get; set; }

    /// <summary>Embedding model id, set by the orchestrator when an embedding generator is configured.</summary>
    public string? EmbeddingModelId { get; set; }

    public MentorMetrics(MentorTokenUsage tokens, IOptions<MentorOptions> options)
    {
        _tokens = tokens;
        _options = options.Value;
    }

    public void RecordTurn()           => Interlocked.Increment(ref _turns);
    public void RecordServed()         => Interlocked.Increment(ref _served);
    public void RecordError()          => Interlocked.Increment(ref _errors);
    public void RecordGuardrailBlock() => Interlocked.Increment(ref _guardrailBlocks);

    public void RecordToolCall(string toolName)
        => _actions.AddOrUpdate(string.IsNullOrWhiteSpace(toolName) ? "unknown" : toolName, 1, (_, c) => c + 1);

    /// <summary>
    /// Records one model round-trip's usage against the given model id (roadmap #9 per-model dashboard).
    /// Called by <c>TokenUsageChatClient</c> (chat, reading <c>response.ModelId</c>) and by
    /// <c>MeteringEmbeddingGenerator</c> (embeddings). Latency/TTFB are optional.
    /// </summary>
    /// <param name="usageMeasured">
    /// <see langword="false"/> when the call reached the provider but never reported its token
    /// counts — a stream the user stopped. Such a call is still counted as a request (it was billed,
    /// input prompt included) and disclosed through
    /// <see cref="MentorModelUsage.RequestsWithoutUsage"/>, but it contributes nothing to the token
    /// totals: the numbers are unknown, and zero is a fabricated measurement, not a cautious one
    /// (BUG-032).
    /// </param>
    /// <param name="modelId">The model that served the call, as the provider reported it.</param>
    /// <param name="inputTokens">Input tokens of the call.</param>
    /// <param name="outputTokens">Output tokens of the call.</param>
    /// <param name="latencyMs">Duration of the whole call, when measured.</param>
    /// <param name="timeToFirstByteMs">Time to the first streamed byte, when measured.</param>
    public void RecordModelUsage(
        string? modelId, long inputTokens, long outputTokens,
        double? latencyMs = null, double? timeToFirstByteMs = null,
        bool usageMeasured = true)
    {
        // Keep the global chat counters in sync for backward compat / other readers.
        _tokens.Record(inputTokens, outputTokens);

        var id = string.IsNullOrWhiteSpace(modelId) ? (ModelId ?? "unknown") : Canonicalise(modelId);
        var b = _models.GetOrAdd(id, _ => new ModelBucket());
        b.Kind = Classify(id);

        Interlocked.Add(ref b.Input, inputTokens);
        Interlocked.Add(ref b.Output, outputTokens);
        Interlocked.Increment(ref b.Requests);
        if (!usageMeasured) Interlocked.Increment(ref b.RequestsWithoutUsage);
        if (latencyMs is { } l)
        {
            Interlocked.Add(ref b.LatencySumMs, (long)l);
            Interlocked.Increment(ref b.LatencyCount);
        }
        if (timeToFirstByteMs is { } t)
        {
            Interlocked.Add(ref b.TtfbSumMs, (long)t);
            Interlocked.Increment(ref b.TtfbCount);
        }

        var bucket = ToBucketKey(DateTimeOffset.UtcNow);
        var p = b.Series.GetOrAdd(bucket, _ => new Point());
        Interlocked.Add(ref p.Input, inputTokens);
        Interlocked.Add(ref p.Output, outputTokens);
        Interlocked.Increment(ref p.Requests);
        if (latencyMs is { } l2)
        {
            Interlocked.Add(ref p.LatencySumMs, (long)l2);
            Interlocked.Increment(ref p.LatencyCount);
        }
    }

    /// <summary>
    /// Folds a provider-reported model id onto the id this application actually configured, when the
    /// two are the same model named at different precision.
    ///
    /// <para>
    /// A provider may answer with a dated build — <c>gpt-4.1-2025-04-14</c> for a deployment called
    /// <c>gpt-4.1</c> — and it may do so inconsistently: one live session reported nothing at all,
    /// the next reported the dated form, same endpoint and same API. Left alone the two forms become
    /// two rows: the configured model shows <b>0 requests</b>, the real traffic is classified
    /// <c>Other</c> so the cheap/strong split is lost, its cost is unknown because
    /// <see cref="MentorOptions.ModelPricing"/> is keyed on the deployment name — and the aggregate
    /// then totals a real, billed session at <b>€0,00</b> (BUG-034).
    /// </para>
    ///
    /// <para>
    /// <b>The rule is exact, and prefix matching would be a bug.</b> <c>gpt-4.1-mini</c> begins with
    /// <c>gpt-4.1</c> and folding it in would price a small model at a large one's rate —
    /// overstating, which is worse than reporting nothing. So only a trailing ISO date
    /// (<c>-YYYY-MM-DD</c>) is stripped, and only if what remains is a model this application
    /// already knows. Anything else is left exactly as the provider reported it: normalisation may
    /// redirect to something known, never invent a neighbour.
    /// </para>
    /// </summary>
    private string Canonicalise(string id)
    {
        if (IsKnown(id)) return id;

        var stripped = DateSuffix.Replace(id, string.Empty);
        return stripped.Length != id.Length && IsKnown(stripped) ? stripped : id;
    }

    /// <summary>An id this application configured, or priced — the only legitimate fold targets.</summary>
    private bool IsKnown(string id)
        => Eq(id, ModelId) || Eq(id, StrongModelId) || Eq(id, EmbeddingModelId)
        || (_options.ModelPricing?.ContainsKey(id) ?? false);

    private static readonly Regex DateSuffix =
        new(@"-\d{4}-\d{2}-\d{2}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private MentorModelKind Classify(string id) =>
        Eq(id, ModelId)          ? MentorModelKind.Cheap
      : Eq(id, StrongModelId)    ? MentorModelKind.Strong
      : Eq(id, EmbeddingModelId) ? MentorModelKind.Embedding
      :                            MentorModelKind.Other;

    private static bool Eq(string a, string? b) => b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Registers the configured cheap / strong / embedding model ids as zero-usage buckets so they
    /// appear (and are selectable) in the dashboard <b>before</b> they serve any traffic — e.g. the
    /// strong routing model when no turn has escalated to it yet. Idempotent and non-destructive:
    /// it never resets a bucket that already has live counters.
    /// </summary>
    /// <remarks>
    /// The ids are read straight from the configured clients' metadata (the orchestrator captures the
    /// same values, but the coordinator is built lazily on first chat — this keeps the dashboard
    /// correct even when it's opened before any conversation).
    /// </remarks>
    public void EnsureConfiguredModels()
    {
        ModelId          ??= _options.ChatClient?.GetService<ChatClientMetadata>()?.DefaultModelId;
        StrongModelId    ??= _options.StrongChatClient?.GetService<ChatClientMetadata>()?.DefaultModelId;
        EmbeddingModelId ??= (_options.EmbeddingGenerator?
            .GetService(typeof(EmbeddingGeneratorMetadata)) as EmbeddingGeneratorMetadata)?.DefaultModelId;

        Ensure(ModelId, MentorModelKind.Cheap);
        Ensure(StrongModelId, MentorModelKind.Strong);
        Ensure(EmbeddingModelId, MentorModelKind.Embedding);

        void Ensure(string? id, MentorModelKind kind)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            _models.GetOrAdd(id, _ => new ModelBucket()).Kind = kind;
        }
    }

    public MentorMetricsSnapshot GetSnapshot()
    {
        // Guarantee the configured models are present even if a store restore (Seed) cleared the map
        // after startup registration — so a never-used strong model still shows a (zero) row (#9/#11).
        EnsureConfiguredModels();

        var cutoff = ToBucketKey(DateTimeOffset.UtcNow - _options.MetricsRetention);
        var pricing = _options.ModelPricing;

        var models = new List<MentorModelUsage>(_models.Count);
        foreach (var (id, b) in _models)
        {
            // Prune buckets older than the retention window.
            foreach (var key in b.Series.Keys)
                if (key < cutoff) b.Series.TryRemove(key, out _);

            var series = b.Series
                .OrderBy(kv => kv.Key)
                .Select(kv => new MentorUsagePoint
                {
                    Timestamp    = FromBucketKey(kv.Key),
                    InputTokens  = Interlocked.Read(ref kv.Value.Input),
                    OutputTokens = Interlocked.Read(ref kv.Value.Output),
                    RequestCount = Interlocked.Read(ref kv.Value.Requests),
                    AvgLatencyMs = kv.Value.LatencyCount > 0
                        ? (double)Interlocked.Read(ref kv.Value.LatencySumMs) / Interlocked.Read(ref kv.Value.LatencyCount)
                        : null,
                })
                .ToList();

            long input  = Interlocked.Read(ref b.Input);
            long output = Interlocked.Read(ref b.Output);

            decimal? inCost = null, outCost = null;
            if (pricing is not null && pricing.TryGetValue(id, out var price))
            {
                inCost  = input  / 1_000_000m * price.InputPer1M;
                outCost = output / 1_000_000m * price.OutputPer1M;
            }

            long latCount = Interlocked.Read(ref b.LatencyCount);
            long ttfbCount = Interlocked.Read(ref b.TtfbCount);

            models.Add(new MentorModelUsage
            {
                ModelId              = id,
                Kind                 = b.Kind,
                InputTokens          = input,
                OutputTokens         = output,
                RequestCount         = Interlocked.Read(ref b.Requests),
                RequestsWithoutUsage = Interlocked.Read(ref b.RequestsWithoutUsage),
                AvgLatencyMs         = latCount  > 0 ? (double)Interlocked.Read(ref b.LatencySumMs) / latCount  : null,
                AvgTimeToFirstByteMs = ttfbCount > 0 ? (double)Interlocked.Read(ref b.TtfbSumMs) / ttfbCount : null,
                InputCost            = inCost,
                OutputCost           = outCost,
                Series               = series,
            });
        }

        // Order for stable display: cheap, strong, embedding, then anything else.
        models.Sort((x, y) => x.Kind.CompareTo(y.Kind));

        // Aggregate fields (sum across all models) preserve the legacy top-line snapshot shape.
        long totalIn  = models.Sum(m => m.InputTokens);
        long totalOut = models.Sum(m => m.OutputTokens);
        long requests = models.Sum(m => m.RequestCount);

        decimal? aggIn  = models.Any(m => m.InputCost  is not null) ? models.Sum(m => m.InputCost  ?? 0) : null;
        decimal? aggOut = models.Any(m => m.OutputCost is not null) ? models.Sum(m => m.OutputCost ?? 0) : null;

        var topActions = _actions
            .Select(kv => new MentorActionCount(kv.Key, kv.Value))
            .OrderByDescending(a => a.Count)
            .Take(10)
            .ToList();

        return new MentorMetricsSnapshot
        {
            TotalInputTokens    = totalIn,
            TotalOutputTokens   = totalOut,
            RequestCount        = requests,
            TurnCount           = Interlocked.Read(ref _turns),
            ServedCount         = Interlocked.Read(ref _served),
            ErrorCount          = Interlocked.Read(ref _errors),
            GuardrailBlockCount = Interlocked.Read(ref _guardrailBlocks),
            ModelId             = ModelId,
            InputCost           = aggIn,
            OutputCost          = aggOut,
            TopActions          = topActions,
            Models              = models,
            SeriesBucketMinutes = BucketMinutes,
            GeneratedAt         = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// Restores counters and per-model buckets from a persisted snapshot (roadmap #9 durable dashboard).
    /// Called once at startup by the persistence hosted service so totals continue across a restart.
    /// </summary>
    public void Seed(MentorMetricsSnapshot snapshot)
    {
        _tokens.Seed(snapshot.TotalInputTokens, snapshot.TotalOutputTokens, snapshot.RequestCount);
        Interlocked.Exchange(ref _turns, snapshot.TurnCount);
        Interlocked.Exchange(ref _served, snapshot.ServedCount);
        Interlocked.Exchange(ref _errors, snapshot.ErrorCount);
        Interlocked.Exchange(ref _guardrailBlocks, snapshot.GuardrailBlockCount);

        _actions.Clear();
        foreach (var a in snapshot.TopActions)
            _actions[a.Action] = a.Count;

        _models.Clear();
        foreach (var m in snapshot.Models)
        {
            var b = new ModelBucket
            {
                Kind    = m.Kind,
                Input   = m.InputTokens,
                Output  = m.OutputTokens,
                Requests = m.RequestCount,
            };
            foreach (var pt in m.Series)
                b.Series[ToBucketKey(pt.Timestamp)] = new Point
                {
                    Input    = pt.InputTokens,
                    Output   = pt.OutputTokens,
                    Requests = pt.RequestCount,
                };
            _models[m.ModelId] = b;
        }

        if (snapshot.ModelId is { } model)
            ModelId ??= model;
    }

    public void Reset()
    {
        _tokens.Reset();
        Interlocked.Exchange(ref _turns, 0);
        Interlocked.Exchange(ref _served, 0);
        Interlocked.Exchange(ref _errors, 0);
        Interlocked.Exchange(ref _guardrailBlocks, 0);
        _actions.Clear();
        _models.Clear();
    }

    // Hourly bucket key = whole hours since the Unix epoch (UTC), so buckets align across models.
    private static long ToBucketKey(DateTimeOffset when) => when.ToUnixTimeSeconds() / (BucketMinutes * 60);
    private static DateTimeOffset FromBucketKey(long key) => DateTimeOffset.FromUnixTimeSeconds(key * BucketMinutes * 60);

    private sealed class ModelBucket
    {
        public long Input;
        public long Output;
        public long Requests;
        public long LatencySumMs;
        public long LatencyCount;
        public long TtfbSumMs;
        public long TtfbCount;

        /// <summary>Requests that reached the provider but never reported usage — see BUG-032.</summary>
        public long RequestsWithoutUsage;

        public MentorModelKind Kind;
        public readonly ConcurrentDictionary<long, Point> Series = new();
    }

    private sealed class Point
    {
        public long Input;
        public long Output;
        public long Requests;
        public long LatencySumMs;
        public long LatencyCount;
    }
}
