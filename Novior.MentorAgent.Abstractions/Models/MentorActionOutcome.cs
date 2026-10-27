namespace Novior.MentorAgent.Abstractions.Models;

/// <summary>
/// The decisions of the tool gate. A closed list: a new value comes only in a minor version, and the CHANGELOG
/// says so.
///
/// <para>
/// A call goes through them in one of these orders, in Blocking and in Native mode alike:
/// <see cref="Allowed"/> then <see cref="Executed"/>, <see cref="Failed"/> or <see cref="Stopped"/>; or
/// <see cref="Allowed"/>, <see cref="ConfirmationRequested"/>, then <see cref="Rejected"/>, <see cref="Stopped"/>,
/// or <see cref="Confirmed"/> followed by the outcome of the run (or <see cref="Denied"/>, for a role or a
/// permission taken away while the confirmation waited); or <see cref="Denied"/>, <see cref="Blocked"/> or
/// <see cref="PolicyFailed"/> alone. <see cref="Allowed"/> comes once the roles and every
/// <c>IMentorActionPolicy</c> have let the call through.
/// </para>
/// </summary>
public enum MentorActionOutcome
{
    /// <summary>
    /// The roles and every <c>IMentorActionPolicy</c> allow the call. The confirmation, when the action or a policy
    /// asks for one, comes next.
    /// </summary>
    Allowed = 0,

    /// <summary>
    /// Refused: a role is missing, nobody is signed in, a policy does not allow the user this action, nobody can
    /// confirm, or the MCP server does not serve it.
    /// </summary>
    Denied = 1,

    /// <summary>The user is asked to confirm.</summary>
    ConfirmationRequested = 2,

    /// <summary>The user confirmed.</summary>
    Confirmed = 3,

    /// <summary>The user said no, or nobody answered within <c>ConfirmationTimeout</c>.</summary>
    Rejected = 4,

    /// <summary>
    /// Stopped before it finished: Stop, the end of the session, or the end of the turn. A Stop that comes after the
    /// confirmation and before the action starts stops it too.
    /// </summary>
    Stopped = 5,

    /// <summary>
    /// Stopped by an <c>IMentorActionPolicy</c> before it ran: its reason is in <see cref="MentorActionDecision.Detail"/>.
    /// </summary>
    Blocked = 6,

    /// <summary>
    /// Not run: an <c>IMentorActionPolicy</c> threw, or did not answer within <c>ActionPolicyTimeout</c>. The
    /// exception's type is in <see cref="MentorActionDecision.ExceptionType"/>.
    /// </summary>
    PolicyFailed = 7,

    /// <summary>The action ran.</summary>
    Executed = 8,

    /// <summary>The action threw.</summary>
    Failed = 9,
}
