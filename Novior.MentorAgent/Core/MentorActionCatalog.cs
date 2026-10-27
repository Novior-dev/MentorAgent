namespace Novior.MentorAgent.Core;

/// <summary>
/// The application's actions as MentorAgent sees them (story 153), decided again at every call, by the rules the
/// surfaces apply: <see cref="MentorAgentMcpToolCollection.ExposureOf"/> for the MCP server, the skills the A2A
/// card was built with (<see cref="AgentCardSkills"/>), <see cref="MentorOptions.RequireConfirmation"/> and the
/// <see cref="MentorOptions.RequiresApproval"/> predicate as the gate reads them.
/// </summary>
internal sealed class MentorActionCatalog(
    MentorDiscoveryService discovery,
    IOptions<MentorOptions> options,
    IServiceProvider services) : IMentorActionCatalog
{
    private readonly MentorOptions _options = options.Value;

    /// <summary>
    /// The ids of the skills on the A2A card, set by <c>MapMentorAgentA2A</c> when it builds the card; null when the
    /// host does not serve A2A.
    /// </summary>
    internal IReadOnlySet<string>? AgentCardSkills { get; set; }

    public IReadOnlyList<MentorActionInfo> GetActions()
    {
        var found = discovery.Discover();
        var mcp = services.GetService<MentorAgentMcpToolCollection>();
        var actions = new List<MentorActionInfo>();

        foreach (var method in found.Level1Methods)
            actions.Add(Info(method, MentorAgentKind.Coordinator, null,
                mcp?.ExposureOf(method) ?? MentorMcpExposure.NotServed,
                AgentCardSkills?.Contains(method.ToolName) == true));

        // A specialist's and a team member's actions are reached through them: the MCP server and the card never
        // publish them.
        foreach (var agent in found.Level2Agents)
            foreach (var method in agent.Methods)
                actions.Add(Info(method, MentorAgentKind.Specialist, agent.Attribute.Name, MentorMcpExposure.NotServed, false));

        foreach (var team in found.Level3Teams)
            foreach (var member in team.Members)
                foreach (var method in member.Tools)
                    actions.Add(Info(method, MentorAgentKind.TeamMember, member.Attribute.Role, MentorMcpExposure.NotServed, false));

        return actions;
    }

    private MentorActionInfo Info(MethodDiscoveryInfo method, MentorAgentKind kind, string? agentName,
        MentorMcpExposure exposure, bool onCard)
    {
        // As MentorConfirmationBuilder decides it for the called method: its own declaration, or the predicate.
        var declared = method.RequiresConfirmation || _options.RequiresApproval?.Invoke(method.ToolName) == true;
        return new()
        {
            ToolName             = method.ToolName,
            Description          = string.IsNullOrWhiteSpace(method.Description) ? null : method.Description,
            AgentKind            = kind,
            AgentName            = agentName,
            RequiredRoles        = [.. method.RequiredRoles],
            RequiresConfirmation = _options.RequireConfirmation && declared,
            ReadOnly             = !declared && (method.ReadOnly || _options.IsReadOnlyAction?.Invoke(method.ToolName) == true),
            McpExposure          = exposure,
            OnAgentCard          = onCard,
        };
    }
}
