namespace Novior.MentorAgent.Rag;

/// <summary>
/// Why the conversation of a session changed, as
/// <see cref="IMentorRagSource.OnConversationChangedAsync(MentorConversationChange, CancellationToken)"/> is told.
/// </summary>
public enum MentorConversationChangeReason
{
    /// <summary>The user started a new conversation: <c>IMentorOrchestrator.ResetSessionAsync</c>, the widget's New conversation.</summary>
    Reset = 0,

    /// <summary>A saved conversation was restored in its place (<c>RestoreSessionAsync</c>).</summary>
    Restored = 1,

    /// <summary>
    /// A restored conversation the service no longer knows was replaced by a new one, on its first turn (BUG-110). That
    /// turn ends with the user told; the next one is the new conversation's first.
    /// </summary>
    Replaced = 2,
}

/// <summary>
/// The conversation of a session changed: what came before is no longer the conversation. A source that keeps state
/// for a conversation — what it showed, a running filter, a cursor — clears it.
///
/// <para>
/// A class with init-only properties rather than a parameter list, so that what it says can grow without breaking a
/// source that already implements the method.
/// </para>
/// </summary>
public sealed class MentorConversationChange
{
    /// <summary>Why it changed.</summary>
    public required MentorConversationChangeReason Reason { get; init; }

    /// <summary>
    /// The session whose conversation changed, the same id the action decisions carry; <see langword="null"/> where a host
    /// has none. A singleton source keys its state on it; a scoped one is already the session's.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>The session's tenant, as <see cref="MentorRagRequest.TenantId"/>; <see langword="null"/> for a single tenant.</summary>
    public string? TenantId { get; init; }
}
