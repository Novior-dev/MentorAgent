using System.ComponentModel;
using FluentAssertions;
using Novior.MentorAgent.Attributes;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.App;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Novior.MentorAgent.Tests.Discovery;

/// <summary>
/// Assembly scanning: what becomes a tool, what does not, and what metadata survives the trip.
///
/// <para>
/// Everything downstream reads this catalogue. The role gate looks up <c>RequiredRoles</c> by tool
/// name, the confirmation gate reads <c>RequiresConfirmation</c>, the prompt builder reads
/// <c>Category</c> and <c>ProactiveHint</c>, metrics key on the name. A method that fails to be
/// discovered is not an error anywhere — it is simply a capability the assistant never mentions,
/// which reads to the developer as the model being unhelpful.
/// </para>
/// </summary>
public sealed class DiscoveryTests
{
    private static DiscoveryResult Discover(params Type[] markerTypes)
    {
        var assemblies = markerTypes.Length > 0
            ? markerTypes.Select(t => t.Assembly).Distinct().ToArray()
            : [typeof(TestOrderService).Assembly];

        var options = Microsoft.Extensions.Options.Options.Create(new MentorOptions
        {
            AppName = "DiscoveryTest",
            ScanAssemblies = assemblies,
        });

        return new MentorDiscoveryService(options, NullLogger<MentorDiscoveryService>.Instance).Discover();
    }

    private static MethodDiscoveryInfo Tool(string name)
        => Discover().Level1Methods.Single(m => m.ToolName == name);

    // ── What becomes a tool ─────────────────────────────────────────────────────

    [Fact]
    public void A_method_with_MentorAction_becomes_a_tool_named_by_the_rule()
    {
        // CancelOrderAsync → cancel_order: PascalCase, Async stripped, snake_cased. The attribute
        // deliberately has no Name parameter, so the method name is the contract.
        Discover().Level1Methods.Select(m => m.ToolName).Should().Contain("cancel_order");
    }

    [Fact]
    public void A_method_with_only_a_Description_attribute_also_becomes_a_tool()
    {
        // The documented "no extra dependencies" path: plain [Description] from
        // System.ComponentModel works without referencing any MentorAgent attribute.
        var byDescription = Discover().Level1Methods
            .Where(m => m.Category is null && m.RequiredRoles.Length == 0 && !m.RequiresConfirmation)
            .Select(m => m.ToolName);

        byDescription.Should().NotBeEmpty("[Description] alone has to register a tool");
    }

    [Fact]
    public void A_method_with_neither_attribute_is_not_a_tool()
    {
        // The fixture's NotATool exists for this. Exposing an undecorated public method would make
        // every service's whole surface callable by the model.
        Discover().Level1Methods.Select(m => m.ToolName).Should().NotContain("not_a_tool");
    }

    [Fact]
    public void An_assembly_with_no_mentor_attributes_yields_nothing()
    {
        // The control, and the shape the "nothing declared" constraint tests rely on.
        var result = Discover(typeof(Novior.MentorAgent.Abstractions.Models.MentorCard));

        result.Level1Methods.Should().BeEmpty();
        result.Level2Agents.Should().BeEmpty();
        result.Level3Teams.Should().BeEmpty();
    }

    [Fact]
    public void Discovery_is_cached_so_repeated_calls_return_the_same_catalogue()
    {
        // Called on every turn by the middleware's role lookup. Re-scanning assemblies per turn
        // would be a per-message reflection cost nobody would spot in a profiler until production.
        var options = Microsoft.Extensions.Options.Options.Create(new MentorOptions
        {
            AppName = "DiscoveryTest",
            ScanAssemblies = [typeof(TestOrderService).Assembly],
        });
        var service = new MentorDiscoveryService(options, NullLogger<MentorDiscoveryService>.Instance);

        service.Discover().Should().BeSameAs(service.Discover());
    }

    // ── Every [MentorAction] parameter survives ─────────────────────────────────

    [Fact]
    public void All_six_action_parameters_reach_the_catalogue()
    {
        // The fixture's cancel_order sets every one at once, which is the only way to catch a
        // parameter that is read at declaration and dropped in the record.
        var tool = Tool("cancel_order");

        tool.Description.Should().Be("Cancels an existing order");
        tool.Category.Should().Be("Orders");
        tool.RequiresConfirmation.Should().BeTrue();
        tool.RequiredRoles.Should().Equal(["Manager"]);
        tool.ProactiveHint.Should().Be("Suggest checking active orders first");
        tool.NavigateTo.Should().Be("/orders");
    }

