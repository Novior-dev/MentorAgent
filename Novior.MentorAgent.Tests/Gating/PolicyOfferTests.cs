using FluentAssertions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.App;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Novior.MentorAgent.Tests.Gating;

/// <summary>
/// BUG-233 — an action the application's <see cref="IMentorActionPolicy"/> does not offer a user could still be run by
/// that user. The policies were asked at <see cref="MentorActionStage.Offer"/> only about the coordinator's actions:
/// a specialist, a team member and an agent of an <see cref="IMentorAgentSource"/> were offered it, and called it. And
/// a call to an action left out — the coordinator's on Path A, whose function-invoking client still holds it, or an
/// agent's — was decided by the policies at the call alone. A policy that decides whether the user may use an action
/// when it is offered, and looks at the record alone at the call, as the README's example did, let it run.
///
/// <para>
/// Now the policies decide the offer of every agent's tools, asked once a turn at the agent's first request, with its
/// kind and name; and a call to an action they did not offer that agent in the turn is refused at the gate as
/// <see cref="MentorActionReasons.NotOffered"/>, without asking them about the call. Every policy here allows every
/// call: it is the README's example, which said no only to the offer.
/// </para>
/// </summary>
public sealed class PolicyOfferTests
{
    private const string Notice = "Some other actions of this application are NOT available to this user on this turn";
    private const string SignIn = "add that signing in may make it available";

    /// <summary>A policy that does not offer <paramref name="leftOut"/> and allows every call, as the README's example.</summary>
    private static TestActionPolicy NotOffering(Func<MentorActionRequest, bool> leftOut)
        => new() { Allowed = r => r.Stage != MentorActionStage.Offer || !leftOut(r) };

    private static MentorHostFixture Create(RecordingActionObserver observer, IMentorActionPolicy policy,
        Action<MentorOptions>? configure = null, Action<IServiceCollection>? services = null)
        => MentorHostFixture.Create(o =>
        {
            o.MemoryAutoCapture = false;
            configure?.Invoke(o);
        }, s =>
        {
            s.AddMentorActionObserver(observer);
            s.AddMentorActionPolicy(policy);
            services?.Invoke(s);
        });

    private static string LastSection(FakeChatClient.RecordedCall call)
    {
        var instructions = call.Instructions ?? "";
        return instructions[Math.Max(0, instructions.LastIndexOf("## ", StringComparison.Ordinal))..];
    }

    private static bool Answered(IReadOnlyList<ChatMessage> messages)
        => messages.LastOrDefault()?.Contents.OfType<FunctionResultContent>().Any() == true;

    private static ChatResponse Text(MentorHostFixture host, string text)
        => new(new ChatMessage(ChatRole.Assistant, text)) { ModelId = host.Chat!.ModelId };

