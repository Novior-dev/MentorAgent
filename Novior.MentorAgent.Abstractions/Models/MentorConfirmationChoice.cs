namespace Novior.MentorAgent.Abstractions.Models;

/// <summary>
/// What an <see cref="Interfaces.IMentorActionGovernor"/> answers about an action's confirmation
/// (<see cref="Interfaces.IMentorActionGovernor.GetConfirmationAsync"/>).
/// </summary>
public enum MentorConfirmationChoice
{
    /// <summary>
    /// What the code says: the confirmation the action declares, the default for writes, or the
    /// <c>RequiresApproval</c> predicate.
    /// </summary>
    Code = 0,

    /// <summary>Ask the user to confirm the action, even if the code does not.</summary>
    Required = 1,

    /// <summary>
    /// Do not ask, even if the code does. Only the code's confirmation is taken away: an
    /// <see cref="Interfaces.IMentorActionPolicy"/> that answers <see cref="MentorActionVerdict.Confirm"/> still asks,
    /// and a hosted MCP server's approval still comes from its provider.
    /// </summary>
    None = 2,
}
