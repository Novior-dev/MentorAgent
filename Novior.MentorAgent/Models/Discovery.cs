// ── DTO Discovery ─────────────────────────────────────────────────────────────

internal sealed class DiscoveryResult
{
    public List<MethodDiscoveryInfo> Level1Methods { get; } = [];
    public List<AgentDiscoveryInfo> Level2Agents { get; } = [];
    public List<TeamDiscoveryInfo> Level3Teams { get; } = [];
    public List<PageDiscoveryInfo> Pages { get; } = [];

    /// <summary>
    /// Every discovered method that carries <paramref name="toolName"/>: level 1, then the specialists',
    /// then the team members'.
    /// </summary>
    public IEnumerable<MethodDiscoveryInfo> Named(string toolName)
        => Level1Methods
            .Concat(Level2Agents.SelectMany(a => a.Methods))
            .Concat(Level3Teams.SelectMany(t => t.Members).SelectMany(m => m.Tools))
            .Where(m => m.ToolName == toolName);

    /// <summary>
    /// The copy of <paramref name="toolName"/> built from <paramref name="method"/>, the function's own method;
    /// null when it is not known or discovery did not find it.
    /// </summary>
    public MethodDiscoveryInfo? BuiltFrom(string toolName, MethodInfo? method)
        => method is null ? null : Named(toolName).FirstOrDefault(m => m.Method == method);

    /// <summary>
    /// The method a call to <paramref name="toolName"/> runs, whose roles, confirmation, text and page the gate
    /// applies: the copy built from the function's own <paramref name="method"/> when discovery knows it, so two
    /// methods that share a name are each checked against their own (BUG-188); otherwise the first copy, level 1
    /// first. Null for a tool discovery does not know, such as an MCP tool.
    /// </summary>
    public MethodDiscoveryInfo? Called(string toolName, MethodInfo? method)
        => BuiltFrom(toolName, method) ?? Named(toolName).FirstOrDefault();
}

internal sealed record PageDiscoveryInfo(
    string Url,
    string Name,
    string? Description,
    bool HasUIActions,
    int ReadyTimeout
);

internal sealed record MethodDiscoveryInfo(
    Type DeclaringType,
    MethodInfo Method,
    string ToolName,
    string Description,
    string? Category,
    bool RequiresConfirmation,
    string? ProactiveHint,
    string[] RequiredRoles,
    string? NavigateTo,
    bool ReadOnly = false,
    bool IsMentorAction = false,
    bool ConfirmationDeclared = false
)
{
    // RequiresConfirmation is the EFFECTIVE value: what the attribute declares, or — for a [MentorAction]
    // that declares nothing — MentorOptions.RequireConfirmationForWrites applied to it (story 152). Every
    // consumer (the gate, tool filtering, /mcp, the A2A card, the zero-match fallback) reads this one field.
    /// <summary>
    /// What the assistant's own model reads for this tool: the description, then the
    /// <c>ProactiveHint</c> when there is one. Discovery stored the hint and nothing ever read it, while
    /// the attribute and both READMEs said it guides the assistant (rc.11 README audit). The tool
    /// description is where it is read at the moment it matters, and it keeps the cached instructions
    /// prefix unchanged. Not used on the MCP server surface, which is for other applications' agents.
    /// </summary>
    public string ToolDescription => string.IsNullOrWhiteSpace(ProactiveHint)
        ? Description
        : $"{Description} Guidance: {ProactiveHint.Trim()}";
}

internal sealed record AgentDiscoveryInfo(
    Type Type,
    MentorAgentAttribute Attribute,
    List<MethodDiscoveryInfo> Methods
);

internal sealed record TeamDiscoveryInfo(
    Type Type,
    MentorTeamAttribute Attribute,
    List<TeamMemberInfo> Members,
    MethodInfo? TerminationMethod   // metodo [TeamTerminationCondition]
);

internal sealed record TeamMemberInfo(
    Type AgentType,
    TeamMemberAttribute Attribute,
    List<MethodDiscoveryInfo> Tools       // read-only tools for this member
);