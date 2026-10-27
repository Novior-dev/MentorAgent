namespace Novior.MentorAgent.Abstractions.Models;

/// <summary>The kinds of <see cref="MentorActionVerdict"/>, from the least strict to the strictest.</summary>
public enum MentorActionVerdictKind
{
    /// <summary>No objection. A confirmation the action asks for is still asked.</summary>
    Proceed = 0,

    /// <summary>Ask the user to confirm first.</summary>
    Confirm = 1,

    /// <summary>Do not run the call.</summary>
    Stop = 2,
}
