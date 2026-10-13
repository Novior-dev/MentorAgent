using Novior.MentorAgent.Options;
using Microsoft.Extensions.Options;

namespace Novior.MentorAgent.Core;

/// <summary>
/// Rate limiter for user messages — in-memory, per session.
///
/// Algorithm: sliding window counter.
/// Each userId has a counter and the window start timestamp.
/// When the window expires, the counter resets.
///
/// Registered as Singleton — shares state across all users.
/// Thread-safe via lock.
///
/// Disabled if RateLimitPerUser = 0.
/// </summary>
public sealed class MentorRateLimiter
{
    private readonly MentorOptions _options;

    private readonly Dictionary<string, (int Count, DateTime WindowStart)> _counters = new();
    private readonly object _lock = new();

    // Lazy cleanup: run Cleanup() every N CheckAndIncrement calls so that
    // expired counters are removed without requiring a background hosted service.
    private int _callCount;
    private const int CleanupInterval = 500;

    public MentorRateLimiter(IOptions<MentorOptions> options)
        => _options = options.Value;

    /// <summary>
    /// Checks whether the user is allowed to send a message.
    /// Increments the counter if allowed.
    /// </summary>
    /// <returns>
    /// (allowed: true, remaining: N) if allowed,
    /// (allowed: false, remaining: 0) if limit reached.
    /// </returns>
    public (bool Allowed, int Remaining) CheckAndIncrement(string userId)
    {
        // Rate limiting disabilitato
        if (_options.RateLimitPerUser <= 0)
            return (true, int.MaxValue);

        var now = DateTime.UtcNow;
        var window = TimeSpan.FromSeconds(_options.RateLimitWindowSecs);
        var limit = _options.RateLimitPerUser;

        (bool Allowed, int Remaining) result;

        lock (_lock)
        {
            if (_counters.TryGetValue(userId, out var entry))
            {
                // Finestra scaduta → azzera
                if (now - entry.WindowStart >= window)
                    entry = (0, now);

                if (entry.Count >= limit)
                {
                    result = (false, 0);
                }
                else
                {
                    entry = (entry.Count + 1, entry.WindowStart);
                    _counters[userId] = entry;
                    result = (true, limit - entry.Count);
                }
            }
            else
            {
                _counters[userId] = (1, now);
                result = (true, limit - 1);
            }
        }

        // Periodic lazy cleanup: remove expired counters every CleanupInterval
        // calls so that the dictionary does not grow unbounded in long-running apps.
        if (Interlocked.Increment(ref _callCount) % CleanupInterval == 0)
            Cleanup();

        return result;
    }

    /// <summary>
    /// Returns the seconds remaining before the window resets for the given user.
    /// </summary>
    public int GetSecondsUntilReset(string userId)
    {
        var window = TimeSpan.FromSeconds(_options.RateLimitWindowSecs);
        lock (_lock)
        {
            if (!_counters.TryGetValue(userId, out var entry))
                return 0;

            var elapsed = DateTime.UtcNow - entry.WindowStart;
            var remaining = window - elapsed;
            return remaining > TimeSpan.Zero ? (int)remaining.TotalSeconds : 0;
        }
    }

    /// <summary>
    /// Cleans up expired counters to prevent memory leaks in long-running apps.
    /// Called periodically by the CleanupTask.
    /// </summary>
    internal void Cleanup()
    {
        var window = TimeSpan.FromSeconds(_options.RateLimitWindowSecs);
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            var expired = _counters
                .Where(kv => now - kv.Value.WindowStart >= window)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var key in expired)
                _counters.Remove(key);
        }
    }
}