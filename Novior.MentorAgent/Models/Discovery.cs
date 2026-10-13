// ── DTO Discovery ─────────────────────────────────────────────────────────────

internal sealed class DiscoveryResult
{
    public List<MethodDiscoveryInfo> Level1Methods { get; } = [];
    public List<AgentDiscoveryInfo> Level2Agents { get; } = [];
    public List<TeamDiscoveryInfo> Level3Teams { get; } = [];
    public List<PageDiscoveryInfo> Pages { get; } = [];
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