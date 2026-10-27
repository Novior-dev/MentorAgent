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
}
