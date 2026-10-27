namespace Novior.MentorAgent.Abstractions.Interfaces;

/// <summary>
/// The application's actions as MentorAgent sees them: every <c>[MentorAction]</c> of the coordinator, of the
/// specialists and of the team members, with who may run it, whether it asks for a confirmation, and where it is
/// published — for an administration page, or a check in the application's tests that nothing is published by
/// mistake. Registered by <c>AddMentorAgent()</c>; resolve it from the services.
///
/// <para>
/// It says what MentorAgent's own rules decide: the roles, the confirmation, the <c>RequiresApproval</c> predicate,
/// <c>McpCallerPrincipal</c>. An <see cref="IMentorActionPolicy"/> may still leave an action out for one user.
/// The tools of the MCP servers the application consumes, and those of an agent source, are not in it. A member
/// added to this interface in a later version comes with a default implementation.
/// </para>
/// </summary>
public interface IMentorActionCatalog
{
    /// <summary>The actions, in the order discovery finds them; decided again at every call.</summary>
    IReadOnlyList<MentorActionInfo> GetActions();
}
