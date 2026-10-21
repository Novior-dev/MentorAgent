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
internal sealed class MentorDiscoveryService
{
    private readonly MentorOptions _options;
    private readonly ILogger<MentorDiscoveryService> _logger;

    private DiscoveryResult? _cached;
    private readonly Lock _lock = new();

    public MentorDiscoveryService(
        IOptions<MentorOptions> options,
        ILogger<MentorDiscoveryService> logger)
    {
        _options = options.Value;
        _logger = logger;
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
            ScanType(type, result);
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
                    ConfirmationDeclared: mentorAttr.RequiresConfirmationDeclared
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
