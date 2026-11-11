namespace Novior.MentorAgent.Abstractions.Interfaces;

/// <summary>
/// Governs the application's catalogue of actions from outside its code: whether each action is on, the roles it
/// requires, whether the user confirms it, and the description the model reads. The code says what it says —
/// <c>[MentorAction(Enabled = false)]</c> ships an action off, <c>RequiredRoles</c> names who may run it,
/// <c>RequiresConfirmation</c> asks first, <c>Description</c> tells the model what it does — and the governor may
/// answer otherwise, for every session. For the application's own actions only, those <c>IMentorActionCatalog</c> lists: not the tools
/// of the MCP servers the application consumes, not an agent source's own, never MentorAgent's.
///
/// <para>
/// <b>Registering a governor gives whoever implements it the power to widen what the code allows</b>: an action the
/// code ships off runs once the governor turns it on, one that requires a role runs for anybody once the governor
/// requires none, and one the code confirms runs without asking once the governor takes its confirmation away. What may
/// be widened, when and with whose approval is the governor's to check; MentorAgent applies
/// its answer.
/// </para>
///
/// <para>
/// Register it with <c>AddMentorActionGovernor&lt;T&gt;()</c>. There is one per application: a second registration
/// throws. It is a singleton: what it needs from the conversation's scope it resolves from
/// <see cref="Models.MentorActionRequest.Services"/>.
/// </para>
///
/// <para>
/// It is asked with <see cref="Models.MentorActionStage.Offer"/> when a session builds its tools — for the
/// coordinator, the specialists and the team members, once for the session — and on every <c>tools/list</c> of the
/// MCP server; and with <see cref="Models.MentorActionStage.Call"/> before every call, ahead of the roles, the
/// policies and the confirmation. <see cref="Models.MentorActionRequest.SessionId"/> names the session every time,
/// so an answer can stay the same for a whole session; on the MCP server without a session it is
/// <see langword="null"/>: answer with what applies now. The tools a session was built with stay for that session;
/// an action turned off meanwhile is refused at the call.
/// </para>
///
/// <para>
/// A governor that does not answer within <c>MentorOptions.ActionPolicyTimeout</c>, or throws, stops the action: it
/// is not offered and the call does not run, with the reason <see cref="Models.MentorActionReasons.GovernorFailed"/>.
/// Going back to the code instead would undo what the governor narrowed. Its exception's message is never logged,
/// only its type. The A2A agent card is built once for every caller and follows the code only.
/// </para>
///
/// <para>
/// Every member has a default body that leaves the code's value, so a governor writes only what it governs. A
/// member added to this interface in a later version comes with a default implementation.
/// </para>
/// </summary>
public interface IMentorActionGovernor
{
    /// <summary>
    /// Whether the action is on. <see langword="null"/>: what the code says — on, unless the action is declared off
    /// (<c>[MentorAction(Enabled = false)]</c>). <see langword="false"/>: not offered on any surface, and a call to it
    /// does not run (<see cref="Models.MentorActionReasons.Disabled"/>). <see langword="true"/>: on, even if the code
    /// ships it off.
    /// <para>
    /// Turning an action off takes effect at once for its calls; turning one on counts from the next session, which
    /// builds its tools with it. On <c>/mentor/chat</c> the session is the request.
    /// </para>
    /// </summary>
    /// <param name="request">The action, the user, the session, and where it is offered or called.</param>
    /// <param name="cancellationToken">Cancelled when the governor takes longer than <c>ActionPolicyTimeout</c>, and with the turn.</param>
    ValueTask<bool?> IsEnabledAsync(Models.MentorActionRequest request, CancellationToken cancellationToken)
        => ValueTask.FromResult<bool?>(null);

    /// <summary>
    /// The roles the action requires, in place of the code's (<c>[MentorAction(RequiredRoles = …)]</c>), which
    /// <see cref="Models.MentorActionRequest.RequiredRoles"/> carries. <see langword="null"/>: the code's roles. An
    /// empty list: no role is required, so anybody may run the action, signed in or not. A list: the user needs any one
    /// of these roles, tested as the code's are. Asked only for an action that is on.
    /// <para>
    /// The answer decides everything the roles decide: whom the action is offered to on each turn, the check before a
    /// confirmation and again after it, and which MCP callers see it — on the MCP server an action that requires roles
    /// is published only with <c>McpCallerPrincipal</c>. The decisions say where the roles came from
    /// (<see cref="Models.MentorActionDecision.RolesFromOutside"/>). As for <see cref="IsEnabledAsync"/>, the roles a
    /// session offers by are decided as it builds its tools, and every call is checked against the answer at the call.
    /// The A2A agent card and <c>IMentorActionCatalog</c> say what the code says.
    /// </para>
    /// </summary>
    /// <param name="request">The action, with the code's roles, the user, the session, and where it is offered or called.</param>
    /// <param name="cancellationToken">Cancelled when the governor takes longer than <c>ActionPolicyTimeout</c>, and with the turn.</param>
    ValueTask<IReadOnlyList<string>?> GetRequiredRolesAsync(Models.MentorActionRequest request, CancellationToken cancellationToken)
        => ValueTask.FromResult<IReadOnlyList<string>?>(null);