    [Fact]
    public void The_two_gates_are_independent_of_each_other()
    {
        // Confirmation and roles are separate controls and are easy to conflate — the fixture
        // declares one of each on purpose.
        var confirmationOnly = Tool("issue_refund");
        confirmationOnly.RequiresConfirmation.Should().BeTrue();
        confirmationOnly.RequiredRoles.Should().BeEmpty();

        var rolesOnly = Tool("export_ledger");
        rolesOnly.RequiresConfirmation.Should().BeFalse();
        rolesOnly.RequiredRoles.Should().Equal(["Admin"]);
    }

    [Fact]
    public void An_ungated_action_carries_neither_control()
    {
        var tool = Tool("count_open_orders");

        tool.RequiresConfirmation.Should().BeFalse();
        tool.RequiredRoles.Should().BeEmpty();
        tool.NavigateTo.Should().BeNull();
    }

    [Fact]
    public void Optional_action_parameters_default_to_the_documented_values()
    {
        // A tool that sets only Description must not inherit anything surprising: defaulting
        // RequiresConfirmation to true would block every action, defaulting RequiredRoles to null
        // would throw in the role lookup.
        var tool = Tool("count_open_orders");

        tool.RequiredRoles.Should().NotBeNull();
        tool.ProactiveHint.Should().BeNull();
    }

    // ── Attribute declarations themselves ───────────────────────────────────────

    [Fact]
    public void Every_attribute_targets_what_the_documentation_says()
    {
        // Targeting is what makes a misplaced attribute a compile error rather than a silent no-op.
        Usage<MentorActionAttribute>().ValidOn.Should().Be(AttributeTargets.Method);
        Usage<MentorPageAttribute>().ValidOn.Should().Be(AttributeTargets.Class);
        Usage<MentorAgentAttribute>().ValidOn.Should().Be(AttributeTargets.Class);
        Usage<MentorTeamAttribute>().ValidOn.Should().Be(AttributeTargets.Class);
        Usage<MentorSkillAttribute>().ValidOn.Should().Be(AttributeTargets.Class);
        Usage<TeamMemberAttribute>().ValidOn.Should().Be(AttributeTargets.Property);
    }

    [Fact]
    public void No_mentor_attribute_can_be_applied_twice_to_the_same_target()
    {
        // Two [MentorAction] on one method would give one method two names and two gates, and
        // discovery reads only the first.
        Usage<MentorActionAttribute>().AllowMultiple.Should().BeFalse();
        Usage<MentorPageAttribute>().AllowMultiple.Should().BeFalse();
        Usage<MentorAgentAttribute>().AllowMultiple.Should().BeFalse();
        Usage<MentorTeamAttribute>().AllowMultiple.Should().BeFalse();
        Usage<MentorSkillAttribute>().AllowMultiple.Should().BeFalse();
        Usage<TeamMemberAttribute>().AllowMultiple.Should().BeFalse();
    }

    [Fact]
    public void Only_MentorAction_is_inherited_by_overrides()
    {
        // An action declared on a base class stays an action when overridden — otherwise a service
        // hierarchy silently loses its tools. Class-level attributes are not inherited, so a
        // derived page or agent has to declare its own identity rather than duplicating its base's.
        Usage<MentorActionAttribute>().Inherited.Should().BeTrue();
        Usage<MentorPageAttribute>().Inherited.Should().BeFalse();
        Usage<MentorAgentAttribute>().Inherited.Should().BeFalse();
        Usage<MentorTeamAttribute>().Inherited.Should().BeFalse();
    }

    [Fact]
    public void The_page_attribute_defaults_match_the_documentation()
    {
        var page = new MentorPageAttribute { Url = "/x", Name = "X" };

        page.HasUIActions.Should().BeFalse("UI actions are opt-in; scanning for them costs a round trip");
        page.ReadyTimeout.Should().Be(2000);
        page.Description.Should().BeNull();
    }

    [Fact]
    public void The_team_attribute_defaults_match_the_documentation()
    {
        var team = new MentorTeamAttribute { Name = "T", Description = "D" };

        team.MaxIterations.Should().Be(10, "an unbounded group chat is an unbounded bill");
        team.TriggerOn.Should().BeEmpty();
        team.HandoffTo.Should().BeEmpty();
    }

