using System.Reflection;
using FluentAssertions;
using Novior.MentorAgent.Abstractions.Core;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Middleware;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.Harness;
using Novior.MentorAgent.Tests.Rules;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Novior.MentorAgent.Tests.Discovery;

/// <summary>
/// The rules of ACTION-RULES.md that no other suite fixed, on shapes an application should not write (story 1273). The
/// shapes live in Novior.MentorAgent.Tests.Rules, which only these tests scan: a class that is both a team and an agent
/// would change the catalogue every other suite reads. Each test names its rule with a <c>Rule</c> trait, which
/// <c>ActionRulesDocumentTests</c> checks against the list.
/// </summary>
public sealed class ActionRulesTests
{
    private static DiscoveryResult Discover()
        => new MentorDiscoveryService(
            Microsoft.Extensions.Options.Options.Create(new MentorOptions
            {
                AppName = "ActionRules",
                ScanAssemblies = [typeof(MethodShapeActions).Assembly],
            }),
            NullLogger<MentorDiscoveryService>.Instance).Discover();

    private static IEnumerable<MethodDiscoveryInfo> ToolsOf<T>(DiscoveryResult result)
        => result.Level1Methods.Where(m => m.DeclaringType == typeof(T));

    // ── scan.* ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Rule", "scan.concrete-classes")]
    public void Only_concrete_classes_are_read()
    {
        var names = Discover().Level1Methods.Select(m => m.ToolName).ToList();

        names.Should().NotContain("action_on_abstract_class").And.NotContain("action_on_struct");
        names.Should().Contain("public_instance_action", "the control: a concrete class is read");
    }

    [Fact]
    [Trait("Rule", "scan.public-instance-declared")]
    public void Only_public_instance_methods_of_the_class_itself_become_actions()
    {
        var result = Discover();

        ToolsOf<MethodShapeActions>(result).Select(m => m.ToolName)
            .Should().Equal(["public_instance_action"], "static, internal, protected and private methods are not actions");
        ToolsOf<DerivedRuleActions>(result).Select(m => m.ToolName)
            .Should().NotContain("inherited_not_overridden", "a method the class inherits is its base class's action");
        ToolsOf<BaseRuleActions>(result).Select(m => m.ToolName).Should().Contain("inherited_not_overridden");
    }

    [Fact]
    [Trait("Rule", "scan.override-keeps-action")]
    public void An_override_of_an_action_is_an_action_of_the_class_that_overrides_it()
    {
        var derived = ToolsOf<DerivedRuleActions>(Discover()).ToDictionary(m => m.ToolName);

        derived.Should().ContainKey("overridden_mentor_action");
        derived["overridden_mentor_action"].IsMentorAction.Should().BeTrue();
        derived["overridden_mentor_action"].Description.Should().Be("An action the derived class overrides");
        derived.Should().ContainKey("overridden_described_method");
        derived["overridden_described_method"].IsMentorAction.Should().BeFalse();
        derived["overridden_described_method"].Description.Should().Be("A described method the derived class overrides");
    }

    [Fact]
    [Trait("Rule", "scan.marker")]
    public void A_Description_without_text_or_no_attribute_makes_no_action()
    {
        var names = ToolsOf<MarkerActions>(Discover()).Select(m => m.ToolName).ToList();

        names.Should().NotContain("empty_description_method")
            .And.NotContain("blank_description_method")
            .And.NotContain("unmarked_method");
    }

    [Fact]
    [Trait("Rule", "scan.marker")]
    public void A_MentorAction_makes_an_action_whatever_its_description()
    {
        var action = ToolsOf<MarkerActions>(Discover()).Single(m => m.ToolName == "empty_mentor_action_description");

        action.IsMentorAction.Should().BeTrue();
        action.Description.Should().BeEmpty();
    }

    [Fact]
    [Trait("Rule", "scan.mentor-action-wins")]
    public void With_both_attributes_MentorAction_decides()
    {
        var action = ToolsOf<MarkerActions>(Discover()).Single(m => m.ToolName == "both_attributes_method");

        action.IsMentorAction.Should().BeTrue();
        action.Description.Should().Be("What MentorAction says");
        action.RequiresConfirmation.Should().BeTrue();
        action.RequiredRoles.Should().Equal("Clerk");
    }

