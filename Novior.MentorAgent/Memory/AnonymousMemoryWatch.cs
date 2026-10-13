namespace Novior.MentorAgent.Memory;

/// <summary>
/// Says, once per process, that a second unauthenticated session has reached the shared memory
/// bucket (BUG-064).
///
/// <para>
/// Every session without a signed-in user resolves to the memory key <c>"anonymous"</c>. In a
/// single-user host that is exactly right; on a website it means a fact one visitor states is
/// recalled for the next. Nothing at startup can tell the two apart — the widget sits in a layout,
/// and whether its pages require authentication is the host's routing, not ours — so the warning
/// waits for the one observation that settles it: a <em>second</em> session using the bucket. A
/// single-user host never produces one, so it is never told something it should ignore.
/// </para>
///
/// <para>
/// Singleton by registration, so "once" means once per process; each <see cref="MentorMemoryService"/>
/// (one per circuit or connection) reports itself at most once.
/// </para>
/// </summary>
internal sealed class AnonymousMemoryWatch(ILogger<AnonymousMemoryWatch> logger)
{
    private int _sessions;

    internal void SessionSeen()
    {
        if (Interlocked.Increment(ref _sessions) != 2) return;

        logger.LogWarning(
            "[MentorAgent] A second session without a signed-in user is using memory. All such sessions " +
            "share the memory key \"anonymous\", so a fact one visitor states is recalled for the next " +
            "— and RateLimitPerUser is one allowance for all of them. Require authentication on the " +
            "pages that host the assistant, or set UseMemoryContext = false. Harmless in a single-user " +
            "application, where this is never said.");
    }
}
