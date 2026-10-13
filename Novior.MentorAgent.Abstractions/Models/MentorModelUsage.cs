namespace Novior.MentorAgent.Abstractions.Models;

/// <summary>Which configured model produced a usage record (dashboard #9 per-model breakdown).</summary>
public enum MentorModelKind
{
    /// <summary>The primary, cheap chat model (<c>MentorOptions.ChatClient</c>).</summary>
    Cheap = 0,

    /// <summary>The strong chat model used by routing (<c>MentorOptions.StrongChatClient</c>).</summary>
    Strong,

    /// <summary>The embedding model (<c>MentorOptions.EmbeddingGenerator</c>).</summary>
    Embedding,

    /// <summary>Any other / unclassified model id.</summary>
    Other,
}

/// <summary>
/// One time-bucket of usage, powering the dashboard's temporal charts (roadmap #9). Buckets are of
/// fixed width (<see cref="MentorMetricsSnapshot.SeriesBucketMinutes"/>) and aligned to wall-clock so
/// they can be summed across models by <see cref="Timestamp"/>.
/// </summary>
public sealed record MentorUsagePoint
{
    /// <summary>Bucket start (UTC).</summary>
    public DateTimeOffset Timestamp { get; init; }

    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long TotalTokens => InputTokens + OutputTokens;
    public long RequestCount { get; init; }

    /// <summary>Average full-response latency (ms) in this bucket, or <see langword="null"/> if unmeasured.</summary>
    public double? AvgLatencyMs { get; init; }
}

/// <summary>
/// Per-model usage, cost and time series for the admin dashboard (roadmap #9). One entry per distinct
/// model id seen at runtime — typically the cheap chat model, the strong routing model, and the
/// embedding model. Cost is populated only when <c>MentorOptions.ModelPricing</c> has an entry for
/// <see cref="ModelId"/>.
/// </summary>
public sealed record MentorModelUsage
{
    public required string ModelId { get; init; }
    public MentorModelKind Kind { get; init; }

    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long TotalTokens => InputTokens + OutputTokens;
    public long RequestCount { get; init; }

    /// <summary>
    /// How many of <see cref="RequestCount"/> reached the provider but reported no token usage —
    /// in practice, turns the user stopped mid-stream.
    ///
    /// <para>
    /// Providers send usage in the final streaming update, so a cancelled stream never delivers it.
    /// Those calls are still billed, and billed for the entire input prompt, so omitting them
    /// understates spend; but their token counts are genuinely unknown and inventing a number —
    /// zero included — would quietly drag every per-request average down. They are therefore counted
    /// as requests, contribute nothing to the token totals, and are disclosed here (BUG-032).
    /// </para>
    /// </summary>
    public long RequestsWithoutUsage { get; init; }

    /// <summary>Average full-response latency (ms) across all calls, or <see langword="null"/>.</summary>
    public double? AvgLatencyMs { get; init; }

    /// <summary>Average time-to-first-byte (ms) for streaming calls, or <see langword="null"/>.</summary>
    public double? AvgTimeToFirstByteMs { get; init; }

    /// <summary>Estimated input cost, or <see langword="null"/> when no pricing is configured for this model.</summary>
    public decimal? InputCost { get; init; }

    /// <summary>Estimated output cost, or <see langword="null"/> when no pricing is configured.</summary>
    public decimal? OutputCost { get; init; }

    /// <summary>Estimated total cost, or <see langword="null"/> when pricing is not available.</summary>
    public decimal? TotalCost => InputCost is { } i && OutputCost is { } o ? i + o : null;

    /// <summary>Time-series buckets, oldest first.</summary>
    public IReadOnlyList<MentorUsagePoint> Series { get; init; } = [];
}
