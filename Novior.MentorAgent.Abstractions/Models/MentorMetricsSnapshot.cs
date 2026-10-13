namespace Novior.MentorAgent.Abstractions.Models;

/// <summary>How many times a given tool/action was invoked (for the "top actions" list).</summary>
public sealed record MentorActionCount(string Action, long Count);

/// <summary>
/// A point-in-time view of MentorAgent usage for the admin token &amp; cost dashboard (roadmap #9).
/// Produced by <c>IMentorMetrics.GetSnapshot()</c>. Process-wide (single instance); for
/// multi-instance production aggregation use OpenTelemetry (roadmap #8).
/// </summary>
public sealed record MentorMetricsSnapshot
{
    /// <summary>Cumulative input (prompt) tokens.</summary>
    public long TotalInputTokens { get; init; }

    /// <summary>Cumulative output (completion) tokens.</summary>
    public long TotalOutputTokens { get; init; }

    /// <summary>Cumulative total tokens.</summary>
    public long TotalTokens => TotalInputTokens + TotalOutputTokens;

    /// <summary>Model round-trips recorded.</summary>
    public long RequestCount { get; init; }

    /// <summary>User messages (turns) received.</summary>
    public long TurnCount { get; init; }

    /// <summary>Turns completed without an error or guardrail block — the deflection proxy.</summary>
    public long ServedCount { get; init; }

    /// <summary>Turns that ended in an error.</summary>
    public long ErrorCount { get; init; }

    /// <summary>
    /// Messages blocked by the input guardrail — the safety check, a custom <c>InputGuardrail</c>, and
    /// <c>RefuseOutOfScope</c> refusals — plus replies withheld by the output check.
    /// </summary>
    public long GuardrailBlockCount { get; init; }

    /// <summary>Share of turns served without error/guardrail block (0–1).</summary>
    public double DeflectionRate => TurnCount == 0 ? 0 : (double)ServedCount / TurnCount;

    /// <summary>Primary chat model id used for pricing (<see langword="null"/> if unknown).</summary>
    public string? ModelId { get; init; }

    /// <summary>Estimated input cost — <see langword="null"/> when no pricing is configured for the model.</summary>
    public decimal? InputCost { get; init; }

    /// <summary>Estimated output cost — <see langword="null"/> when no pricing is configured.</summary>
    public decimal? OutputCost { get; init; }

    /// <summary>Estimated total cost, or <see langword="null"/> when pricing is not available.</summary>
    public decimal? TotalCost => InputCost is { } i && OutputCost is { } o ? i + o : null;

    /// <summary>Most-invoked tools/actions, highest first.</summary>
    public IReadOnlyList<MentorActionCount> TopActions { get; init; } = [];

    /// <summary>
    /// Per-model usage, cost and time series (roadmap #9) — typically the cheap chat model, the strong
    /// routing model and the embedding model. Empty on legacy snapshots; the aggregate fields above are
    /// the sum across these entries.
    /// </summary>
    public IReadOnlyList<MentorModelUsage> Models { get; init; } = [];

    /// <summary>Width of each <see cref="MentorUsagePoint"/> time-series bucket, in minutes (default 60).</summary>
    public int SeriesBucketMinutes { get; init; } = 60;

    /// <summary>When this snapshot was produced (UTC).</summary>
    public DateTimeOffset GeneratedAt { get; init; }
}
