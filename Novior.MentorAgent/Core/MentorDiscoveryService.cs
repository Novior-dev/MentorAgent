using System.ComponentModel;
using Novior.MentorAgent.Attributes;
using Microsoft.AspNetCore.Components;
using Novior.MentorAgent.Options;

namespace Novior.MentorAgent.Core;

/// <summary>
/// Scans the assemblies configured in <see cref="MentorOptions.ScanAssemblies"/>
/// and returns a catalog of methods, agents, and teams to expose to the Main Coordinator.
///
/// Executed only once at startup (lazy, thread-safe). The result is cached in
/// <see cref="DiscoveryResult"/>.
/// </summary>
internal sealed partial class MentorDiscoveryService
{
    private readonly MentorOptions _options;
    private readonly ILogger<MentorDiscoveryService> _logger;
    private readonly IServiceProvider? _services;

    private DiscoveryResult? _cached;
    private readonly Lock _lock = new();

    public MentorDiscoveryService(
        IOptions<MentorOptions> options,
        ILogger<MentorDiscoveryService> logger,
        IServiceProvider? services = null)
    {
        _options = options.Value;
        _logger = logger;
        _services = services;
    }

    /// <summary>
    /// Runs (or returns the cached) discovery.
    /// </summary>
    public DiscoveryResult Discover()
    {
        lock (_lock)
        {
            if (_cached is not null) return _cached;

            // Deduplicated before scanning. Two entries resolving to the same assembly is an easy
            // mistake — ScanAssemblies is written by hand as typeof(X).Assembly, and in a multi-
            // project app it takes one wrongly-chosen marker type for two entries to be the same
            // assembly. Scanning it twice registered every tool, agent and team twice, and the
            // duplicate handoff registration then made the Agent Framework throw
            // "A handoff from agent 'X' to agent 'main_coordinator' has already been registered" —
            // which killed the coordinator outright and named nothing the developer had written.
            var assemblies = _options.ScanAssemblies.Distinct().ToArray();

            if (assemblies.Length != _options.ScanAssemblies.Length)
                _logger.LogWarning(
                    "[MentorAgent] ScanAssemblies lists the same assembly more than once ({Names}); "
                    + "the duplicates were ignored. If you meant to scan a different project, check "
                    + "which type you passed to typeof(...).Assembly.",
                    string.Join(", ", _options.ScanAssemblies
                        .GroupBy(a => a)
                        .Where(g => g.Count() > 1)
                        .Select(g => g.Key.GetName().Name)));

            _logger.LogInformation(
                "[MentorAgent] Discovery started on {N} assembly(ies): {Names}",
                assemblies.Length,
                string.Join(", ", assemblies.Select(a => a.GetName().Name)));

            var result = new DiscoveryResult();

            foreach (var asm in assemblies)
                ScanAssembly(asm, result);

            RemoveMemberToolsFromLevel1(result);
            AddRegisteredActions(result);
            AddCandidates(result);

            _logger.LogInformation(
                "[MentorAgent] Discovery completed — L1 tools: {L1}, L2 agents: {L2}, L3 teams: {L3}, pages: {P}",
                result.Level1Methods.Count, result.Level2Agents.Count, result.Level3Teams.Count, result.Pages.Count);

            WarnAboutUnconfirmedWrites(result);
            WarnAboutSharedToolNames(result);

            _cached = result;
            return result;
        }
    }

    /// <summary>
    /// The confirmation a <c>[MentorAction]</c> method gets: what it declares, or — declaring nothing —
    /// <see cref="MentorOptions.RequireConfirmationForWrites"/> unless it says, or the host's
    /// <see cref="MentorOptions.IsReadOnlyAction"/> says, that it only reads.
    /// </summary>
    private bool EffectiveConfirmation(MentorActionAttribute action, string toolName)
        => action.RequiresConfirmationDeclared
            ? action.RequiresConfirmation
            : _options.RequireConfirmationForWrites && !IsReadOnly(action.ReadOnly, toolName);

    private bool IsReadOnly(bool declared, string toolName)
        => declared || _options.IsReadOnlyAction?.Invoke(toolName) == true;

