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
    IServiceProvider services,
    MentorPageActionTrace? pageTrace = null,
    MentorPageDirectory? pageDirectory = null) : IMentorActionCatalog
{
    /// <summary>
    /// Story 1635 — the pages the AutoMentor knows (<see cref="MentorPageDirectory"/>), with what the manifest says of
    /// each, as the public records. Story 1640 — and what the widget observed that the scan did not read, marked
    /// <see cref="MentorPageCoverage.Observed"/>, with no label and no form: what a user saw stays in the process
    /// (<c>page.observe-private</c>), whoever reads the catalogue and wherever they send it.
    /// </summary>
    public IReadOnlyList<MentorPageInfo> GetPages()
        => pageDirectory is null ? [] : [.. pageDirectory.Pages().Select(p => new MentorPageInfo
        {
            Route     = p.Route,
            Component = p.Component,
            Coverage  = p.Scan is not null ? MentorPageCoverage.Scanned
                      : p.Observed.Count > 0 ? MentorPageCoverage.Observed
                      : MentorPageCoverage.ToScan,
            ScannedAt = p.Scan?.ScannedAt,
            Elements  = [.. (p.Scan?.Elements ?? []).Select(e => Element(e, MentorPageCoverage.Scanned)),
                         .. p.Observed.Select(e => Element(e with { Label = null, Form = null }, MentorPageCoverage.Observed))],
        })];

    private static MentorPageElement Element(MentorUiManifest.Element e, MentorPageCoverage coverage) => new()
    {
        Element  = e.Tag,
        Role     = e.Role,
        Label    = e.Label,
        Type     = e.Type,
        Options  = [.. e.Options],
        Form     = e.Form,
        Selector = e.Selector,
        Roles    = [.. e.Roles],
        Coverage = coverage,
    };

    private readonly MentorOptions _options = options.Value;

    /// <summary>
    /// Story 1773 — the schema of each action's arguments, built once: discovery keeps its actions, and building a tool
    /// reads the method by reflection.
    /// </summary>
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<MethodDiscoveryInfo, System.Runtime.CompilerServices.StrongBox<System.Text.Json.JsonElement?>> _schemas = new();

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
                method.EnabledByDefault && AgentCardSkills?.Contains(method.ToolName) == true));

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

    /// <summary>
    /// Story 1285 — the one public trace of where the actions come from: one registration per origin and class, in the
    /// order discovery finds their first action, with every action it gives — the coordinator's, a specialist's or a team
    /// member's, those shipped off included. A method found twice (a skill's, a member's toolbox) is listed once.
    /// An endpoint declared as an action (story 1287) is a registration of its own, named by its verb and route
    /// (<c>endpoint.trace</c>): its handler's class says nothing, and for a lambda it is the compiler's.
    /// After them, story 1288 slice C: one registration per page, with the names of the UI actions it declared
    /// (<see cref="MentorPageActionTrace"/>).
    /// </summary>
    public IReadOnlyList<MentorActionRegistration> GetRegistrations()
        => [.. CodeRegistrations(), .. pageTrace?.Registrations() ?? []];

    private IEnumerable<MentorActionRegistration> CodeRegistrations()
    {
        var found = discovery.Discover();
        var registrations = found.Level1Methods
            .Concat(found.Level2Agents.SelectMany(a => a.Methods))
            .Concat(found.Level3Teams.SelectMany(t => t.Members).SelectMany(m => m.Tools))
            .DistinctBy(m => (m.Origin, m.Method, SourceOf(m)))   // two endpoints may share their handler
            .GroupBy(RegistrationId)
            .Select(g => new MentorActionRegistration
            {
                Id      = g.Key,
                Origin  = g.First().Origin,
                Source  = SourceOf(g.First()),
                Actions = [.. g.Select(m => m.ToolName)],
            })
            .ToList();

        // Story 1569: the candidates a rule left out, in their class's registration, or in one of their own when the
        // class gives no candidate.
        foreach (var rejected in found.CandidateRejections.GroupBy(r => r.Type))
        {
            var source = rejected.Key.FullName ?? rejected.Key.Name;
            var id = MentorActionOrigins.Candidate + ":" + source;
            var rejections = rejected.Select(r => new MentorActionRejection { Name = r.Name, Rule = r.Rule }).ToList();
            var index = registrations.FindIndex(r => r.Id == id);
            if (index >= 0)
                registrations[index] = registrations[index] with { Rejected = rejections };
            else
                registrations.Add(new MentorActionRegistration
                {
                    Id = id, Origin = MentorActionOrigins.Candidate, Source = source, Rejected = rejections,
                });
        }
        return registrations;
    }

    /// <summary>The <see cref="MentorActionRegistration.Id"/> of the registration that gives <paramref name="method"/>.</summary>
    private static string RegistrationId(MethodDiscoveryInfo method) => method.Origin + ":" + SourceOf(method);

    private static string SourceOf(MethodDiscoveryInfo method)
        => method.Endpoint?.Display
           ?? (method.Message is { } message ? message.MessageType.FullName ?? message.MessageType.Name : null)   // story 1286
           ?? method.DeclaringType.FullName ?? method.DeclaringType.Name;

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
            EnabledByDefault     = method.EnabledByDefault,
            McpExposure          = exposure,
            OnAgentCard          = onCard,
            Origin               = method.Origin,
            RegistrationId       = RegistrationId(method),
            Method               = MethodOf(method),
            ParametersSchema     = _schemas.GetValue(method, m => new(SchemaOf(m))).Value,
        };
    }

    /// <summary>
    /// Story 1773 (<c>describe.parameters-schema</c>) — the schema of the arguments of the tool the model is given for
    /// <paramref name="method"/>, from the code that builds it: <see cref="MentorActionFunctions.Schema"/>, or, for an
    /// endpoint, <see cref="IMentorEndpointActionRunner.Schema"/>, which only Novior.MentorAgent.Server registers.
    /// </summary>
    private System.Text.Json.JsonElement? SchemaOf(MethodDiscoveryInfo method)
    {
        if (method.Endpoint is null)
            return MentorActionFunctions.Schema(method, method.ToolDescription);
        try
        {
            return services.GetService<IMentorEndpointActionRunner>()?.Schema(method);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// The method of an action written as one (story 1571's inventory reads its parameters): an endpoint's
    /// <see cref="MethodDiscoveryInfo.Method"/> is its handler's, a lambda's included, and is not given. A command's or a
    /// query's is its handler's <c>HandleAsync</c> (story 1286), whose parameter is the type the model's arguments make.
    /// </summary>
    private static System.Reflection.MethodInfo? MethodOf(MethodDiscoveryInfo method)
        => method.Origin is MentorActionOrigins.Attribute or MentorActionOrigins.Fluent or MentorActionOrigins.Candidate
               or MentorActionOrigins.Command or MentorActionOrigins.Query
            ? method.Method
            : null;
}