    [Fact]
    public void The_agent_attribute_defaults_match_the_documentation()
    {
        var agent = new MentorAgentAttribute { Name = "A", Description = "D" };

        agent.HandoffTo.Should().BeEmpty();
        agent.Instructions.Should().BeNull();
    }

    // ── Pages ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Every_page_attribute_field_reaches_the_catalogue()
    {
        var pages = Discover().Pages;

        pages.Should().NotBeEmpty();
        var orders = pages.Single(p => p.Name == "Orders");
        orders.Url.Should().Be("/orders");
        orders.HasUIActions.Should().BeTrue();
    }

    [Fact]
    public void A_page_without_ui_actions_is_recorded_as_such()
    {
        // The flag decides whether the widget waits for SignalReady() before a turn. Getting it
        // wrong in either direction costs either a stall or a stale page context.
        var reports = Discover().Pages.Single(p => p.Name == "Reports");

        reports.HasUIActions.Should().BeFalse();
    }

    // ── Levels 2 and 3 ──────────────────────────────────────────────────────────

    [Fact]
    public void A_MentorAgent_class_becomes_a_level_2_agent_with_its_own_tools()
    {
        var agent = Discover().Level2Agents.Single();

        agent.Attribute.Name.Should().NotBeNullOrWhiteSpace();
        agent.Methods.Should().NotBeEmpty("an agent with no tools can only talk");
    }

    [Fact]
    public void An_agents_methods_are_not_also_registered_as_level_1_tools()
    {
        // A specialist's tools belong to the specialist. Promoting them to the coordinator would
        // let the model bypass the handoff and lose the routing the agent exists to provide.
        var result = Discover();

        var agentTools = result.Level2Agents.SelectMany(a => a.Methods).Select(m => m.ToolName).ToHashSet();
        var l1 = result.Level1Methods.Select(m => m.ToolName).ToHashSet();

        string.Join(", ", agentTools.Intersect(l1)).Should().BeEmpty();
    }

    [Fact]
    public void A_team_with_members_is_discovered_with_all_of_them()
    {
        var team = Discover().Level3Teams.Single();

        team.Members.Should().HaveCountGreaterThan(1, "a team of one is an agent");
        team.Members.Should().OnlyContain(m => !string.IsNullOrWhiteSpace(m.Attribute.Role));
    }

    [Fact]
    public void A_team_with_no_members_is_dropped_at_discovery_rather_than_half_built()
    {
        // Discovery itself skips it and warns (the warning is asserted in the constraint suite).
        // Dropping it here rather than later is the right call: a team record with an empty member
        // list would have to be defended against everywhere downstream.
        Discover().Level3Teams.Select(t => t.Type.Name).Should().NotContain(nameof(TestEmptyTeam));
        Discover().Level3Teams.Should().OnlyContain(t => t.Members.Count > 0);
    }

    // ── Skills ──────────────────────────────────────────────────────────────────

    [Fact]
    public void A_skill_with_methods_registers_them_as_level_1_tools()
    {
        // The documented dual role: the skill supplies the "how", its methods the "do".
        Discover().Level1Methods.Select(m => m.ToolName).Should().Contain("submit_expense_report");
    }

