namespace Novior.MentorAgent.Abstractions.Models;

/// <summary>Which kind of agent asked for an action, or made a call to a model.</summary>
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

    /// <summary>
    /// The router of the level-2 handoff, which hands a delegated request to a specialist. It has no tools of the
    /// application, so it is named only by the calls to a model (<c>MentorModelCall</c>), never by a decision.
    /// </summary>
    Router = 4,
}
