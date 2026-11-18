using FluentAssertions;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Declarative;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Tests.App;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Novior.MentorAgent.Tests.Declarative;

/// <summary>
/// Story 1570, slice B — the teams written in YAML (<c>*.team.yaml</c>, <see cref="MentorDeclarativeOptions.TeamDefinitions"/>):
/// read with YamlDotNet's node model into the same <see cref="MentorTeamDefinition"/> an <see cref="IMentorTeamSource"/>
/// gives, checked by the core as any source's, and a file that does not hold left out with its line and column.
/// </summary>
public sealed class TeamYamlTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "mentoragent-teams-" + Guid.NewGuid().ToString("N")[..8]);

    public TeamYamlTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private const string OrderReview = """
        kind: Team
        name: Order Review
        description: "Reviews an order: before anything is done with it."
        triggerOn: [review, check]
        maxIterations: 4
        handoffTo:
          - Shipping
        approver: Approver
        members:
          - role: Clerk
            instructions: |
              Look at the order.
              Then say what you saw.
            tools: [count_open_orders]
          - role: Approver
            instructions: Decide. Reply with the verdict.
        """;

    private static string Fault(string yaml)
    {
        var act = () => DeclarativeTeamSource.Read(yaml);
        return act.Should().Throw<TeamFileException>().Which.Message;
    }

    private sealed class TeamObserver : IMentorAgentSourceObserver
    {
        public List<MentorAgentSourceReport> Teams { get; } = [];
        public void OnAgentsLoaded(MentorAgentSourceReport report) { }
        public void OnTeamsLoaded(MentorAgentSourceReport report) { lock (Teams) Teams.Add(report); }
    }

    // ── team.from-yaml: what a file says ───────────────────────────────────────

    [Fact]
    [Trait("Rule", "team.from-yaml")]
    public void A_team_file_reads_into_the_definition_a_source_gives()
    {
        var team = DeclarativeTeamSource.Read(OrderReview);

        team.Name.Should().Be("Order Review");
        team.Description.Should().Be("Reviews an order: before anything is done with it.");
        team.TriggerOn.Should().Equal("review", "check");
        team.MaxIterations.Should().Be(4);
        team.HandoffTo.Should().Equal("Shipping");
        team.Approver.Should().Be("Approver");
        team.Members.Select(m => m.Role).Should().Equal("Clerk", "Approver");
        team.Members[0].Instructions.Should().Be("Look at the order.\nThen say what you saw.\n");
        team.Members[0].Tools.Should().Equal("count_open_orders");
        team.Members[1].Tools.Should().BeEmpty();
    }

    [Fact]
    [Trait("Rule", "team.from-yaml")]
    public void What_a_file_leaves_out_takes_the_defaults_and_the_core_checks_the_rest()
    {
        var team = DeclarativeTeamSource.Read("""
            name: Lean
            members:
              - role: Solo
                instructions: Do it.
            """);

        team.Description.Should().BeEmpty("missing: the core leaves it out with invalid-definition");
        team.MaxIterations.Should().Be(10);
        team.Approver.Should().BeNull();
        team.TriggerOn.Should().BeEmpty();
        DeclarativeTeamSource.Read("name: Empty").Members.Should().BeEmpty("the core leaves it out with no-members");
    }

    [Theory]
    [Trait("Rule", "team.from-yaml")]
    [InlineData("name: x\nmembres:\n  - role: A\n", "line 2, column 1: unknown key 'membres' in a team")]
    [InlineData("name: x\nmembers:\n  - role: A\n    instructions: B\n    tool: [c]\n", "line 5, column 5: unknown key 'tool' in a member")]
    [InlineData("name: x\nname: y\n", "line 2, column 1:")]
    [InlineData("name: x\nmembers:\n  - role: [unclosed\n", "line ")]
    [InlineData("name: x\nmembers: Clerk\n", "line 2, column 10: members is a list")]
    [InlineData("name: x\nmaxIterations: four\n", "line 2, column 16: maxIterations is a whole number")]
    [InlineData("name: [a, b]\n", "line 1, column 7: name is a text")]
    [InlineData("name: x\nmembers:\n  - Clerk\n", "line 3, column 5: a member is a mapping")]
    [InlineData("kind: Prompt\nname: x\n", "line 1, column 7: kind is Team")]
    [InlineData("- just\n- a list\n", "line 1, column 1: a team is a mapping")]
    [InlineData("name: a\n---\nname: b\n", "line 3, column 1: one team per file")]
    [InlineData("# nothing\n", "the file has no team")]
    public void A_file_that_does_not_hold_says_where_and_what(string yaml, string expected)
        => Fault(yaml).Should().StartWith(expected);

    [Fact]
    [Trait("Rule", "team.from-yaml")]
    public void A_key_written_twice_is_refused_by_the_parser_not_overwritten()
        => Fault("name: First\ndescription: d\nname: Second\n").Should().StartWith("line 3, column 1:").And.Contain("Duplicate key");

    // ── The files of the folder ────────────────────────────────────────────────

    private (MentorTeamSourceResult Result, TestLogCollector Logs) LoadTeams(MentorDeclarativeOptions options)
    {
        var logs = new TestLogCollector();
        var factory = LoggerFactory.Create(b => b.AddProvider(new TestLoggerProvider(logs)));
        var result = new DeclarativeTeamSource(options, factory).LoadAsync(new MentorTeamSourceContext()).GetAwaiter().GetResult();
        return (result, logs);
    }

    [Fact]
    [Trait("Rule", "team.from-yaml")]
    public void The_team_files_of_the_folder_and_the_inline_ones_are_read_and_a_broken_one_is_left_out_with_its_place()
    {
        File.WriteAllText(Path.Combine(_root, "review.team.yaml"), OrderReview);
        File.WriteAllText(Path.Combine(_root, "broken.team.yaml"), "name: Broken\nmembres: []\n");
        File.WriteAllText(Path.Combine(_root, "shipping.agent.yaml"), "kind: GptComponentMetadata\nname: Shipping\n");
        var options = new MentorDeclarativeOptions { Directory = _root };
        options.TeamDefinitions.Add("name: Inline\ndescription: d\nmembers:\n  - role: A\n    instructions: B\n");

        var (result, logs) = LoadTeams(options);

        result.Teams.Select(t => t.Name).Should().Equal("Inline", "Order Review");
        var rejected = result.Rejected.Should().ContainSingle().Subject;
        rejected.Name.Should().Be("broken.team.yaml");
        rejected.ReasonCode.Should().Be(MentorAgentRejectionReasons.InvalidDefinition);
        rejected.Detail.Should().Be("line 2, column 1: unknown key 'membres' in a team; the keys are kind, name, description, triggerOn, maxIterations, handoffTo, approver, members");
        logs.Records.Should().Contain(r => r.Level == LogLevel.Warning && r.Message.Contains("broken.team.yaml"));
    }

    [Fact]
    [Trait("Rule", "team.from-yaml")]
    public void A_team_file_is_never_read_as_an_agent_even_by_a_wide_pattern()
    {
        File.WriteAllText(Path.Combine(_root, "review.team.yaml"), OrderReview);
        File.WriteAllText(Path.Combine(_root, "shipping.yaml"), "kind: GptComponentMetadata\nname: Shipping\n");
        var options = new MentorDeclarativeOptions { Directory = _root, SearchPattern = "*.yaml" };

        var agents = typeof(DeclarativeAgentSource)
            .GetMethod("LoadDefinitions", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(new DeclarativeAgentSource(options, LoggerFactory.Create(_ => { })), null) as List<(string Name, string Yaml)>;

        agents!.Select(a => a.Name).Should().Equal("shipping.yaml");
        LoadTeams(options).Result.Teams.Should().ContainSingle(t => t.Name == "Order Review");
    }

    [Fact]
    [Trait("Rule", "team.from-yaml")]
    public void TeamSearchPattern_chooses_the_team_files_and_the_agents_leave_them_alone()
    {
        File.WriteAllText(Path.Combine(_root, "review.crew.yaml"), OrderReview);
        File.WriteAllText(Path.Combine(_root, "other.team.yaml"), OrderReview.Replace("Order Review", "Other"));
        var options = new MentorDeclarativeOptions { Directory = _root, SearchPattern = "*.yaml", TeamSearchPattern = "*.crew.yaml" };

        LoadTeams(options).Result.Teams.Select(t => t.Name).Should().Equal("Order Review");
        var agents = typeof(DeclarativeAgentSource)
            .GetMethod("LoadDefinitions", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(new DeclarativeAgentSource(options, LoggerFactory.Create(_ => { })), null) as List<(string Name, string Yaml)>;
        agents!.Select(a => a.Name).Should().Equal(["other.team.yaml"], "with another pattern, a .team.yaml is no team's file");
    }

    [Fact]
    public void A_missing_folder_is_said_once_by_the_agents_source_not_again_by_the_teams()
    {
        var (result, logs) = LoadTeams(new MentorDeclarativeOptions { Directory = Path.Combine(_root, "absent") });

        result.Teams.Should().BeEmpty();
        logs.Records.Should().NotContain(r => r.Level >= LogLevel.Warning);
    }

    // ── Through the host: as any source's team ─────────────────────────────────

    [Fact]
    [Trait("Rule", "team.from-yaml")]
    [Trait("Rule", "team.rejected-with-reason")]
    public async Task A_yaml_team_joins_the_coordinator_and_a_broken_one_is_reported_with_its_place()
    {
        var observer = new TeamObserver();
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false, s =>
        {
            s.AddSingleton<IMentorAgentSourceObserver>(observer);
            s.AddMentorAgentDeclarative(d =>
            {
                d.TeamDefinitions.Add(OrderReview);
                d.TeamDefinitions.Add("name: Broken\nmembers:\n  - role: [unclosed\n");
                d.TeamDefinitions.Add("name: Hollow\ndescription: d\n");
            });
        });
        host.Chat!.SayRepeatedly("ok", 4);

        await host.RunTurnAsync("ciao");

        host.FirstToolsSent.Should().Contain("order_review", "a team of a file is offered as one written in C#");
        var report = observer.Teams.Should().ContainSingle(r => r.Source.Contains(nameof(DeclarativeTeamSource))).Subject;
        report.Loaded.Should().Equal("Order Review");
        report.Rejected.Should().HaveCount(2);
        report.Rejected.Should().Contain(r => r.Name == "inline[1]" && r.ReasonCode == MentorAgentRejectionReasons.InvalidDefinition
                                              && r.Detail!.StartsWith("line ", StringComparison.Ordinal));
        report.Rejected.Should().Contain(r => r.Name == "Hollow" && r.ReasonCode == MentorAgentRejectionReasons.NoMembers,
            "what a file means is checked by the core, as for any source");
    }

    // ── policy.offered-to-agents: a member of a file is offered its tools by the policies ──

    private const string ClerkReview = """
        name: Order Review
        description: Reviews an order before anything is done with it.
        maxIterations: 4
        approver: Approver
        members:
          - role: Clerk
            instructions: Look at the order.
            tools: [count_open_orders, show_order_card]
          - role: Approver
            instructions: Decide. Reply with the verdict.
        """;

    private static bool Is(string? instructions, string role)
        => (instructions ?? "").Contains($"Your role in the team is: **{role}**", StringComparison.Ordinal);

    /// <summary>The coordinator calls the team; the Clerk calls <paramref name="clerkCalls"/> once when given, then speaks.</summary>
    private static Func<IReadOnlyList<ChatMessage>, ChatOptions?, ChatResponse> AskTheTeam(MentorHostFixture host,
        string? clerkCalls = null)
        => (messages, options) =>
        {
            var tools = options?.Tools?.Select(t => t.Name).ToList() ?? [];
            var answered = messages.LastOrDefault()?.Contents.OfType<FunctionResultContent>().Any() == true;
            ChatResponse Say(string text) => new(new ChatMessage(ChatRole.Assistant, text)) { ModelId = host.Chat!.ModelId };
            ChatResponse Call(string name, Dictionary<string, object?> args)
                => new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(Guid.NewGuid().ToString("N"), name, args)]))
                {
                    ModelId = host.Chat!.ModelId,
                };

            if (Is(options?.Instructions, "Clerk"))
                return clerkCalls is null || answered ? Say("The order looks right.") : Call(clerkCalls, []);
            if (Is(options?.Instructions, "Approver")) return Say("Fine. [TEAM:APPROVED]");
            if (tools.Contains("order_review"))
                return answered ? Say("relayed") : Call("order_review", new() { ["task"] = "review order 7" });
            return Say("SAFE");
        };

    private static MentorHostFixture YamlTeamHost(TestActionPolicy policy, RecordingActionObserver observer)
        => MentorHostFixture.Create(o => o.MemoryAutoCapture = false, s =>
        {
            s.AddMentorAgentDeclarative(d => d.TeamDefinitions.Add(ClerkReview));
            s.AddMentorActionPolicy(policy);
            s.AddMentorActionObserver(observer);
        });

    [Fact]
    [Trait("Rule", "policy.offered-to-agents")]
    public async Task A_policy_decides_what_a_member_of_a_yaml_team_is_offered()
    {
        // The README's example: no to the offer of count_open_orders, yes to every call.
        var policy = new TestActionPolicy
        {
            Allowed = r => r.Stage != MentorActionStage.Offer || r.ToolName != "count_open_orders",
        };
        await using var host = YamlTeamHost(policy, new RecordingActionObserver());
        host.Chat!.Respond(AskTheTeam(host));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        (await host.RunTurnAsync("review order 7", deadline.Token)).Should().BeNull();

        var clerk = host.Chat.Calls.Where(c => Is(c.Instructions, "Clerk")).ToList();
        clerk.Should().NotBeEmpty("the team ran. Warnings: {0}", host.Logs.WarningText);
        clerk.Should().OnlyContain(c => !c.ToolNames.Contains("count_open_orders") && c.ToolNames.Contains("show_order_card"));
        // Asked once for the coordinator, which has the action too, and once for the Clerk, by its role.
        var asked = policy.Seen.Where(s => s.Request.ToolName == "count_open_orders" && s.Request.Stage == MentorActionStage.Offer
                                           && s.Request.AgentKind == MentorAgentKind.TeamMember)
                               .Select(s => s.Request).ToList();
        asked.Should().ContainSingle();
        asked[0].AgentName.Should().Be("Clerk");
    }

    [Fact]
    [Trait("Rule", "policy.offered-to-agents")]
    public async Task A_call_of_a_yaml_team_member_to_an_action_the_policy_did_not_offer_runs_nothing()
    {
        var observer = new RecordingActionObserver();
        var policy = new TestActionPolicy
        {
            Allowed = r => r.Stage != MentorActionStage.Offer || r.ToolName != "count_open_orders",
        };
        await using var host = YamlTeamHost(policy, observer);
        host.Chat!.Respond(AskTheTeam(host, clerkCalls: "count_open_orders"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        (await host.RunTurnAsync("review order 7", deadline.Token)).Should().BeNull();
        await host.Get<MentorActionDecisions>().FlushAsync();

        host.Get<TestOrderService>().Invoked.Should().NotContain("CountOpenOrders");
        observer.Steps("count_open_orders").Should().Equal("Denied/not-offered");
        policy.Asked("count_open_orders").Should().BeEmpty("the call is refused without asking the policy about it");
    }
}
