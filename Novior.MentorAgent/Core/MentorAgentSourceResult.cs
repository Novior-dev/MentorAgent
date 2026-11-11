namespace Novior.MentorAgent.Core;

/// <summary>
/// What an <see cref="IMentorAgentSource"/> loaded, and what it left out: the answer of
/// <see cref="IMentorAgentSource.LoadAsync"/>.
/// </summary>
/// <param name="Agents">The agents that join the coordinator's handoff graph.</param>
/// <param name="Rejected">
/// What was left out: a definition that did not load, or a tool taken away from an agent that did. Empty when
/// everything loaded as written.
/// </param>
public sealed record MentorAgentSourceResult(
    IReadOnlyList<AIAgent> Agents,
    IReadOnlyList<MentorAgentRejection> Rejected);

/// <summary>
/// Something an <see cref="IMentorAgentSource"/> left out: a whole definition, or — when <see cref="Tool"/> is set —
/// one tool of an agent that loaded without it.
/// </summary>
/// <param name="Name">
/// What was left out, as the source names it. <c>Novior.MentorAgent.Declarative</c> gives the file name, or
/// <c>inline[n]</c> for the n-th of <c>MentorDeclarativeOptions.Definitions</c>; for
/// <see cref="MentorAgentRejectionReasons.SourceFailed"/>, the full type name of the source.
/// </param>
/// <param name="ReasonCode">One of <see cref="MentorAgentRejectionReasons"/>, or a code of the source's own.</param>
/// <param name="Detail">
/// What to correct, when there is something to say: for a definition the agent factory refused, its message, which
/// describes the text. Never an exception's message for <see cref="MentorAgentRejectionReasons.SourceFailed"/>: only
/// its type.
/// </param>
public sealed record MentorAgentRejection(string Name, string ReasonCode, string? Detail)
{
    /// <summary>
    /// The tool taken away, when the agent loaded without it; <see langword="null"/> when the whole definition was
    /// left out.
    /// </summary>
    public string? Tool { get; init; }
}
