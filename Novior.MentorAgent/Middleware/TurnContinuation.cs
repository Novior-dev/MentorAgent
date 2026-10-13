namespace Novior.MentorAgent.Middleware;

/// <summary>
/// Tells apart the <b>first</b> request of a user turn from the follow-up requests the
/// function-invoking chat client makes inside the same turn to deliver tool results.
///
/// <para>
/// <b>Why any of this matters.</b> A middleware that decides something per request — which model
/// serves it (<see cref="RoutingChatClient"/>), which tools are declared
/// (<see cref="ToolFilterChatClient"/>) — must decide <i>once per turn</i>, not once per request.
/// Re-deciding on the follow-up changes the request the provider is asked to continue, and the
/// provider rejects it: <c>HTTP 400 — No tool call found for function call output with call_id …</c>.
/// </para>
///
/// <para>
/// The failure only shows up with <c>MentorOptions.UseServiceManagedHistory</c> (the Responses API
/// path that hosted tools need). With a local <c>ChatHistoryProvider</c> the follow-up carries the
/// whole conversation, so the same decision happens to come out again by accident; with
/// service-managed history it carries only the tool result, and the decision flips.
/// </para>
/// </summary>
internal static class TurnContinuation
{
    /// <summary>
    /// True when this request continues a turn already in progress — the function-invoking chat
    /// client returning tool results, or a human-approval answer (#16).
    /// </summary>
    public static bool IsToolResult(IReadOnlyList<ChatMessage> messages)
    {
        if (messages.Count == 0) return false;

        // Only the tail matters: with service-managed history the client sends just the new
        // messages, with local history it sends the whole conversation — either way the
        // continuation marker is in the final message.
        foreach (var content in messages[^1].Contents)
            if (content is FunctionResultContent or ToolApprovalResponseContent)
                return true;

        return false;
    }
}