    /// <summary>
    /// Story 198: the writes that run without anyone confirming them, said once at startup, in two lists
    /// because they are fixed in two different places. The zero-match fallback (BUG-106) can hand any
    /// ungated action to the model on a message that matches nothing — a greeting included — so an action
    /// that changes data and asks nobody should be a decision someone made, not an omission.
    /// </summary>
    private void WarnAboutUnconfirmedWrites(DiscoveryResult result)
    {
        var all = result.Level1Methods
            .Concat(result.Level2Agents.SelectMany(a => a.Methods))
            .Concat(result.Level3Teams.SelectMany(t => t.Members).SelectMany(m => m.Tools))
            .Where(m => !m.RequiresConfirmation
                        && !IsReadOnly(m.ReadOnly, m.ToolName)
                        && _options.RequiresApproval?.Invoke(m.ToolName) != true)
            .ToList();

        var chosen = all.Where(m => m.IsMentorAction).Select(m => m.ToolName).Distinct().Order().ToList();
        if (chosen.Count > 0)
            _logger.LogWarning(
                "[MentorAgent] {Count} [MentorAction] method(s) that are not ReadOnly run without confirmation: {Names}. " +
                "{Why} If one only reads, declare ReadOnly = true; if it changes data, let it ask.",
                chosen.Count, string.Join(", ", chosen),
                _options.RequireConfirmationForWrites
                    ? "Each declares RequiresConfirmation = false."
                    : "RequireConfirmationForWrites is off, so only RequiresConfirmation = true asks.");

        var uncovered = all.Where(m => !m.IsMentorAction).Select(m => m.ToolName).Distinct().Order().ToList();
        if (uncovered.Count > 0)
            _logger.LogWarning(
                "[MentorAgent] {Count} method(s) marked with [Description] alone are not covered by " +
                "RequireConfirmationForWrites and run without confirmation: {Names}. Mark one [MentorAction] to have " +
                "the default, or declare it read-only with IsReadOnlyAction if it only reads.",
                uncovered.Count, string.Join(", ", uncovered));
    }

    /// <summary>
    /// BUG-182: the tool names more than one method carries, said once at startup. A model calls a tool by
    /// name, so an agent offered both methods can reach only one of them, and tool filtering and the
    /// <c>RequiresApproval</c> and <c>IsReadOnlyAction</c> rules decide by name. The gate checks the method
    /// called (BUG-188); up to 1.0.0 it took the first copy with the name. Only tool filtering reported shared
    /// names before, and only when it was switched on.
    /// </summary>
    private void WarnAboutSharedToolNames(DiscoveryResult result)
    {
        var shared = result.Level1Methods
            .Concat(result.Level2Agents.SelectMany(a => a.Methods))
            .Concat(result.Level3Teams.SelectMany(t => t.Members).SelectMany(m => m.Tools))
            // One method found twice is not two: a skill's methods are level-1 tools and a member's too.
            .DistinctBy(m => m.Method)
            .GroupBy(m => m.ToolName, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Key} ({string.Join(", ", g.Select(m => $"{m.DeclaringType.Name}.{m.Method.Name}"))})")
            .ToList();

        if (shared.Count > 0)
            _logger.LogWarning(
                "[MentorAgent] {Count} tool name(s) are carried by more than one method: {Names}. A model calls " +
                "a tool by name, so an agent offered both can reach only one of them, and tool filtering and the " +
                "RequiresApproval and IsReadOnlyAction rules decide by name. Rename the methods or expose only " +
                "one of them.",
                shared.Count, string.Join("; ", shared));
    }

    /// <summary>
    /// A class listed in a <c>[TeamMember(Tools = ...)]</c> is that member's toolbox, not the
    /// coordinator's. The scan finds its methods as level-1 tools like any other class's, and the
    /// README asks for the class in DI, so it also became a set of coordinator actions — on /mcp and
    /// the A2A card too. A <c>[MentorSkill]</c> keeps its methods at level 1: that is the skill's own
    /// documented role.
    /// </summary>
    private void RemoveMemberToolsFromLevel1(DiscoveryResult result)
    {
        // Who lists each class, for the log: an upgrade from rc.14 loses these coordinator tools,
        // and the line has to say which and why.
        var listedBy = result.Level3Teams
            .SelectMany(t => t.Members.SelectMany(m => m.Attribute.Tools
                .Select(toolType => (ToolType: toolType, Member: $"'{m.Attribute.Role}' ({t.Attribute.Name})"))))
            .Where(x => x.ToolType.GetCustomAttribute<MentorSkillAttribute>() is null)
            .GroupBy(x => x.ToolType, x => x.Member);

        foreach (var listing in listedBy)
        {
            var moved = result.Level1Methods.Where(m => m.DeclaringType == listing.Key).ToList();
            if (moved.Count == 0) continue;

            result.Level1Methods.RemoveAll(m => m.DeclaringType == listing.Key);
            _logger.LogInformation(
                "[MentorAgent] {Type} is listed in the Tools of {Members}: {Methods} are team-member tools only, " +
                "not the coordinator's, nor on /mcp or the A2A card.",
                listing.Key.Name,
                string.Join(", ", listing.Distinct()),
                string.Join(", ", moved.Select(m => m.ToolName)));
        }
    }

