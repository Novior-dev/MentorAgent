namespace Novior.MentorAgent.Abstractions.Interfaces;

/// <summary>
/// Decides, in the application's own code, whether a user may use one of its actions at all, and whether one call
/// may run: a permission per company or per record, a rule on the arguments, a check against the application's
/// data. For the application's actions only, on every surface: the coordinator's, a specialist's, a team member's,
/// an agent source's, the tools of the MCP servers the application consumes, and the MCP server it serves. Never
/// for MentorAgent's own tools (<c>navigate_to</c>, <c>remember</c>, <c>forget_all</c>, <c>load_skill</c>,
/// <c>read_skill_resource</c>, <c>route_to_specialist</c>, a team's tool, <c>invoke_ui_action</c>).
///
/// <para>
/// Register it with <c>AddMentorActionPolicy&lt;T&gt;()</c>, or as a singleton <see cref="IMentorActionPolicy"/> of
/// your own; every policy registered is asked, in the order of registration. It is a singleton: what it needs from
/// the conversation's scope it resolves from <see cref="MentorActionRequest.Services"/>.
/// </para>
///
/// <para>
/// A call is decided in this order: the roles of the action, <see cref="IsAllowedAsync"/>,
/// <see cref="BeforeActionAsync"/>, the confirmation, then the action. A policy that does not answer within
/// <c>MentorOptions.ActionPolicyTimeout</c>, or throws, stops the call; when MentorAgent decides what to offer,
/// it leaves the action out. Its exception's message is never logged, only its type.
/// </para>
///
/// <para>
/// Both methods have a default body, so a policy writes only the one it needs. A member added to this interface
/// in a later version comes with a default implementation.
/// </para>
/// </summary>
public interface IMentorActionPolicy
{
    /// <summary>
    /// Whether this user may use this action at all. Asked when MentorAgent decides what to offer — the
    /// coordinator's tools for a turn, the MCP server's <c>tools/list</c> — with
    /// <see cref="MentorActionRequest.Stage"/> <see cref="MentorActionStage.Offer"/> and no
    /// arguments; and before every call, with <see cref="MentorActionStage.Call"/> and the arguments, again
    /// after the user confirmed. <see langword="false"/>: the action is not offered, and a call to it does not run.
    /// Asked for every action offered on every turn: keep it fast, for example with a cache per user.
    /// </summary>
    /// <param name="request">The action, the user, and where the call comes from.</param>
    /// <param name="cancellationToken">Cancelled when the policy takes longer than <c>ActionPolicyTimeout</c>, and with the turn.</param>
    ValueTask<bool> IsAllowedAsync(MentorActionRequest request, CancellationToken cancellationToken)
        => ValueTask.FromResult(true);

    /// <summary>
    /// Before every call, once the roles and <see cref="IsAllowedAsync"/> have let it through, and before the
    /// confirmation: <see cref="MentorActionVerdict.Proceed"/> has no objection,
    /// <see cref="MentorActionVerdict.Confirm"/> asks the user first, <see cref="MentorActionVerdict.Stop"/>
    /// stops the call with a reason the model reads. Every policy is asked, and the strictest answer wins: Stop,
    /// then Confirm, then Proceed. Proceed never removes a confirmation the action asks for. Where nobody can
    /// confirm — the MCP server, a task from another agent — Confirm refuses the call.
    /// </summary>
    /// <param name="request">The call: the action, its arguments, the user, and where it comes from.</param>
    /// <param name="cancellationToken">Cancelled when the policy takes longer than <c>ActionPolicyTimeout</c>, and with the turn.</param>
    ValueTask<MentorActionVerdict> BeforeActionAsync(MentorActionRequest request, CancellationToken cancellationToken)
        => ValueTask.FromResult(MentorActionVerdict.Proceed);
}
