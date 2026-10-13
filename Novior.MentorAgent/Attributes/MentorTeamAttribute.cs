namespace Novior.MentorAgent.Attributes;

/// <summary>
/// Level 3 — Decorates a class to define a collaborative Group Chat team.
///
/// Team members are defined via properties decorated with <see cref="TeamMemberAttribute"/>.
/// The termination condition is defined via a method decorated with
/// <see cref="TeamTerminationConditionAttribute"/>.
///
/// After approval, the team can hand off to L2 agents via HandoffTo.
///
/// <code>
/// [MentorTeam(
///     Name = "BusinessAnalysisTeam",
///     Description = "Business analysis with real data and handoff for execution",
///     HandoffTo = ["OrderAgent", "CustomerAgent"])]
/// public class BusinessAnalysisTeam
/// {
///     [TeamMember(
///         Role = "DataAnalyst",
///         Tools = [typeof(ReportTools), typeof(OrderTools)],
///         Instructions = "Analyze the data...")]
///     public object? Analyst { get; set; }
///
///     [TeamMember(Role = "Approver", Instructions = "Give the verdict...")]
///     public object? Approver { get; set; }
///
///     [TeamTerminationCondition]
///     public bool ShouldTerminate(string lastMessage, string lastSpeaker)
///         => lastSpeaker == "Approver" &amp;&amp; lastMessage.Contains("APPROVED");
/// }
/// </code>
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class MentorTeamAttribute : Attribute
{
    /// <summary>The team name.</summary>
    public required string Name { get; init; }

    /// <summary>Collective description of the team. Used by the Main Coordinator to decide when to activate it.</summary>
    public required string Description { get; init; }

    /// <summary>Keywords that trigger team activation.</summary>
    public string[] TriggerOn { get; init; } = [];

    /// <summary>
    /// Maximum number of turns in the Group Chat before forced termination.
    /// Default: 10
    /// </summary>
    public int MaxIterations { get; init; } = 10;

    /// <summary>
    /// L2 agents to hand off to after team approval.
    /// After APPROVED the coordinator delegates execution to the specified agents.
    /// Same syntax as [MentorAgent(HandoffTo = [...])].
    ///
    /// Example: HandoffTo = ["OrderAgent", "CustomerAgent"]
    /// </summary>
    public string[] HandoffTo { get; init; } = [];
}
