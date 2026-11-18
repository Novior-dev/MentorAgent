namespace Novior.MentorAgent.Core;

/// <summary>
/// Gives the coordinator teams that are not written in C#: from definition files (<c>Novior.MentorAgent.Declarative</c>),
/// from a console, from any store. Asked every time a coordinator is built, so a new session sees what the source holds
/// then. A team it gives is a team like one with <c>[MentorTeam]</c>: the same group chat, a tool named after it, the
/// same verdict and hand-off. Register it with <c>AddMentorTeamSource&lt;T&gt;()</c>; every source registered is asked.
/// <para>
/// A definition chooses from what the application has: a member's tools are names of the session's actions, each called
/// through the gate as it is everywhere else. What does not hold is left out with its reason, in the log and for every
/// <see cref="IMentorAgentSourceObserver"/> (<see cref="IMentorAgentSourceObserver.OnTeamsLoaded"/>), never silently.
/// </para>
/// </summary>
public interface IMentorTeamSource
{
    /// <summary>The source's teams for one coordinator, and what it left out itself.</summary>
    Task<MentorTeamSourceResult> LoadAsync(MentorTeamSourceContext context, CancellationToken ct = default);
}

/// <summary>What a <see cref="IMentorTeamSource"/> is told when it is asked.</summary>
public sealed record MentorTeamSourceContext
{
    /// <summary>The session the coordinator is built for; null when there is none, as at the warm-up.</summary>
    public string? SessionId { get; init; }
}

/// <summary>The teams a <see cref="IMentorTeamSource"/> gives, and the definitions it left out with their reason.</summary>
/// <param name="Teams">The definitions, in the order the source gives them.</param>
/// <param name="Rejected">What the source itself could not read, with a <see cref="MentorAgentRejectionReasons"/> code.</param>
public sealed record MentorTeamSourceResult(
    IReadOnlyList<MentorTeamDefinition> Teams,
    IReadOnlyList<MentorAgentRejection> Rejected);

/// <summary>
/// A team as a source gives it: what <c>[MentorTeam]</c> and its <c>[TeamMember]</c> properties declare in C#, with
/// <see cref="Approver"/> in place of a <c>[TeamTerminationCondition]</c>.
/// </summary>
public sealed record MentorTeamDefinition
{
    /// <summary>The team's name; its tool is named after it, in snake_case. Required.</summary>
    public required string Name { get; init; }

    /// <summary>What the team is for: the coordinator reads it to decide when to call the team. Required.</summary>
    public required string Description { get; init; }

    /// <summary>Words that tell the coordinator to always call the team, as <c>MentorTeamAttribute.TriggerOn</c>.</summary>
    public IReadOnlyList<string> TriggerOn { get; init; } = [];

    /// <summary>The most turns of the discussion, at least 1. Default 10.</summary>
    public int MaxIterations { get; init; } = 10;

    /// <summary>The specialists the coordinator hands the approved actions to, as <c>MentorTeamAttribute.HandoffTo</c>.</summary>
    public IReadOnlyList<string> HandoffTo { get; init; } = [];

    /// <summary>
    /// The role of the member whose verdict ends the discussion; null: the first verdict of any member ends it.
    /// It must be the role of one of <see cref="Members"/>.
    /// </summary>
    public string? Approver { get; init; }

    /// <summary>The members, who speak in turn. At least one.</summary>
    public required IReadOnlyList<MentorTeamMemberDefinition> Members { get; init; }
}

/// <summary>A member of a <see cref="MentorTeamDefinition"/>, as a <c>[TeamMember]</c> property declares it.</summary>
public sealed record MentorTeamMemberDefinition
{
    /// <summary>The member's role, its name in the discussion; one per team. Required.</summary>
    public required string Role { get; init; }

    /// <summary>What the member does and how it answers. Required.</summary>
    public required string Instructions { get; init; }

    /// <summary>
    /// The tool names of the session's actions the member may call. A name the session has no action for — unknown, or
    /// off for the session — is taken away with <see cref="MentorAgentRejectionReasons.ToolNotFound"/>.
    /// </summary>
    public IReadOnlyList<string> Tools { get; init; } = [];
}
