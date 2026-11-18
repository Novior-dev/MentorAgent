// ── DTO Discovery ─────────────────────────────────────────────────────────────

internal sealed class DiscoveryResult
{
    public List<MethodDiscoveryInfo> Level1Methods { get; } = [];
    public List<AgentDiscoveryInfo> Level2Agents { get; } = [];
    public List<TeamDiscoveryInfo> Level3Teams { get; } = [];
    public List<PageDiscoveryInfo> Pages { get; } = [];

    /// <summary>
    /// Story 1569 — the AutoMentor's candidates left out, each by the rule that left it out, for the trace
    /// (<c>MentorActionRegistration.Rejected</c> of the class's registration). Only those the developer can fix: a
    /// method excluded on purpose, or of the wrong shape, is not here.
    /// </summary>
    public List<CandidateRejection> CandidateRejections { get; } = [];

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

/// <summary>A method of <paramref name="Type"/> the AutoMentor did not take as a candidate, by <paramref name="Rule"/>.</summary>
internal sealed record CandidateRejection(Type Type, string Name, string Rule);

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
    bool ConfirmationDeclared = false,
    bool EnabledByDefault = true,
    string Origin = MentorActionOrigins.Attribute,
    Novior.MentorAgent.Core.IMentorEndpoint? Endpoint = null,
    Novior.MentorAgent.Core.MentorMessageBinding? Message = null
)
{
    // Message (story 1286) is the command or the query an action of origin "command" or "query" sends: the tool is built
    // from its type (MentorActionFunctions.Create), and DeclaringType and Method are its handler's and its HandleAsync.
    // Endpoint (story 1287) is the endpoint an action of origin "endpoint" runs, opaque here but for its name in the
    // trace: the core has no HTTP of its own. Novior.MentorAgent.Server reads it, through IMentorEndpointActionRunner,
    // when it builds the tool.
    // DeclaringType and Method are then the handler's, for the schema and for the rules that read the method.
    // Origin is how the application declares it (story 1285): a MentorActionOrigins code, the registration's first half.
    // EnabledByDefault is what the code says: [MentorAction(Enabled = false)], or an action MentorAgent registers off
    // (story 1549). An IMentorActionGovernor may turn it on; MentorActionGovernance decides.
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
    Type? Type,                     // the [MentorTeam] class; null for a team of an IMentorTeamSource
    MentorTeamAttribute Attribute,
    List<TeamMemberInfo> Members,
    MethodInfo? TerminationMethod,  // metodo [TeamTerminationCondition]
    string? Approver = null         // story 1570: the member whose verdict ends the discussion (a source's team)
);

internal sealed record TeamMemberInfo(
    Type? AgentType,                // the [TeamMember] property's type; null for a source's member
    TeamMemberAttribute Attribute,
    List<MethodDiscoveryInfo> Tools       // read-only tools for this member
);