    // ── confirm.* ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Rule", "confirm.description-only-not-defaulted")]
    public void A_method_with_only_a_Description_does_not_get_the_default_confirmation()
    {
        // RequireConfirmationForWrites is on by default: a [MentorAction] that declares nothing asks, a [Description] does not.
        var result = Discover();

        ToolsOf<DerivedRuleActions>(result).Single(m => m.ToolName == "overridden_described_method")
            .RequiresConfirmation.Should().BeFalse();
        ToolsOf<DerivedRuleActions>(result).Single(m => m.ToolName == "overridden_mentor_action")
            .RequiresConfirmation.Should().BeTrue("the control: the same shape with [MentorAction] asks");
    }

    // ── kind.* ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Rule", "kind.one-per-class")]
    public void A_class_that_is_a_team_and_an_agent_is_a_team()
    {
        var result = Discover();

        result.Level3Teams.Select(t => t.Type).Should().Contain(typeof(TeamAndAgentClass));
        result.Level2Agents.Select(a => a.Type).Should().NotContain(typeof(TeamAndAgentClass));
        ToolsOf<TeamAndAgentClass>(result).Should().BeEmpty("a team's own methods are not the coordinator's actions");
    }

    [Fact]
    [Trait("Rule", "kind.one-per-class")]
    public void A_class_that_is_an_agent_and_a_skill_is_an_agent()
    {
        var result = Discover();

        result.Level2Agents.Single(a => a.Type == typeof(AgentAndSkillClass)).Methods.Select(m => m.ToolName)
            .Should().Equal("method_of_agent_and_skill_class");
        ToolsOf<AgentAndSkillClass>(result).Should().BeEmpty("the agent's method is not also a skill's level-1 action");
        ToolsOf<SkillOnlyClass>(result).Select(m => m.ToolName)
            .Should().Equal(["method_of_skill_class"], "the control: a skill alone gives its methods to the coordinator");
    }

    [Fact]
    [Trait("Rule", "kind.page-is-separate")]
    public void A_page_keeps_its_class_whatever_else_it_is()
    {
        var result = Discover();

        result.Pages.Select(p => p.Url).Should().Contain(["/rules/page", "/rules/agent-page"]);
        ToolsOf<PageWithActions>(result).Select(m => m.ToolName).Should().Equal("method_of_page_class");
        result.Level2Agents.Select(a => a.Type).Should().Contain(typeof(PageThatIsAnAgent));
    }

    // ── roles.* ─────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [Trait("Rule", "roles.any-of")]
    [InlineData(new[] { "Admin" }, MentorActionReasons.Allowed)]
    [InlineData(new[] { "OrderManager" }, MentorActionReasons.Allowed)]
    [InlineData(new[] { "Clerk", "Admin" }, MentorActionReasons.Allowed)]
    [InlineData(new[] { "Clerk" }, MentorActionReasons.RoleMissing)]
    [InlineData(new[] { "admin" }, MentorActionReasons.RoleMissing)]
    public async Task One_of_the_roles_is_enough_and_names_match_as_IsInRole_matches_them(string[] roles, string expected)
    {
        var auth = new TestAuthStateProvider();
        auth.SignIn("ada", roles);
        var options = Microsoft.Extensions.Options.Options.Create(new MentorOptions
        {
            AppName = "ActionRules",
            ScanAssemblies = [typeof(RoleRuleActions).Assembly],
            EnableActionFeedback = false,
        });
        var discovery = new MentorDiscoveryService(options, NullLogger<MentorDiscoveryService>.Instance);
        var gate = new MentorAgentUIMiddleware(
            Substitute.For<IMentorStateService>(),
            Substitute.For<IApprovalStore>(),
            options,
            discovery,
            new MentorConfirmationBuilder(discovery, options),
            new MentorMetrics(new MentorTokenUsage(), options),
            new MentorLocalizer(MentorLanguage.English),
            NullLogger<MentorAgentUIMiddleware>.Instance,
            auth);

        var check = await gate.CheckRolesAsync("approve_rule_refund", CancellationToken.None,
            typeof(RoleRuleActions).GetMethod(nameof(RoleRuleActions.ApproveRuleRefund), BindingFlags.Public | BindingFlags.Instance));

        check.Reason.Should().Be(expected);
    }
}
