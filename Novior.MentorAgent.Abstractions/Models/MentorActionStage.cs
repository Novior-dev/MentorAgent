namespace Novior.MentorAgent.Abstractions.Models;

/// <summary>When an <see cref="Interfaces.IMentorActionPolicy"/> is asked.</summary>
public enum MentorActionStage
{
    /// <summary>
    /// MentorAgent decides what to offer: the coordinator's tools for a turn, the MCP server's <c>tools/list</c>.
    /// There is no call yet, and no arguments: <see cref="MentorActionRequest.Arguments"/> is <see langword="null"/>.
    /// </summary>
    Offer = 0,

    /// <summary>A call is about to run, with its arguments.</summary>
    Call = 1,

    /// <summary>
    /// The A2A agent card is built, from 1.5.0: there is no caller and no call. <see cref="MentorActionRequest.User"/>,
    /// the session and <see cref="MentorActionRequest.Arguments"/> are <see langword="null"/>, and
    /// <see cref="MentorActionRequest.Surface"/> is <see cref="MentorActionSurface.A2A"/>. Answer
    /// <see langword="false"/> only for an action that is off for every caller: the card then leaves it out. An action
    /// that depends on who calls is not the card's to decide: answer <see langword="true"/>, and the task asks again,
    /// with its caller. A policy that does not know this stage and looks at the user sees nobody, and often answers no:
    /// the card leaves the action out, and promises less, never more. Asked again when the card is older than 30
    /// seconds, so a rule changed at run time reaches the card within that while.
    /// </summary>
    Card = 2,
}