    private static ChatResponse Invoke(MentorHostFixture host, string name, Dictionary<string, object?> args)
        => new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(Guid.NewGuid().ToString("N"), name, args)]))
        {
            ModelId = host.Chat!.ModelId,
        };

    private static void ConfirmEveryBanner(MentorHostFixture host)
    {
        var state = host.Get<IMentorStateService>();
        state.OnConfirmationRequired += req => _ = state.ConfirmAsync(req.ActionId);
    }

    private static List<MentorActionRequest> Offers(TestActionPolicy policy, string tool)
        => [.. policy.Seen.Where(s => s.Request.ToolName == tool && s.Request.Stage == MentorActionStage.Offer)
                          .Select(s => s.Request)];

    // ── Specialists ──────────────────────────────────────────────────────────────

    /// <summary>The specialist's requests: the only ones that carry its own read.</summary>
    private static List<FakeChatClient.RecordedCall> SpecialistCalls(MentorHostFixture host, int from = 0)
        => [.. host.Chat!.Calls.Skip(from).Where(c => c.ToolNames.Contains("get_tracking_status"))];

    private static Func<IReadOnlyList<ChatMessage>, ChatOptions?, ChatResponse> TrackOrder(MentorHostFixture host)
        => ActionDecisionTests.Delegating(host, "where is order 7?", "shipping", "get_tracking_status",
            new() { ["orderId"] = 7 });

    [Theory]
    [InlineData(MentorHitlMode.Blocking, false)]
    [InlineData(MentorHitlMode.Native, false)]
    [InlineData(MentorHitlMode.Blocking, true)]
    public async Task A_policy_decides_what_a_specialist_is_offered(MentorHitlMode mode, bool offered)
    {
        var observer = new RecordingActionObserver();
        var policy = NotOffering(r => !offered && r.ToolName == "cancel_shipment");
        await using var host = Create(observer, policy, o => o.HitlMode = mode);
        host.Auth.SignIn("ada", "Manager");   // the role allows it: only the policy decides
        host.Chat!.Respond(TrackOrder(host));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        (await host.RunTurnAsync("where is order 7?", deadline.Token)).Should().BeNull();

        var calls = SpecialistCalls(host);
        calls.Should().NotBeEmpty("the specialist ran. Warnings: {0}", host.Logs.WarningText);
        if (offered)
        {
            calls.Should().OnlyContain(c => c.ToolNames.Contains("cancel_shipment"), "the policy offers it");
            calls.Should().NotContain(c => (c.Instructions ?? "").Contains(Notice));
        }
        else
        {
            calls.Should().OnlyContain(c => !c.ToolNames.Contains("cancel_shipment"), "mode {0}: the policy does not offer it", mode);
            calls.Should().OnlyContain(c => LastSection(c).Contains(Notice) && !LastSection(c).Contains(SignIn),
                "the specialist is told, as the coordinator is; ada is signed in");
        }

        var asked = Offers(policy, "cancel_shipment");
        asked.Should().ContainSingle("asked once in the turn, though the specialist made {0} requests", calls.Count);
        asked[0].AgentKind.Should().Be(MentorAgentKind.Specialist);
        asked[0].AgentName.Should().Be("ShippingAgent");
        asked[0].User!.Identity!.Name.Should().Be("ada");
        asked[0].TurnId.Should().Be(host.Get<MentorTurnContext>().TurnId);
        asked[0].Arguments.Should().BeNull();
        asked[0].RequiredRoles.Should().Equal("Manager");
        asked[0].RequiresConfirmation.Should().BeTrue();
    }

    [Fact]
    public async Task A_call_to_a_specialists_action_the_policy_did_not_offer_runs_nothing()
    {
        // The README's example: the policy does not offer cancel_shipment to ada, and allows every call. The specialist's
        // model names it anyway; its function-invoking client still holds the tool.
        var observer = new RecordingActionObserver();
        var policy = NotOffering(r => r.ToolName == "cancel_shipment");
        await using var host = Create(observer, policy);
        host.Auth.SignIn("ada", "Manager");
        ConfirmEveryBanner(host);
        var delegating = TrackOrder(host);
        host.Chat!.Respond((messages, options) =>
            options?.Tools?.Any(t => t.Name == "get_tracking_status") == true && !Answered(messages)
                ? Invoke(host, "cancel_shipment", new() { ["orderId"] = 7 })
                : delegating(messages, options));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        (await host.RunTurnAsync("cancel the shipment of order 7", deadline.Token)).Should().BeNull();
        await host.Get<MentorActionDecisions>().FlushAsync();

        host.Get<TestShippingAgent>().Invoked.Should().BeEmpty("a policy did not offer it");
        observer.Steps("cancel_shipment").Should().Equal("Denied/not-offered");
        policy.Asked("cancel_shipment").Should().BeEmpty("the call is refused without asking the policies about it");
        host.Logs.WarningText.Should().Contain("which an action policy did not offer on this turn");
    }

    [Fact]
    public async Task The_policies_are_asked_once_a_turn_and_only_about_the_agents_that_work()
    {
        var observer = new RecordingActionObserver();
        var policy = new TestActionPolicy();
        await using var host = Create(observer, policy);
        host.Auth.SignIn("ada", "Manager");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        host.Chat!.Respond((_, _) => Text(host, "Hello."));
        (await host.RunTurnAsync("hello", deadline.Token)).Should().BeNull();
        policy.Seen.Where(s => s.Request.Stage == MentorActionStage.Offer)
            .Should().OnlyContain(s => s.Request.AgentKind == MentorAgentKind.Coordinator, "no agent worked");

        host.Chat.Respond(TrackOrder(host));
        (await host.RunTurnAsync("where is order 7?", deadline.Token)).Should().BeNull();
        (await host.RunTurnAsync("where is order 8?", deadline.Token)).Should().BeNull();

        var asked = Offers(policy, "get_tracking_status");
        asked.Should().HaveCount(2).And.OnlyContain(r => r.AgentKind == MentorAgentKind.Specialist);
        asked.Select(r => r.TurnId).Distinct().Should().HaveCount(2, "asked again at the next turn");
    }

    [Fact]
    public async Task A_policy_that_fails_as_a_specialists_tools_are_offered_leaves_the_action_out()
    {
        var observer = new RecordingActionObserver();
        var policy = new TestActionPolicy
        {
            Before = (r, _) => r is { Stage: MentorActionStage.Offer, AgentKind: MentorAgentKind.Specialist, ToolName: "cancel_shipment" }
                ? throw new InvalidOperationException("the permissions service is down")
                : Task.CompletedTask,
        };
        await using var host = Create(observer, policy);
        host.Auth.SignIn("ada", "Manager");
        host.Chat!.Respond(TrackOrder(host));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        (await host.RunTurnAsync("where is order 7?", deadline.Token)).Should().BeNull();

        var calls = SpecialistCalls(host);
        calls.Should().NotBeEmpty("the specialist ran. Warnings: {0}", host.Logs.WarningText);
        calls.Should().OnlyContain(c => !c.ToolNames.Contains("cancel_shipment"), "leaving it out is the safe answer");
        host.Logs.WarningText.Should().Contain("threw (InvalidOperationException)")
            .And.NotContain("the permissions service is down", "the log never has the policy's message");
    }

    // ── Team members ─────────────────────────────────────────────────────────────

    private static bool Is(FakeChatClient.RecordedCall call, string role)
        => (call.Instructions ?? "").Contains($"Your role in the team is: **{role}**", StringComparison.Ordinal);

    private static bool Is(ChatOptions? options, string role)
        => (options?.Instructions ?? "").Contains($"Your role in the team is: **{role}**", StringComparison.Ordinal);

    /// <summary>
    /// The coordinator asks the team; the Data Analyst calls <paramref name="analystCalls"/> first when it is given, then
    /// speaks; the Strategist approves.
    /// </summary>
    private static Func<IReadOnlyList<ChatMessage>, ChatOptions?, ChatResponse> AskTheTeam(MentorHostFixture host,
        string? analystCalls = null)
        => (messages, options) =>
        {
            var tools = options?.Tools?.Select(t => t.Name).ToList() ?? [];
            if (Is(options, "Data Analyst"))
                return analystCalls is not null && !Answered(messages)
                    ? Invoke(host, analystCalls, [])
                    : Text(host, "Revenue is steady.");
            if (Is(options, "Strategist")) return Text(host, "Keep the budget. [TEAM:APPROVED]");
            if (tools.Contains("business_analysis_team"))
                return Answered(messages)
                    ? Text(host, "relayed")
                    : Invoke(host, "business_analysis_team", new() { ["task"] = "review the quarter" });
            return Text(host, "SAFE");
        };

    [Theory]
    [InlineData(MentorHitlMode.Blocking)]
    [InlineData(MentorHitlMode.Native)]
    public async Task A_policy_decides_what_a_team_member_is_offered_and_told(MentorHitlMode mode)
    {
        var observer = new RecordingActionObserver();
        var policy = NotOffering(r => r.ToolName == "read_quarter_revenue");
        await using var host = Create(observer, policy, o => o.HitlMode = mode);
        host.Auth.SignIn("ada", "Manager");
        host.Chat!.Respond(AskTheTeam(host));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        (await host.RunTurnAsync("review the quarter", deadline.Token)).Should().BeNull();

        var analyst = host.Chat.Calls.Where(c => Is(c, "Data Analyst")).ToList();
        analyst.Should().NotBeEmpty("the team ran. Warnings: {0}", host.Logs.WarningText);
        analyst.Should().OnlyContain(c => !c.ToolNames.Contains("read_quarter_revenue")
                                          && c.ToolNames.Contains("approve_budget_line"), "mode {0}", mode);
        analyst.Should().OnlyContain(c => !(c.Instructions ?? "").Contains("**read_quarter_revenue**")
                                          && !(c.Instructions ?? "").Contains("Reads the quarter's revenue"),
            "the member's instructions listed the tool with its description");
        analyst.Should().OnlyContain(c => (c.Instructions ?? "").Contains("**approve_budget_line**"));
        analyst.Should().OnlyContain(c => LastSection(c).Contains(Notice) && !LastSection(c).Contains(SignIn));

        var asked = Offers(policy, "read_quarter_revenue");
        asked.Should().ContainSingle();
        asked[0].AgentKind.Should().Be(MentorAgentKind.TeamMember);
        asked[0].AgentName.Should().Be("Data Analyst");
    }

    [Fact]
    public async Task A_call_to_a_team_members_action_the_policy_did_not_offer_runs_nothing()
    {
        var observer = new RecordingActionObserver();
        var policy = NotOffering(r => r.ToolName == "read_quarter_revenue");
        await using var host = Create(observer, policy);
        host.Auth.SignIn("ada", "Manager");
        host.Chat!.Respond(AskTheTeam(host, analystCalls: "read_quarter_revenue"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        (await host.RunTurnAsync("review the quarter", deadline.Token)).Should().BeNull();
        await host.Get<MentorActionDecisions>().FlushAsync();

        observer.Steps("read_quarter_revenue").Should().Equal("Denied/not-offered");
        policy.Asked("read_quarter_revenue").Should().BeEmpty();
    }

    [Fact]
    public async Task Signing_in_is_suggested_only_for_a_tool_left_out_for_its_roles()
    {
        // Nobody signed in: the Data Analyst's approve_budget_line is left out for its role, every tool of the
        // Strategist's by the policy alone.
        var observer = new RecordingActionObserver();
        var policy = NotOffering(r => r.AgentName == "Strategist");
        await using var host = Create(observer, policy);
        host.Chat!.Respond(AskTheTeam(host));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        (await host.RunTurnAsync("review the quarter", deadline.Token)).Should().BeNull();

        var strategist = host.Chat.Calls.Where(c => Is(c, "Strategist")).ToList();
        strategist.Should().NotBeEmpty("the team ran. Warnings: {0}", host.Logs.WarningText);
        strategist.Should().OnlyContain(c => c.ToolNames.Count == 0 || c.ToolNames.All(n => !n.Contains("expense")));
        strategist.Should().OnlyContain(c => LastSection(c).Contains(Notice) && !LastSection(c).Contains(SignIn),
            "signing in does not change a policy's answer");
        host.Chat.Calls.Where(c => Is(c, "Data Analyst")).Should().NotBeEmpty()
            .And.OnlyContain(c => LastSection(c).Contains(SignIn), "a role may come with signing in");
    }

    // ── Agents of an IMentorAgentSource ──────────────────────────────────────────

    /// <summary>A source that hands back one agent with the tools it is given, the way Novior.MentorAgent.Declarative does.</summary>
    private sealed class LedgerAgentSource : IMentorAgentSource
    {
        public Task<IReadOnlyList<AIAgent>> GetAgentsAsync(MentorAgentSourceContext context, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AIAgent>>(
            [
                new ChatClientAgent(context.ChatClient, new ChatClientAgentOptions
                {
                    Name = "LedgerAgent",
                    Description = "Keeps the order ledger",
                    ChatOptions = new ChatOptions
                    {
                        Instructions = "You are the ledger specialist.",
                        Tools = [.. context.Tools],
                    },
                }),
            ]);
    }

    [Fact]
    public async Task A_policy_decides_what_an_agent_source_is_offered_and_its_call_runs_nothing()
    {
        // Left out for the source's agent only: the coordinator is still offered count_open_orders.
        var observer = new RecordingActionObserver();
        var policy = NotOffering(r => r is { ToolName: "count_open_orders", AgentKind: MentorAgentKind.AgentSource });
        await using var host = Create(observer, policy, services: s => s.AddSingleton<IMentorAgentSource>(new LedgerAgentSource()));
        var delegating = ActionDecisionTests.Delegating(host, "how many open orders?", "ledger", "count_open_orders", []);
        static bool OfTheAgent(ChatOptions? o) => (o?.Instructions ?? "").Contains("You are the ledger specialist.", StringComparison.Ordinal);
        host.Chat!.Respond((messages, options) =>
            OfTheAgent(options) && !Answered(messages)
                ? Invoke(host, "count_open_orders", [])
                : delegating(messages, options));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        (await host.RunTurnAsync("how many open orders?", deadline.Token)).Should().BeNull();
        await host.Get<MentorActionDecisions>().FlushAsync();

        var agent = host.Chat.Calls.Where(c => (c.Instructions ?? "").Contains("You are the ledger specialist.")).ToList();
        agent.Should().NotBeEmpty("the source's agent ran. Warnings: {0}", host.Logs.WarningText);
        agent.Should().OnlyContain(c => !c.ToolNames.Contains("count_open_orders") && c.ToolNames.Contains("show_order_card"));
        host.Chat.Calls.Should().Contain(c => c.ToolNames.Contains("route_to_specialist") && c.ToolNames.Contains("count_open_orders"),
            "the coordinator is still offered it");
        host.Get<TestOrderService>().Invoked.Should().NotContain(nameof(TestOrderService.CountOpenOrders));
        observer.Steps("count_open_orders").Should().Equal("Denied/not-offered");
        Offers(policy, "count_open_orders").Should().Contain(r => r.AgentKind == MentorAgentKind.AgentSource && r.AgentName == null);
    }

    /// <summary>The name of the tool whose result is the last message, or null.</summary>
    private static string? LastResultOf(IReadOnlyList<ChatMessage> messages)
    {
        if (messages.LastOrDefault()?.Contents.OfType<FunctionResultContent>().LastOrDefault() is not { } result) return null;
        return messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>()
            .LastOrDefault(c => c.CallId == result.CallId)?.Name;
    }

    [Fact]
    public async Task What_a_policy_did_not_offer_one_agent_another_agent_of_the_turn_still_runs()
    {
        // count_open_orders is left out for the source's agent only. In the same turn, after it, the coordinator calls
        // it: offered to the coordinator, it runs.
        var observer = new RecordingActionObserver();
        var policy = NotOffering(r => r is { ToolName: "count_open_orders", AgentKind: MentorAgentKind.AgentSource });
        await using var host = Create(observer, policy, services: s => s.AddSingleton<IMentorAgentSource>(new LedgerAgentSource()));
        var delegating = ActionDecisionTests.Delegating(host, "how many open orders?", "ledger", "count_open_orders", []);
        static bool OfTheAgent(ChatOptions? o) => (o?.Instructions ?? "").Contains("You are the ledger specialist.", StringComparison.Ordinal);
        host.Chat!.Respond((messages, options) =>
        {
            if (OfTheAgent(options))
                return Answered(messages) ? Text(host, "done") : Invoke(host, "count_open_orders", []);
            if (options?.Tools?.Any(t => t.Name == "route_to_specialist") == true && LastResultOf(messages) == "route_to_specialist")
                return Invoke(host, "count_open_orders", []);
            return delegating(messages, options);
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        (await host.RunTurnAsync("how many open orders?", deadline.Token)).Should().BeNull();
        await host.Get<MentorActionDecisions>().FlushAsync();

        observer.Steps("count_open_orders").Should().Equal("Denied/not-offered", "Allowed/allowed", "Executed/completed");
        host.Get<TestOrderService>().Invoked.Should().Equal(nameof(TestOrderService.CountOpenOrders));
    }

    // ── The coordinator, Path A ──────────────────────────────────────────────────

    [Theory]
    [InlineData(MentorHitlMode.Blocking, "show_order_card")]
    [InlineData(MentorHitlMode.Native, "show_order_card")]
    [InlineData(MentorHitlMode.Blocking, "issue_refund")]
    [InlineData(MentorHitlMode.Native, "issue_refund")]
    public async Task A_call_to_a_coordinators_action_the_policy_did_not_offer_runs_nothing(MentorHitlMode mode, string tool)
    {
        // The coordinator's model names an action it was not offered — remembered from an earlier turn, or dictated
        // by the user. Its function-invoking client still holds it; issue_refund asks for a confirmation, which the
        // user would give.
        var observer = new RecordingActionObserver();
        var policy = NotOffering(r => r.ToolName == tool);
        await using var host = Create(observer, policy, o => o.HitlMode = mode);
        host.Auth.SignIn("ada");
        ConfirmEveryBanner(host);
        host.Chat!.Respond((messages, _) => Answered(messages)
            ? Text(host, "Done.")
            : Invoke(host, tool, new() { ["orderId"] = 7 }));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        (await host.RunTurnAsync($"call {tool} for order 7", deadline.Token)).Should().BeNull();
        await host.Get<MentorActionDecisions>().FlushAsync();

        host.Chat.Calls.Should().NotContain(c => c.ToolNames.Contains(tool), "it was not offered");
        host.Get<TestOrderService>().Invoked.Should().BeEmpty("mode {0}: a policy did not offer {1}", mode, tool);
        observer.Steps(tool).Should().Equal("Denied/not-offered");
        policy.Asked(tool).Should().BeEmpty("the call is refused without asking the policies about it");
    }

    [Fact]
    public async Task What_a_policy_did_not_offer_in_one_turn_is_asked_again_in_the_next()
    {
        var observer = new RecordingActionObserver();
        var offer = false;
        var policy = new TestActionPolicy { Allowed = r => r.Stage != MentorActionStage.Offer || r.ToolName != "show_order_card" || offer };
        await using var host = Create(observer, policy);
        host.Auth.SignIn("ada");
        host.Chat!.Respond((messages, _) => Answered(messages)
            ? Text(host, "Done.")
            : Invoke(host, "show_order_card", new() { ["orderId"] = 7 }));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        (await host.RunTurnAsync("show order 7", deadline.Token)).Should().BeNull();
        offer = true;
        (await host.RunTurnAsync("show order 7", deadline.Token)).Should().BeNull();
        await host.Get<MentorActionDecisions>().FlushAsync();

        observer.Steps("show_order_card").Should().Equal("Denied/not-offered", "Allowed/allowed", "Executed/completed");
        host.Get<TestOrderService>().Invoked.Should().Equal(nameof(TestOrderService.ShowOrderCard));
    }
}