    /// <summary>
    /// Story 1285 — the actions <see cref="MentorOptions.Actions"/> registers in code (rule <c>fluent.coordinator-action</c>):
    /// the coordinator's, after the scan, as a <c>[MentorAction]</c> method would be, with what each registration declares
    /// (<c>fluent.declared-decides</c>). <see cref="MentorOptions.Validate"/> has already refused the method registered twice.
    /// </summary>
    private void AddRegisteredActions(DiscoveryResult result)
    {
        foreach (var registration in _options.Actions.All)
        {
            // Story 1286 — a command or a query: named after its type, or as declared; a query is read-only.
            var toolName = registration.ToolName;
            result.Level1Methods.Add(new MethodDiscoveryInfo(
                DeclaringType: registration.Type,
                Method: registration.Method,
                ToolName: toolName,
                Description: registration.Description,
                Category: registration.Category,
                // As EffectiveConfirmation for a [MentorAction]: what it declares, or the default for writes.
                RequiresConfirmation: registration.Confirmation
                    ?? (_options.RequireConfirmationForWrites && !IsReadOnly(registration.ReadOnly, toolName)),
                ProactiveHint: registration.ProactiveHint,
                RequiredRoles: registration.Roles,
                NavigateTo: registration.NavigateTo,
                ReadOnly: registration.ReadOnly,
                IsMentorAction: true,
                ConfirmationDeclared: registration.Confirmation is not null,
                EnabledByDefault: registration.Enabled,
                Origin: registration.Message?.Origin ?? MentorActionOrigins.Fluent,
                Message: registration.Message));
        }

        if (_options.Actions.Count > 0)
            _logger.LogDebug("[MentorAgent] L1: {N} action(s) registered in Actions", _options.Actions.Count);
    }

    // ── Scansione ─────────────────────────────────────────────────────────────

    private void ScanAssembly(Assembly asm, DiscoveryResult result)
    {
        IEnumerable<Type> types;
        try
        {
            types = asm.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            _logger.LogWarning("[MentorAgent] Some types from {Asm} could not be loaded: {Err}",
                asm.GetName().Name,
                string.Join("; ", ex.LoaderExceptions.Select(e => e?.Message)));
            types = ex.Types.Where(t => t is not null)!;
        }

        foreach (var type in types.Where(t => t.IsClass && !t.IsAbstract))
        {
            // Story 1287 (scan.controllers-are-endpoints): a controller's actions are endpoints. Read here, they ran as a
            // method on an instance from the container, past the endpoint's authorization and filters; a [MentorAction]
            // on a controller's action is read from the endpoint's metadata instead, by Novior.MentorAgent.Server.
            var controller = IsController(type, out var unreadable);
            if (unreadable)
            {
                // BUG-236 (scan.attributes-not-created): a type one of its attributes names does not load.
                _logger.LogWarning(
                    "[MentorAgent] The attributes of {Type} could not be read: a type they name does not load. The class is skipped.",
                    type.FullName);
                continue;
            }
            if (controller)
            {
                _logger.LogDebug("[MentorAgent] {Type} is a controller: its actions are read from its endpoints.", type.Name);
                continue;
            }

            ScanType(type, result);
        }
    }

    /// <summary>
    /// A class ASP.NET Core MVC serves as a controller: one that carries <c>[Controller]</c> or <c>[ApiController]</c>,
    /// itself or from a base class, and not <c>[NonController]</c>. <c>ControllerBase</c> carries <c>[Controller]</c>, so
    /// every class derived from it is one. Read by name: the core does not reference MVC.
    /// </summary>
    internal static bool IsController(Type type) => IsController(type, out _);

