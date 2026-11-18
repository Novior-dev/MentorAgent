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

    /// <summary>
    /// The places the application declares its actions in, one per source: a class read for its attributes, the actions
    /// registered in code for one class, then one per page with the names of the UI actions it declared since the
    /// application started (<see cref="MentorActionOrigins.Page"/>, within <see cref="MentorPageActionLimits"/>). Each
    /// lists the actions it gives and what it left out, and every action of <see cref="GetActions"/> names its own with
    /// <see cref="MentorActionInfo.RegistrationId"/>. A catalogue written before this member returns none.
    /// </summary>
    IReadOnlyList<MentorActionRegistration> GetRegistrations() => [];
}
