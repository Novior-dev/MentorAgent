using FluentAssertions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Tests.App;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Novior.MentorAgent.Tests.Levels;

/// <summary>
/// Story 1570, slice A — the teams of an <see cref="IMentorTeamSource"/>: a team given by a source joins the coordinator
/// as a team written in C# does, its members call the session's actions through the gate once, and what does not hold
/// is left out with its reason, for the observers too. Every rule of the <c>team</c> area is named on its test.
/// </summary>
public sealed class TeamSourceTests
{
    private const string Review = "order_review";

    /// <summary>A source that gives the teams it holds, and counts how often it is asked.</summary>
    private sealed class Source(params MentorTeamDefinition[] teams) : IMentorTeamSource
    {
        public int Asked;

        public Task<MentorTeamSourceResult> LoadAsync(MentorTeamSourceContext context, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Asked);
            return Task.FromResult(new MentorTeamSourceResult(teams, []));
        }
    }

    private sealed class FailingSource : IMentorTeamSource
    {
        public Task<MentorTeamSourceResult> LoadAsync(MentorTeamSourceContext context, CancellationToken ct = default)
            => throw new InvalidOperationException("the store is down: secret connection string");
    }

    /// <summary>Keeps the team reports only; the agent reports go to the interface's own method.</summary>
    private sealed class TeamObserver : IMentorAgentSourceObserver
    {
        public List<MentorAgentSourceReport> Teams { get; } = [];
        public int Agents;

        public void OnAgentsLoaded(MentorAgentSourceReport report) => Interlocked.Increment(ref Agents);

        public void OnTeamsLoaded(MentorAgentSourceReport report)
        {
            lock (Teams) Teams.Add(report);
        }
    }

    private static MentorTeamDefinition OrderReview(string[] clerkTools, string? approver = "Approver", string name = "Order Review",
        int maxIterations = 4)
        => new()
        {
            Name = name,
            Description = "Reviews an order before anything is done with it.",
            MaxIterations = maxIterations,
            Approver = approver,
            Members =
            [
                new MentorTeamMemberDefinition { Role = "Clerk", Instructions = "Look at the order.", Tools = clerkTools },
                new MentorTeamMemberDefinition { Role = "Approver", Instructions = "Decide. Reply with the verdict." },
            ],
        };

    private static MentorHostFixture Host(IMentorTeamSource source, Action<IServiceCollection>? services = null,
        Action<Novior.MentorAgent.Options.MentorOptions>? configure = null)
        => MentorHostFixture.Create(o => { o.MemoryAutoCapture = false; configure?.Invoke(o); }, s =>
        {
            s.AddSingleton<IMentorTeamSource>(source);
            services?.Invoke(s);
        });

    private static ChatResponse Text(MentorHostFixture host, string text)
        => new(new ChatMessage(ChatRole.Assistant, text)) { ModelId = host.Chat!.ModelId };

    private static ChatResponse Invoke(MentorHostFixture host, string name, Dictionary<string, object?> args)
        => new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(Guid.NewGuid().ToString("N"), name, args)]))
        {
            ModelId = host.Chat!.ModelId,
        };

    private static bool Is(string? instructions, string role)
        => (instructions ?? "").Contains($"Your role in the team is: **{role}**", StringComparison.Ordinal);

    /// <summary>
    /// The coordinator calls the team; the Clerk calls <paramref name="tool"/> once, then says what it found; the
    /// Approver says <paramref name="verdict"/>.
    /// </summary>
    private static Func<IReadOnlyList<ChatMessage>, ChatOptions?, ChatResponse> AskTheTeam(MentorHostFixture host,
        string? tool = null, Dictionary<string, object?>? args = null, string verdict = "Fine. [TEAM:APPROVED]",
        string clerkSays = "The order looks right.")
        => (messages, options) =>
        {
            var tools = options?.Tools?.Select(t => t.Name).ToList() ?? [];
            var answered = messages.LastOrDefault()?.Contents.OfType<FunctionResultContent>().Any() == true;

            if (Is(options?.Instructions, "Clerk"))
                return tool is null || answered ? Text(host, clerkSays) : Invoke(host, tool, args ?? []);
            if (Is(options?.Instructions, "Approver"))
                return Text(host, verdict);
            if (tools.Contains(Review))
                return answered ? Text(host, "relayed") : Invoke(host, Review, new() { ["task"] = "review order 7" });
            return Text(host, "SAFE");
        };

    private static async Task TurnAsync(MentorHostFixture host, string message = "review order 7")
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        (await host.RunTurnAsync(message, deadline.Token)).Should().BeNull("the turn ran. Warnings: {0}", host.Logs.WarningText);
    }

    // ── team.from-source ─────────────────────────────────────────────────────────

    [Fact]
    [Trait("Rule", "team.from-source")]
    public async Task A_sources_team_is_offered_to_the_coordinator_as_a_team_written_in_CSharp_is()
    {
        var source = new Source(OrderReview(["count_open_orders"]));
        await using var host = Host(source);
        host.Chat!.Respond(AskTheTeam(host));

        await TurnAsync(host);

        host.FirstToolsSent.Should().Contain([Review, "business_analysis_team"], "the source's team next to the code's");
        host.Chat.Calls.Should().Contain(c => Is(c.Instructions, "Clerk"), "the team's members spoke");
        host.Chat.Calls.Should().Contain(c => Is(c.Instructions, "Approver"));
        source.Asked.Should().Be(1, "asked as the coordinator is built");
    }

    [Fact]
    [Trait("Rule", "team.from-source")]
    public async Task Every_source_is_reported_to_the_observers_with_the_teams_it_gave()
    {
        var observer = new TeamObserver();
        var source = new Source(OrderReview(["count_open_orders"]));
        await using var host = Host(source, s => s.AddSingleton<IMentorAgentSourceObserver>(observer));
        host.Chat!.Respond(AskTheTeam(host));

        await TurnAsync(host);

        var report = observer.Teams.Should().ContainSingle().Which;
        report.Source.Should().Be(typeof(Source).FullName);
        report.Loaded.Should().Equal("Order Review");
        report.Rejected.Should().BeEmpty();
        observer.Agents.Should().Be(0, "no agent source: OnAgentsLoaded is not called for a team source");
    }

    // ── team.approver ────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Rule", "team.approver")]
    public async Task With_an_approver_only_its_verdict_ends_the_discussion()
    {
        await using var host = Host(new Source(OrderReview([], maxIterations: 6)));
        host.Chat!.Respond(AskTheTeam(host, clerkSays: "Looks fine to me. [TEAM:APPROVED]", verdict: "No: wrong address. [TEAM:REJECTED]"));

        await TurnAsync(host);

        host.Chat.Calls.Count(c => Is(c.Instructions, "Clerk")).Should().Be(1,
            "the Clerk's verdict does not end it; the Approver's does, at its first turn");
        host.Chat.Calls.Count(c => Is(c.Instructions, "Approver")).Should().Be(1);
    }

    // ── team.member-tools ───────────────────────────────────────────────────────

    [Fact]
    [Trait("Rule", "team.member-tools")]
    public async Task A_members_write_with_a_confirmation_asks_once_and_runs()
    {
        var observer = new RecordingActionObserver();
        await using var host = Host(new Source(OrderReview(["issue_refund"])), s => s.AddMentorActionObserver(observer));
        host.Chat!.Respond(AskTheTeam(host, "issue_refund", new() { ["orderId"] = 7 }));
        var state = host.Get<IMentorStateService>();
        var asked = 0;
        state.OnConfirmationRequired += request =>
        {
            Interlocked.Increment(ref asked);
            _ = state.ConfirmAsync(request.ActionId);
        };

        await TurnAsync(host);
        await host.Get<MentorActionDecisions>().FlushAsync();

        asked.Should().Be(1, "one gate around the member's tool, not two");
        host.Get<TestOrderService>().Invoked.Should().Equal(["IssueRefundAsync"]);
        observer.Seen.Where(d => d.ToolName == "issue_refund").Should().OnlyContain(d => d.AgentName == "Clerk");
    }

    [Fact]
    [Trait("Rule", "team.member-tools")]
    public async Task A_members_action_that_needs_a_role_the_user_lacks_is_refused_by_the_gate()
    {
        var observer = new RecordingActionObserver();
        await using var host = Host(new Source(OrderReview(["export_ledger", "count_open_orders"])), s => s.AddMentorActionObserver(observer));
        host.Chat!.Respond(AskTheTeam(host, "export_ledger"));
        host.Auth.SignIn("bob", "Manager");

        await TurnAsync(host);
        await host.Get<MentorActionDecisions>().FlushAsync();

        host.Get<TestOrderService>().Invoked.Should().NotContain("ExportLedger");
        observer.Steps("export_ledger").Should().Equal("Denied/role-missing");
        host.Chat.Calls.Where(c => Is(c.Instructions, "Clerk")).Should().OnlyContain(
            c => !c.ToolNames.Contains("export_ledger") && c.ToolNames.Contains("count_open_orders"),
            "the member is offered only what the user may run");
    }

    [Fact]
    [Trait("Rule", "team.member-tools")]
    public async Task A_members_read_runs_without_a_question()
    {
        var observer = new RecordingActionObserver();
        await using var host = Host(new Source(OrderReview(["count_open_orders"])), s => s.AddMentorActionObserver(observer));
        host.Chat!.Respond(AskTheTeam(host, "count_open_orders"));
        var asked = 0;
        host.Get<IMentorStateService>().OnConfirmationRequired += _ => Interlocked.Increment(ref asked);

        await TurnAsync(host);
        await host.Get<MentorActionDecisions>().FlushAsync();

        asked.Should().Be(0);
        host.Get<TestOrderService>().Invoked.Should().Equal(["CountOpenOrders"]);
        observer.Steps("count_open_orders").Should().Equal("Allowed/allowed", "Executed/completed");
    }

    [Fact]
    [Trait("Rule", "team.member-tools")]
    public async Task A_name_the_session_has_no_action_for_is_taken_away_and_the_team_still_loads()
    {
        var observer = new TeamObserver();
        await using var host = Host(new Source(OrderReview(["count_open_orders", "drop_database", "archive_order", "remember"])),
            s => s.AddSingleton<IMentorAgentSourceObserver>(observer));
        host.Chat!.Respond(AskTheTeam(host));

        await TurnAsync(host);

        var report = observer.Teams.Should().ContainSingle().Which;
        report.Loaded.Should().Equal("Order Review");
        report.Rejected.Select(r => (r.Name, r.ReasonCode, r.Tool)).Should().BeEquivalentTo(
        [
            ("Order Review", MentorAgentRejectionReasons.ToolNotFound, "drop_database"),
            ("Order Review", MentorAgentRejectionReasons.ToolNotFound, "archive_order"),
            ("Order Review", MentorAgentRejectionReasons.ToolNotFound, "remember"),
        ], "unknown, off by default, and one of MentorAgent's own tools");
        host.Chat.Calls.Where(c => Is(c.Instructions, "Clerk")).Should().OnlyContain(c => c.ToolNames.SequenceEqual(new[] { "count_open_orders" }));
    }

    [Fact]
    [Trait("Rule", "team.member-tools")]
    public async Task An_action_the_governor_turns_off_for_the_session_is_taken_away_from_the_member()
    {
        var observer = new TeamObserver();
        var governor = new TestActionGovernor { Enabled = r => r.ToolName == "issue_refund" ? false : null };
        await using var host = Host(new Source(OrderReview(["issue_refund", "count_open_orders"])), s =>
        {
            s.AddSingleton<IMentorActionGovernor>(governor);
            s.AddSingleton<IMentorAgentSourceObserver>(observer);
        });
        host.Chat!.Respond(AskTheTeam(host));

        await TurnAsync(host);

        observer.Teams.Should().ContainSingle().Which.Rejected.Should().ContainSingle()
            .Which.Should().Match<MentorAgentRejection>(r => r.Tool == "issue_refund" && r.ReasonCode == MentorAgentRejectionReasons.ToolNotFound);
        host.Chat.Calls.Where(c => Is(c.Instructions, "Clerk")).Should().OnlyContain(c => !c.ToolNames.Contains("issue_refund"));
    }

    [Fact]
    [Trait("Rule", "team.member-tools")]
    public async Task A_candidate_no_governor_turned_on_is_taken_away_from_the_member()
    {
        var observer = new TeamObserver();
        await using var host = Host(new Source(OrderReview(["issue", "count_open_orders"])), s =>
        {
            s.AddScoped<CandidateShop.Services.InvoiceService>();
            s.AddSingleton<IMentorAgentSourceObserver>(observer);
        }, o =>
        {
            o.AutoMentor.Enabled = true;
            o.AutoMentor.Scan = [typeof(TeamSourceTests).Assembly];
            o.AutoMentor.Namespaces = ["CandidateShop.Services"];
        });
        host.Chat!.Respond(AskTheTeam(host));

        await TurnAsync(host);

        host.Get<IMentorActionCatalog>().GetActions().Should().Contain(a => a.ToolName == "issue" && !a.EnabledByDefault,
            "the candidate exists, off");
        observer.Teams.Should().ContainSingle().Which.Rejected.Should().ContainSingle()
            .Which.Should().Match<MentorAgentRejection>(r => r.Tool == "issue" && r.ReasonCode == MentorAgentRejectionReasons.ToolNotFound);
    }

    [Fact]
    [Trait("Rule", "team.member-tools")]
    public async Task An_action_a_policy_refuses_is_refused_at_the_members_call_as_for_a_member_written_in_CSharp()
    {
        // A policy is asked at every turn, not once per session. BUG-233 — it decides the member's offer, as for a member
        // written in C#: the tool is left out, and the call the member's model makes all the same is refused as not
        // offered, without asking the policy about it (README: "Where policies are asked").
        var observer = new RecordingActionObserver();
        var policy = new TestActionPolicy { Allowed = r => r.ToolName != "count_open_orders" };
        await using var host = Host(new Source(OrderReview(["count_open_orders"])), s =>
        {
            s.AddMentorActionPolicy(policy);
            s.AddMentorActionObserver(observer);
        });
        host.Chat!.Respond(AskTheTeam(host, "count_open_orders"));

        await TurnAsync(host);
        await host.Get<MentorActionDecisions>().FlushAsync();

        host.Get<TestOrderService>().Invoked.Should().NotContain("CountOpenOrders");
        policy.Seen.Should().Contain(s => s.Request.ToolName == "count_open_orders"
            && s.Request.Stage == MentorActionStage.Offer && s.Request.AgentKind == MentorAgentKind.TeamMember);
        policy.Asked("count_open_orders").Should().BeEmpty("the call is refused without asking the policy about it");
        observer.Steps("count_open_orders").Should().Equal("Denied/not-offered");
    }

    // ── team.rejected-with-reason ─────────────────────────────────────────────────

    [Fact]
    [Trait("Rule", "team.rejected-with-reason")]
    public async Task A_definition_that_does_not_hold_is_left_out_with_its_reason_and_the_others_load()
    {
        var observer = new TeamObserver();
        MentorTeamDefinition[] teams =
        [
            OrderReview([], name: "No Members") with { Members = [] },
            OrderReview([], name: "No Approver", approver: "Boss"),
            OrderReview([], name: "Twice") with
            {
                Members = [new() { Role = "Clerk", Instructions = "a" }, new() { Role = "Clerk", Instructions = "b" }],
                Approver = null,
            },
            OrderReview([], name: "No Turns", maxIterations: 0),
            OrderReview([], name: "Business Analysis Team"),
            OrderReview([], name: "Cancel Order"),
            OrderReview([]),
            OrderReview([]),
        ];
        await using var host = Host(new Source(teams), s => s.AddSingleton<IMentorAgentSourceObserver>(observer));
        host.Chat!.Respond(AskTheTeam(host));

        await TurnAsync(host);

        var report = observer.Teams.Should().ContainSingle().Which;
        report.Loaded.Should().Equal("Order Review");
        report.Rejected.Select(r => (r.Name, r.ReasonCode)).Should().Equal(
            ("No Members", MentorAgentRejectionReasons.NoMembers),
            ("No Approver", MentorAgentRejectionReasons.InvalidDefinition),
            ("Twice", MentorAgentRejectionReasons.InvalidDefinition),
            ("No Turns", MentorAgentRejectionReasons.InvalidDefinition),
            ("Business Analysis Team", MentorAgentRejectionReasons.NameTaken),
            ("Cancel Order", MentorAgentRejectionReasons.NameTaken),
            ("Order Review", MentorAgentRejectionReasons.NameTaken));
        report.Rejected.Should().OnlyContain(r => !string.IsNullOrWhiteSpace(r.Detail));
        host.Logs.HasWarning("Team source").Should().BeTrue("the log names what was left out");
        host.FirstToolsSent.Count(t => t == Review).Should().Be(1);
    }

    [Fact]
    [Trait("Rule", "team.rejected-with-reason")]
    public async Task A_source_that_throws_is_reported_by_its_type_and_the_others_still_load()
    {
        var observer = new TeamObserver();
        await using var host = Host(new Source(OrderReview(["count_open_orders"])), s =>
        {
            s.AddSingleton<IMentorTeamSource, FailingSource>();
            s.AddSingleton<IMentorAgentSourceObserver>(observer);
        });
        host.Chat!.Respond(AskTheTeam(host));

        await TurnAsync(host);

        host.FirstToolsSent.Should().Contain(Review);
        var failed = observer.Teams.Should().ContainSingle(r => r.Source == typeof(FailingSource).FullName).Which;
        failed.Rejected.Should().ContainSingle().Which.Should().Be(
            new MentorAgentRejection(typeof(FailingSource).FullName!, MentorAgentRejectionReasons.SourceFailed, nameof(InvalidOperationException)));
        host.Logs.ScopedRecords.Should().NotContain(r => r.Message.Contains("secret connection string"), "the type only, never the message");
    }
}
