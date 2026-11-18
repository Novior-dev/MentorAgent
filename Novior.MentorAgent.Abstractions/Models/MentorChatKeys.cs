namespace Novior.MentorAgent.Abstractions.Models;

/// <summary>
/// Keys MentorAgent reads in the <c>AdditionalProperties</c> of a chat response and of its streaming updates, that the
/// application's own <c>IChatClient</c> sets. A key stays the same from one version to the next.
/// </summary>
public static class MentorChatKeys
{
    /// <summary>
    /// The response does not come from a model: the application's chat client stopped the call before it reached one —
    /// the assistant turned off, a budget used up — and answered by itself. Set it on the <c>ChatResponse</c>, and on each
    /// <c>ChatResponseUpdate</c> when streaming; its value is the cause, an open string such as <c>ai_switch</c> or
    /// <c>budget</c>. MentorAgent asks no model about a turn none of whose requests reached one: the
    /// check of the actions a reply announced does not run. A missing or zero <c>Usage</c> says nothing of the kind: a
    /// model may not report its usage.
    /// </summary>
    public const string ModelNotCalled = "mentoragent.model_not_called";
}
