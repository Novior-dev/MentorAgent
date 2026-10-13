using FluentAssertions;
using Novior.MentorAgent.Attributes;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Models;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.App;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Novior.MentorAgent.Tests.Discovery;

/// <summary>
/// The tools of a <c>[TeamMember]</c>: whose they are, which methods count, and what the member is told.
///
/// <para>
/// Up to rc.14 a class listed in <c>[TeamMember(Tools = ...)]</c> was also scanned as level 1 — and the
/// README asks for it in DI — so its methods became coordinator actions too, on /mcp and the A2A card
/// as well. Members were discovered with <c>GetMethods()</c>: inherited and static methods and an empty
/// <c>[Description]</c> became tools, which levels 1 and 2 refuse. And every member was told "These tools
/// are read-only." whatever its tools did.
/// </para>
/// </summary>
public sealed class TeamMemberToolTests
{
    private static DiscoveryResult Discover()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new MentorOptions
        {
            AppName = "TeamMemberToolTest",
            ScanAssemblies = [typeof(TestOrderService).Assembly],
        });
        return new MentorDiscoveryService(options, NullLogger<MentorDiscoveryService>.Instance).Discover();
    }

    private static TeamMemberInfo Member(DiscoveryResult result, string role)
        => result.Level3Teams.Single().Members.Single(m => m.Attribute.Role == role);

    private static MentorPromptBuilder Builder(Action<MentorOptions>? configure = null)
    {
        var options = new MentorOptions { AppName = "TeamMemberToolTest", ScanAssemblies = [typeof(TestOrderService).Assembly] };
        configure?.Invoke(options);
        return new MentorPromptBuilder(Microsoft.Extensions.Options.Options.Create(options));
    }

    // ── Whose tools ─────────────────────────────────────────────────────────────

    [Fact]
    public void A_class_listed_only_as_member_tools_is_not_a_coordinator_tool()
    {
        var result = Discover();

        // Level 1 is what the coordinator, /mcp and the A2A card are built from.
        result.Level1Methods.Should().NotContain(m => m.DeclaringType == typeof(TestAnalysisTools));
        result.Level1Methods.Select(m => m.ToolName).Should().Contain("cancel_order", "the control: level 1 is intact");
        Member(result, "Data Analyst").Tools.Select(t => t.ToolName)
            .Should().BeEquivalentTo(["read_quarter_revenue", "approve_budget_line"], "they are the member's");
    }

    [Fact]
    public async Task A_member_tool_class_registered_in_DI_is_not_offered_to_the_coordinator()
    {
        // The README asks for the class in DI; that is the case that used to turn it into coordinator
        // actions. The fixture registers TestAnalysisTools.
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false);
        host.Chat!.Say("ok");

        (await host.RunTurnAsync("hello")).Should().BeNull();

        host.FirstToolsSent.Should().Contain("business_analysis_team")
            .And.NotContain("read_quarter_revenue")
            .And.NotContain("approve_budget_line");
    }

    [Fact]
    public void Each_class_taken_from_the_coordinator_is_logged_once_with_its_member_and_methods()
    {
        // For a host upgrading from rc.14 this is a silent loss of coordinator tools otherwise — the
        // samples' ReportTools was exactly that case.
        var logs = new TestLogCollector();
        var options = Microsoft.Extensions.Options.Options.Create(new MentorOptions
        {
            AppName = "TeamMemberToolTest",
            ScanAssemblies = [typeof(TestOrderService).Assembly],
        });
        var logger = new TestLoggerProvider(logs).CreateLogger(typeof(MentorDiscoveryService).FullName!);
        new MentorDiscoveryService(options, new TypedLogger<MentorDiscoveryService>(logger)).Discover();

        var lines = logs.Records
            .Where(r => r.Level == Microsoft.Extensions.Logging.LogLevel.Information
                        && r.Message.Contains("is listed in the Tools of"))
            .Select(r => r.Message)
            .ToList();

        lines.Should().ContainSingle("one line per class taken from level 1, and the skill is not one");
        lines[0].Should().Contain("TestAnalysisTools")
            .And.Contain("'Data Analyst' (Business Analysis Team)")
            .And.Contain("read_quarter_revenue").And.Contain("approve_budget_line")
            .And.Contain("not the coordinator's");
    }

    [Fact]
    public void A_skill_listed_as_member_tools_keeps_its_level_1_tools()
    {
        var result = Discover();

        result.Level1Methods.Should().Contain(m => m.DeclaringType == typeof(TestExpenseSkill),
            "a skill's methods are tools of the coordinator by the skill's own documented role");
        Member(result, "Strategist").Tools.Select(t => t.ToolName)
            .Should().BeEquivalentTo(["submit_expense_report", "get_policy_for_category"]);
    }

    // ── Which methods ───────────────────────────────────────────────────────────

    [Fact]
    public void Member_tools_follow_the_level_1_rules()
    {
        var names = Member(Discover(), "Data Analyst").Tools.Select(t => t.ToolName).ToList();

        names.Should().NotContain("inherited_helper", "an inherited method is not discovered at any level");
        names.Should().NotContain("static_helper", "nor a static one");
        names.Should().NotContain("empty_description", "nor a [Description] with no text");
    }

    [Fact]
    public void A_member_tool_keeps_what_its_attribute_declares()
    {
        var tools = Member(Discover(), "Data Analyst").Tools;

        tools.Single(t => t.ToolName == "read_quarter_revenue").ReadOnly.Should().BeTrue();
        var write = tools.Single(t => t.ToolName == "approve_budget_line");
        write.RequiresConfirmation.Should().BeTrue();
        write.RequiredRoles.Should().Equal("Manager");
        write.IsMentorAction.Should().BeTrue();
    }

    // ── What the member is told ─────────────────────────────────────────────────

    [Fact]
    public void A_member_with_a_write_is_not_told_its_tools_are_read_only()
    {
        var text = Builder().BuildTeamMemberInstructions(Member(Discover(), "Data Analyst"));

        text.Should().Contain("read_quarter_revenue").And.NotContain("These tools are read-only.");
    }

    [Fact]
    public void A_member_whose_tools_all_only_read_is_told_so()
    {
        var analyst = Member(Discover(), "Data Analyst");
        var readsOnly = analyst with { Tools = [.. analyst.Tools.Where(t => t.ReadOnly)] };

        Builder().BuildTeamMemberInstructions(readsOnly).Should().Contain("These tools are read-only.");
    }

    [Fact]
    public void A_member_tool_declared_read_only_by_name_counts()
    {
        // [Description] methods are declared read-only with IsReadOnlyAction.
        var strategist = Member(Discover(), "Strategist");

        Builder().BuildTeamMemberInstructions(strategist).Should().NotContain("These tools are read-only.");
        Builder(o => o.IsReadOnlyAction = name => name is "submit_expense_report" or "get_policy_for_category")
            .BuildTeamMemberInstructions(strategist).Should().Contain("These tools are read-only.");
    }

    [Fact]
    public void A_confirmation_wins_over_a_read_only_claim()
    {
        var analyst = Member(Discover(), "Data Analyst");
        var readTool = analyst.Tools.Single(t => t.ToolName == "read_quarter_revenue");
        var gatedByName = analyst with { Tools = [readTool] };

        Builder(o => o.RequiresApproval = name => name == "read_quarter_revenue")
            .BuildTeamMemberInstructions(gatedByName).Should().NotContain("These tools are read-only.");
    }
}