    [Fact]
    public void A_skills_methods_are_registered_exactly_once()
    {
        // The early return in the skill branch exists to stop the generic L1 block scanning the
        // same type again. A duplicate tool name is not an error — it is two identical entries in
        // the tool list and doubled metrics.
        var names = Discover().Level1Methods.Select(m => m.ToolName).ToList();

        string.Join(", ", names.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key))
            .Should().BeEmpty("a tool discovered twice is sent to the model twice");
    }

    [Fact]
    public void A_knowledge_only_skill_contributes_no_tools_and_no_error()
    {
        // The documented second form: a [MentorSkill] class with no methods at all is valid — it
        // is pure instructions loaded on demand.
        var act = () => Discover();

        act.Should().NotThrow();
    }

    // ── Robustness ──────────────────────────────────────────────────────────────

    [Fact]
    public void Scanning_the_same_assembly_twice_does_not_double_the_catalogue()
    {
        // BUG-011. Two entries resolving to the same assembly registered every tool, agent and
        // team twice, and the duplicate handoff then made the Agent Framework throw — killing the
        // coordinator outright, with zero tools and no reply.
        var result = DiscoverWith(
            [typeof(TestOrderService).Assembly, typeof(TestOrderService).Assembly],
            out _);

        var duplicates = result.Level1Methods.GroupBy(m => m.ToolName).Where(g => g.Count() > 1).Select(g => g.Key);
        string.Join(", ", duplicates).Should().BeEmpty(
            "a host listing an assembly twice — easy with a hand-written marker type — must not " +
            "double every tool");

        result.Level2Agents.Should().HaveCount(1);
        result.Level3Teams.Should().HaveCount(1);
        result.Pages.Should().HaveCount(2);
    }

    [Fact]
    public void A_duplicated_assembly_is_reported_rather_than_silently_swallowed()
    {
        // Deduplicating quietly would hide the more interesting case: the developer meant to name
        // a *different* project and picked a marker type from this one. The message is what tells
        // them their two entries are the same assembly.
        DiscoverWith([typeof(TestOrderService).Assembly, typeof(TestOrderService).Assembly], out var logs);

        logs.HasWarning("lists the same assembly more than once").Should().BeTrue(
            "warnings seen:{0}{1}", Environment.NewLine, logs.WarningText);
        logs.WarningText.Should().Contain("Novior.MentorAgent.Tests.App", "the message has to name which one");
    }

    [Fact]
    public void A_normal_single_assembly_list_produces_no_duplicate_warning()
    {
        DiscoverWith([typeof(TestOrderService).Assembly], out var logs);

        logs.HasWarning("lists the same assembly more than once").Should().BeFalse();
    }

    [Fact]
    public async Task A_duplicated_assembly_still_produces_a_working_coordinator()
    {
        // The damage BUG-011 actually did, asserted where it was felt rather than in the
        // catalogue. Before the fix this turn produced: zero tools, no reply, no error returned to
        // the caller, and one Error-level log line naming an Agent Framework internal
        // ("A handoff from agent 'ShippingAgent' to agent 'main_coordinator' has already been
        // registered") that pointed at nothing the developer had written. The widget showed a user
        // bubble and then silence.
        await using var host = MentorHostFixture.Create(o =>
            o.ScanAssemblies = [typeof(TestOrderService).Assembly, typeof(TestOrderService).Assembly]);

        var error = await host.RunTurnAsync("hello");

        error.Should().BeNull();
        host.FirstToolsSent.Should().NotBeEmpty("a coordinator that built with no tools is a dead assistant");
        host.Logs.ExceptionText.Should().NotContain("already been registered");
        host.Logs.Records.Select(r => r.ToString())
            .Should().NotContain(r => r.Contains("Initialization failed"));
    }

    [Fact]
    public void The_discovery_log_is_in_English()
    {
        // The two lines that open and close the discovery were the only Italian in the log of an English library
        // ("Discovery avviata su …", "Discovery completata …").
        DiscoverWith([typeof(TestOrderService).Assembly], out var logs);

        var lines = logs.Records.Select(r => r.Message).ToList();
        lines.Should().Contain(l => l.StartsWith("[MentorAgent] Discovery started on 1 assembly(ies): ", StringComparison.Ordinal));
        lines.Should().Contain(l => l.StartsWith("[MentorAgent] Discovery completed — L1 tools: ", StringComparison.Ordinal));
        lines.Should().NotContain(l => l.Contains("avviata", StringComparison.Ordinal) || l.Contains("completata", StringComparison.Ordinal));
    }

    private static DiscoveryResult DiscoverWith(System.Reflection.Assembly[] assemblies, out TestLogCollector logs)
    {
        logs = new TestLogCollector();
        var options = Microsoft.Extensions.Options.Options.Create(new MentorOptions
        {
            AppName = "DiscoveryTest",
            ScanAssemblies = assemblies,
        });

        var logger = new TestLoggerProvider(logs).CreateLogger(typeof(MentorDiscoveryService).FullName!);
        return new MentorDiscoveryService(options, new TypedLogger<MentorDiscoveryService>(logger)).Discover();
    }

    /// <summary>Adapts a plain <see cref="ILogger"/> to <see cref="ILogger{T}"/>.</summary>
    private static AttributeUsageAttribute Usage<T>() where T : Attribute
        => typeof(T).GetCustomAttributes(typeof(AttributeUsageAttribute), false)
            .Cast<AttributeUsageAttribute>().Single();
}
