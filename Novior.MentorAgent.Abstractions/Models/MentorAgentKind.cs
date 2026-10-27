namespace Novior.MentorAgent.Abstractions.Models;

/// <summary>Which kind of agent asked for an action.</summary>
public enum MentorAgentKind
{
    /// <summary>The coordinator: on Path A the one MentorAgent builds, on Path B the host's own agent.</summary>
    Coordinator = 0,

    /// <summary>A level-2 specialist (<c>[MentorAgent]</c>).</summary>
    Specialist = 1,

    /// <summary>A member of a level-3 team (<c>[TeamMember]</c>).</summary>
    TeamMember = 2,

    /// <summary>An agent built by an <c>IMentorAgentSource</c>, such as a declarative (YAML) agent.</summary>
    AgentSource = 3,
}