    /// <summary>
    /// <inheritdoc cref="IsController(Type)"/> BUG-236 (<c>scan.attributes-not-created</c>): the names are read from the
    /// attributes' metadata (<see cref="CustomAttributeData"/>), the class's and its base classes', as the three
    /// attributes are inherited, so no attribute of the application is created and one whose constructor throws cannot
    /// stop the scan. <paramref name="unreadable"/> is set, and the class is not a controller, when a type one of its
    /// attributes names does not load.
    /// </summary>
    internal static bool IsController(Type type, out bool unreadable)
    {
        unreadable = false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            for (var t = type; t is not null && t != typeof(object); t = t.BaseType)
                foreach (var data in t.GetCustomAttributesData())
                    if (data.AttributeType.FullName is { } name) names.Add(name);
        }
        catch (Exception ex) when (IsLoadFailure(ex))
        {
            unreadable = true;
            return false;
        }
        return !names.Contains("Microsoft.AspNetCore.Mvc.NonControllerAttribute")
               && (names.Contains("Microsoft.AspNetCore.Mvc.ControllerAttribute") || names.Contains("Microsoft.AspNetCore.Mvc.ApiControllerAttribute"));
    }

    /// <summary>A type, or the assembly it is in, that does not load: what reading an attribute's metadata can throw.</summary>
    internal static bool IsLoadFailure(Exception ex)
        => ex is TypeLoadException or FileNotFoundException or FileLoadException or BadImageFormatException;

    /// <summary>
    /// Story 1287 — the actions of the application's endpoints, added once the host has started and mapped them
    /// (<c>EndpointDataSource</c>), after the discovery of the code. A name an action already has stops the application:
    /// a model calls one tool per name (<c>endpoint.declared</c>).
    /// </summary>
    internal void AddEndpointActions(IReadOnlyList<MethodDiscoveryInfo> actions)
    {
        if (actions.Count == 0) return;

        var result = Discover();
        lock (_lock)
        {
            foreach (var action in actions)
            {
                // Story 1569: a candidate gives way to the endpoint (candidate.name), it does not stop the application.
                YieldCandidateName(result, action.ToolName);

                var clash = result.Level1Methods
                    .Concat(result.Level2Agents.SelectMany(a => a.Methods))
                    .Concat(result.Level3Teams.SelectMany(t => t.Members).SelectMany(m => m.Tools))
                    .FirstOrDefault(m => string.Equals(m.ToolName, action.ToolName, StringComparison.OrdinalIgnoreCase));
                if (clash is not null)
                    throw new InvalidOperationException(
                        $"[MentorAgent] The endpoint action '{action.ToolName}' has the name of another action " +
                        $"({clash.DeclaringType.Name}.{clash.Method.Name}). Give it another name with WithMentorAction(name, …).");

                result.Level1Methods.Add(action);
            }

            _logger.LogInformation("[MentorAgent] L1: {N} action(s) from the application's endpoints: {Names}",
                actions.Count, string.Join(", ", actions.Select(a => a.ToolName)));
            var added = new DiscoveryResult();
            added.Level1Methods.AddRange(actions);
            WarnAboutUnconfirmedWrites(added);
        }
    }

    private void ScanType(Type type, DiscoveryResult result)
    {
        // ── Navigable pages: [MentorPage] ────────────────────────────────────
        var pageAttr = type.GetCustomAttribute<MentorPageAttribute>();
        if (pageAttr is not null)
        {
            result.Pages.Add(new PageDiscoveryInfo(
                Url: pageAttr.Url,
                Name: pageAttr.Name,
                Description: pageAttr.Description,
                HasUIActions: pageAttr.HasUIActions,
                ReadyTimeout: pageAttr.ReadyTimeout
            ));
            _logger.LogDebug("[MentorAgent] Page '{Name}' → {Url}", pageAttr.Name, pageAttr.Url);
        }

        // ── Level 3: [MentorTeam] ────────────────────────────────────────────
        var teamAttr = type.GetCustomAttribute<MentorTeamAttribute>();
        if (teamAttr is not null)
        {
            var memberProps = type.GetProperties()
                .Select(p => (Prop: p, Attr: p.GetCustomAttribute<TeamMemberAttribute>()))
                .Where(x => x.Attr is not null)
                .Select(x =>
                {
                    // Resolve tools from the classes specified in Tools = [typeof(...)], by the same
                    // rules as levels 1 and 2: public instance methods declared on the class itself,
                    // with [MentorAction] or a non-empty [Description]. A member used to get inherited
                    // and static methods too, and a [Description("")] one.
                    var memberTools = x.Attr!.Tools.SelectMany(DiscoverMethods).ToList();

                    return new TeamMemberInfo(x.Prop.PropertyType, x.Attr!, memberTools);
                })
                .ToList();

            var terminationMethod = type.GetMethods()
                .FirstOrDefault(m => m.GetCustomAttribute<TeamTerminationConditionAttribute>() is not null);

            if (memberProps.Count == 0)
            {
                _logger.LogWarning("[MentorAgent] {Type} has [MentorTeam] but no [TeamMember] properties. Skipped.", type.Name);
                return;
            }

            result.Level3Teams.Add(new TeamDiscoveryInfo(type, teamAttr, memberProps, terminationMethod));
            _logger.LogDebug("[MentorAgent] L3 Team '{Name}' ({Type}) — {N} members", teamAttr.Name, type.Name, memberProps.Count);
            return;
        }

        // ── Level 2: [MentorAgent] ───────────────────────────────────────────
        var agentAttr = type.GetCustomAttribute<MentorAgentAttribute>();
        if (agentAttr is not null)
        {
            var methods = DiscoverMethods(type);
            result.Level2Agents.Add(new AgentDiscoveryInfo(type, agentAttr, methods));
            _logger.LogDebug("[MentorAgent] L2 Agent '{Name}' ({Type}) — {N} methods", agentAttr.Name, type.Name, methods.Count);
            return;
        }

        // ── [MentorSkill] — dual role: skill knowledge + optional L1 tools ─────
        // The [MentorSkill] attribute registers the class as a skill (instructions
        // injected via load_skill). If the class also has [Description] / [MentorAction]
        // methods, those are registered as L1 tools so the AI can execute them directly.
        //
        // Pattern:
        //   Skill  = the HOW (policy, rules, limits — loaded on demand via load_skill)
        //   Methods = the DO  (actual execution — always available as tools)
        //
        // ⚠️ Requires DI registration:
        //   builder.Services.AddScoped<ExpenseReportSkill>();
        var skillAttr = type.GetCustomAttribute<MentorSkillAttribute>();
        if (skillAttr is not null)
        {
            var skillMethods = DiscoverMethods(type);
            if (skillMethods.Count > 0)
            {
                result.Level1Methods.AddRange(skillMethods);
                _logger.LogDebug(
                    "[MentorAgent] Skill '{Name}' ({Type}) — {N} method(s) registered as L1 tools",
                    skillAttr.Name, type.Name, skillMethods.Count);
            }
            return; // explicit early return — prevents double-scan by the generic L1 block below
        }

        // ── Level 1: methods with [Description] or [MentorAction] ─────────────
        var methods1 = DiscoverMethods(type);
        if (methods1.Count > 0)
        {
            result.Level1Methods.AddRange(methods1);
            _logger.LogDebug("[MentorAgent] L1: {N} methods in {Type}", methods1.Count, type.Name);
        }
    }

    // ── Method Discovery ──────────────────────────────────────────────────────

    private List<MethodDiscoveryInfo> DiscoverMethods(Type type)
    {
        var list = new List<MethodDiscoveryInfo>();

        foreach (var method in type.GetMethods(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            // [MentorAction] — MentorAgent attribute with extended metadata
            var mentorAttr = method.GetCustomAttribute<MentorActionAttribute>();
            if (mentorAttr is not null)
            {
                list.Add(new MethodDiscoveryInfo(
                    DeclaringType: type,
                    Method: method,
                    ToolName: MentorUtils.ToSnakeCase(method.Name),
                    Description: mentorAttr.Description,
                    Category: mentorAttr.Category,
                    RequiresConfirmation: EffectiveConfirmation(mentorAttr, MentorUtils.ToSnakeCase(method.Name)),
                    ProactiveHint: mentorAttr.ProactiveHint,
                    RequiredRoles: mentorAttr.RequiredRoles,
                    NavigateTo: mentorAttr.NavigateTo,
                    ReadOnly: mentorAttr.ReadOnly,
                    IsMentorAction: true,
                    ConfirmationDeclared: mentorAttr.RequiresConfirmationDeclared,
                    EnabledByDefault: mentorAttr.Enabled
                ));
                continue;
            }

            // [Description] — native .NET attribute, no extra dependencies
            var descAttr = method.GetCustomAttribute<DescriptionAttribute>();
            if (descAttr is not null && !string.IsNullOrWhiteSpace(descAttr.Description))
            {
                list.Add(new MethodDiscoveryInfo(
                    DeclaringType: type,
                    Method: method,
                    ToolName: MentorUtils.ToSnakeCase(method.Name),
                    Description: descAttr.Description,
                    Category: null,
                    RequiresConfirmation: false,
                    ProactiveHint: null,
                    RequiredRoles: [],
                    NavigateTo: null
                ));
            }
        }

        return list;
    }

}
