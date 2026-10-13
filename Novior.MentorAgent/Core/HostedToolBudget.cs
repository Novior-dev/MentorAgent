namespace Novior.MentorAgent.Core;

/// <summary>
/// Counts how many provider-hosted tool calls a session has made, so a cap can stop declaring them
/// once the budget is spent (roadmap #15).
///
/// <para>
/// <b>Why a hard cap alongside the semantic filter.</b> Relevance scoring is probabilistic: it lowers
/// how often an expensive tool fires on a message that did not need it, but it cannot promise a
/// ceiling. A counter can. The two are complementary — the filter reduces the frequency, the cap
/// bounds the worst case, and only the second one survives a message the embedding scores wrongly.
/// </para>
///
/// <para>
/// One instance per orchestrator, i.e. per user session (Blazor circuit or hub connection), which is
/// also the unit a budget should be expressed in. Interlocked because the count is written from the
/// streaming read loop and read from the request pipeline.
/// </para>
/// </summary>
internal sealed class HostedToolBudget
{
    private int _used;

    /// <summary>Hosted tool calls observed in this session.</summary>
    public int Used => Volatile.Read(ref _used);

    /// <summary>Records one hosted tool call. Called once per call id by the activity reporter.</summary>
    public void Record() => Interlocked.Increment(ref _used);

    /// <summary>
    /// True when <paramref name="max"/> is a real limit and the session has reached it.
    /// A non-positive <paramref name="max"/> means "no cap".
    /// </summary>
    public bool IsExhausted(int max) => max > 0 && Used >= max;
}
