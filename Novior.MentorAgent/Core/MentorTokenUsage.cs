namespace Novior.MentorAgent.Core;

/// <summary>
/// Lightweight, thread-safe accumulator of LLM token usage (Wave 1 · #1).
///
/// Registered as a singleton so usage is aggregated across the whole app lifetime.
/// This is a <b>developer/operator measurement aid</b> — it is the instrument used to
/// verify the token-reduction work; it is never surfaced to end users. A future admin
/// dashboard (roadmap #9) can read these counters; full OpenTelemetry (roadmap #8)
/// supersedes it for production observability.
/// </summary>
public sealed class MentorTokenUsage
{
    private long _totalInput;
    private long _totalOutput;
    private long _requests;
    private long _lastInput;
    private long _lastOutput;

    /// <summary>Cumulative input (prompt) tokens since process start.</summary>
    public long TotalInputTokens => Interlocked.Read(ref _totalInput);

    /// <summary>Cumulative output (completion) tokens since process start.</summary>
    public long TotalOutputTokens => Interlocked.Read(ref _totalOutput);

    /// <summary>Cumulative total tokens since process start.</summary>
    public long TotalTokens => TotalInputTokens + TotalOutputTokens;

    /// <summary>Number of model round-trips recorded.</summary>
    public long RequestCount => Interlocked.Read(ref _requests);

    /// <summary>Input tokens of the most recent recorded model call.</summary>
    public long LastInputTokens => Interlocked.Read(ref _lastInput);

    /// <summary>Output tokens of the most recent recorded model call.</summary>
    public long LastOutputTokens => Interlocked.Read(ref _lastOutput);

    /// <summary>Records one model round-trip's token usage.</summary>
    public void Record(long inputTokens, long outputTokens)
    {
        Interlocked.Add(ref _totalInput, inputTokens);
        Interlocked.Add(ref _totalOutput, outputTokens);
        Interlocked.Increment(ref _requests);
        Interlocked.Exchange(ref _lastInput, inputTokens);
        Interlocked.Exchange(ref _lastOutput, outputTokens);
    }

    /// <summary>
    /// Seeds the cumulative totals from a persisted snapshot (roadmap #9 durable dashboard), so counts
    /// continue across a restart. Overwrites the running totals; "last call" fields are left at zero.
    /// </summary>
    public void Seed(long totalInput, long totalOutput, long requestCount)
    {
        Interlocked.Exchange(ref _totalInput, totalInput);
        Interlocked.Exchange(ref _totalOutput, totalOutput);
        Interlocked.Exchange(ref _requests, requestCount);
    }

    /// <summary>Resets all counters (e.g. when starting a fresh measurement run).</summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _totalInput, 0);
        Interlocked.Exchange(ref _totalOutput, 0);
        Interlocked.Exchange(ref _requests, 0);
        Interlocked.Exchange(ref _lastInput, 0);
        Interlocked.Exchange(ref _lastOutput, 0);
    }
}
