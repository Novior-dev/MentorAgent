namespace Novior.MentorAgent.Options;

/// <summary>
/// How MentorAgent implements human-in-the-loop tool approval (roadmap #16).
/// Both modes show the <b>same</b> confirmation banner and use the <b>same</b> client protocol
/// (<c>ConfirmationRequired</c> → <c>RespondToApproval</c>), so switching mode never requires a
/// client change.
/// </summary>
public enum MentorHitlMode
{
    /// <summary>
    /// MentorAgent's own flow (default). The tool invocation is intercepted inside
    /// <c>MentorAgentUIMiddleware</c> and the AI thread is parked on a
    /// <see cref="System.Threading.Tasks.TaskCompletionSource{TResult}"/> until the user answers,
    /// so the agent run — and its streaming — stays alive for the whole turn.
    /// Fewer moving parts and one model round-trip; use it unless you need AF interop.
    /// </summary>
    Blocking = 0,

    /// <summary>
    /// The Agent Framework standard flow. Tools that need approval are wrapped in
    /// <c>ApprovalRequiredAIFunction</c>; the framework's function-invoking chat client stops before
    /// invoking them and returns a <c>ToolApprovalRequestContent</c>, and MentorAgent resumes the run
    /// by sending back a <c>ToolApprovalResponseContent</c> on the same session.
    ///
    /// <para>
    /// Choose this for <b>interop</b>: an AF workflow, DevUI or any AF-native host that consumes the
    /// agent sees the standard approval protocol instead of MentorAgent's internal one. It costs one
    /// extra model round-trip per approved call, and the reply arrives in two streamed segments
    /// (before and after the banner).
    /// </para>
    /// </summary>
    Native = 1,
}
