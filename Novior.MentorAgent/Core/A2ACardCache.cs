using System.Collections.Concurrent;

namespace Novior.MentorAgent.Core;

/// <summary>
/// Process-wide cache for remote A2A agent cards, with a TTL and a backoff for the ones that do not
/// answer.
///
/// <para>
/// A card is metadata about a remote agent: its name, what it can do, where to send tasks. It is the
/// same for every visitor, and it was fetched again for each of them — one HTTP round trip per
/// browser circuit before the first message could be answered, and, when the agent was down, one
/// <b>full stack trace per visitor</b> in the log while every one of them waited out the connect
/// timeout.
/// </para>
///
/// <para>
/// Only the card is shared. The <c>A2AChatClient</c> built from it stays per session on purpose: it
/// carries the remote conversation's context id, and sharing that would splice two users'
/// conversations into one — the shape of BUG-055.
/// </para>
/// </summary>
internal sealed class A2ACardCache(ILogger<A2ACardCache> logger, TimeProvider? time = null)
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>How long a fetched card is reused before it is fetched again.</summary>
    internal TimeSpan Ttl { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>How long a failure is remembered before another visitor pays for a retry.</summary>
    internal TimeSpan FailureBackoff { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Fetches this process actually performed — for tests.</summary>
    internal int Fetches;

    /// <summary>
    /// Returns the card for <paramref name="key"/>, fetching it only when this process has never
    /// had it, when the TTL expired, or when the backoff on a previous failure has run out.
    /// Rethrows the cached failure inside the backoff window, without a second round trip and
    /// without a second stack trace in the log.
    /// </summary>
    internal async Task<A2A.AgentCard> GetAsync(
        string key, string agentName, string url, Func<CancellationToken, Task<A2A.AgentCard>> fetch,
        CancellationToken ct)
    {
        var now = _time.GetUtcNow();

        if (_entries.TryGetValue(key, out var cached))
        {
            if (cached.Card is { } card && now - cached.At < Ttl) return card;
            if (cached.Failure is { } failure && now - cached.At < cached.Backoff) throw failure;
        }

        try
        {
            Interlocked.Increment(ref Fetches);
            var card = await fetch(ct);
            _entries[key] = new Entry(card, null, now, TimeSpan.Zero);
            return card;
        }
        catch (Exception ex)
        {
            // Growing backoff, so a permanently dead agent is dialled once a minute rather than once
            // per visitor — and said once, as a message. The stack trace is Debug: the first line
            // already names the agent and the URL, which is what the reader can act on.
            var backoff = cached?.Failure is not null
                ? TimeSpan.FromTicks(Math.Min(cached.Backoff.Ticks * 2, TimeSpan.FromMinutes(5).Ticks))
                : FailureBackoff;

            _entries[key] = new Entry(null, ex, now, backoff);

            logger.LogWarning(
                "[MentorAgent:A2A] Remote agent '{Name}' at {Url} did not answer: {Reason}. " +
                "Not retried for {Backoff}.",
                agentName, url, ex.Message, backoff);
            logger.LogDebug(ex, "[MentorAgent:A2A] Agent card fetch for '{Name}' failed.", agentName);

            throw;
        }
    }

    private sealed record Entry(A2A.AgentCard? Card, Exception? Failure, DateTimeOffset At, TimeSpan Backoff);
}