    /// <summary>
    /// Whether the user confirms the action before it runs, in place of the code's confirmation, which
    /// <see cref="Models.MentorActionRequest.RequiresConfirmation"/> carries. <see cref="Models.MentorConfirmationChoice.Code"/>:
    /// what the code says. <see cref="Models.MentorConfirmationChoice.Required"/>: the user is asked, even if the code
    /// does not ask. <see cref="Models.MentorConfirmationChoice.None"/>: the user is not asked, even if the code asks —
    /// declared, by default for writes, or by the <c>RequiresApproval</c> predicate. Asked only for an action that is on,
    /// after its roles.
    /// <para>
    /// Only the code's confirmation is the governor's to decide: an <see cref="IMentorActionPolicy"/> that answers
    /// <see cref="Models.MentorActionVerdict.Confirm"/> still asks, a hosted MCP server's approval still comes from its
    /// provider, and with <c>RequireConfirmation</c> off nobody is asked. In Native mode
    /// <see cref="Models.MentorConfirmationChoice.Required"/> is asked even when an auto-approval rule would let the call
    /// through: those rules are the code's. An action whose confirmation is taken away still counts as one that changes
    /// data: tool filtering never offers it alone, and a model is never made to call it.
    /// </para>
    /// <para>
    /// Narrowing takes effect at once: a confirmation added counts from the call, as an action turned off does. Widening
    /// counts from the next session: a confirmation taken away, as an action turned on. A call is confirmed when the
    /// session's answer or the call's asks for it. On the MCP server, where nobody can confirm, an action that asks is not
    /// published, and one whose confirmation the governor takes away is. The decisions say when the governor changed
    /// whether a call is confirmed (<see cref="Models.MentorActionDecision.ConfirmationFromOutside"/>). The A2A agent
    /// card and <c>IMentorActionCatalog</c> say what the code says.
    /// </para>
    /// </summary>
    /// <param name="request">The action, with the code's confirmation, the user, the session, and where it is offered or called.</param>
    /// <param name="cancellationToken">Cancelled when the governor takes longer than <c>ActionPolicyTimeout</c>, and with the turn.</param>
    ValueTask<Models.MentorConfirmationChoice> GetConfirmationAsync(Models.MentorActionRequest request, CancellationToken cancellationToken)
        => ValueTask.FromResult(Models.MentorConfirmationChoice.Code);

    /// <summary>
    /// The description the model reads for the action, in place of the code's (<c>[MentorAction(Description = …)]</c>
    /// or <c>[Description]</c>), which <see cref="Models.MentorActionRequest.Description"/> carries.
    /// <see langword="null"/>, or a blank text: the code's. The action's name and the schema of its arguments never
    /// change, and the code's <c>ProactiveHint</c> still follows the description. Asked only for an action that is on,
    /// after its confirmation, as a session builds its tools and on every <c>tools/list</c> of the MCP server — never
    /// at a call.
    /// <para>
    /// The answer is what the model reads: the tools of the coordinator, the specialists and the team members, the
    /// texts tool filtering ranks, the classifiers, and the MCP server's <c>tools/list</c>. What the user sees stays
    /// the programmer's: the confirmation banner, the bar of the running action and the decisions'
    /// <see cref="Models.MentorActionDecision.DisplayName"/> keep the code's description, so a description written
    /// outside the code, for example proposed by a console, speaks to the model and never changes what a user is asked
    /// to confirm. The policies are asked with the description the model reads, except on a call to the MCP server,
    /// which has no session and is asked with the code's. The A2A agent card, <c>IMentorActionCatalog</c> and the
    /// onboarding tour say what the code says.
    /// </para>
    /// <para>
    /// The text reaches the model as the code's does: whoever writes it writes part of the model's instructions about
    /// the action.
    /// </para>
    /// </summary>
    /// <param name="request">The action, with the code's description, the user, the session, and where it is offered.</param>
    /// <param name="cancellationToken">Cancelled when the governor takes longer than <c>ActionPolicyTimeout</c>, and with the turn.</param>
    ValueTask<string?> GetDescriptionAsync(Models.MentorActionRequest request, CancellationToken cancellationToken)
        => ValueTask.FromResult<string?>(null);
}
