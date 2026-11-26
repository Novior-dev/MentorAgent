using FluentAssertions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Declarative;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Tests.App;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Novior.MentorAgent.Tests.Gating;

/// <summary>
/// BUG-235: the model of a specialist or a team member called an action of the application its agent does not hold, one
/// an <see cref="IMentorActionGovernor"/> turned off when the agent was built, or another agent's. Its function-invoking
/// client answered "not found", nothing ran, and no decision was given: an audit of the actions did not see the call.
/// The coordinator's had one since story 1600. Now every agent's call gets it, Denied with
/// <see cref="MentorActionReasons.NotOffered"/>, with the agent's kind and name, and still nothing runs.
/// </summary>
[Trait("Rule", "agent.not-offered-call")]
public sealed class AgentNotHeldCallTests
{
    private static TestActionGovernor TurningOff(string tool)
        => new() { Enabled = r => r.ToolName == tool ? false : null };

    private static MentorHostFixture Create(RecordingActionObserver observer, IMentorActionGovernor? governor,
        Action<IServiceCollection>? services = null)
        => MentorHostFixture.Create(o => o.MemoryAutoCapture = false, s =>
        {
            s.AddMentorActionObserver(observer);
            if (governor is not null) s.AddSingleton(governor);
            services?.Invoke(s);
        });

    private static bool Answered(IReadOnlyList<ChatMessage> messages)
        => messages.LastOrDefault()?.Contents.OfType<FunctionResultContent>().Any() == true;

    private static ChatResponse Text(MentorHostFixture host, string text)
        => new(new ChatMessage(ChatRole.Assistant, text)) { ModelId = host.Chat!.ModelId };

    private static ChatResponse Invoke(MentorHostFixture host, string name, Dictionary<string, object?> args)
        => new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(Guid.NewGuid().ToString("N"), name, args)]))
        {
            ModelId = host.Chat!.ModelId,
        };

    private static List<MentorActionDecision> Decisions(RecordingActionObserver observer, string tool)
        => [.. observer.Seen.Where(d => d.ToolName == tool)];

    // ── Specialists ──────────────────────────────────────────────────────────────

    /// <summary>The coordinator hands over to the shipping specialist, whose model calls <paramref name="calls"/> once.</summary>
    private static Func<IReadOnlyList<ChatMessage>, ChatOptions?, ChatResponse> ShippingCalls(MentorHostFixture host, string calls)
    {
        var delegating = ActionDecisionTests.Delegating(host, "where is order 7?", "shipping", "get_tracking_status",
            new() { ["orderId"] = 7 });
        return (messages, options) =>
            options?.Tools?.Any(t => t.Name == "get_tracking_status") == true && !Answered(messages)
                ? Invoke(host, calls, new() { ["orderId"] = 7 })
                : delegating(messages, options);
    }

    [Fact]
    public async Task A_specialists_call_to_an_action_the_governor_turned_off_is_decided_not_offered_and_runs_nothing()
    {
        var observer = new RecordingActionObserver();
        await using var host = Create(observer, TurningOff("cancel_shipment"));
        host.Chat!.Respond(ShippingCalls(host, "cancel_shipment"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        (await host.RunTurnAsync("cancel the shipment of order 7", deadline.Token)).Should().BeNull();
        await host.Get<MentorActionDecisions>().FlushAsync();

        host.Chat.Calls.Should().Contain(c => c.ToolNames.Contains("get_tracking_status"), "the specialist worked");
        host.Chat.Calls.Should().NotContain(c => c.ToolNames.Contains("cancel_shipment"), "the governor turned it off");
        host.Get<TestShippingAgent>().Invoked.Should().NotContain(i => i.Contains("Cancel", StringComparison.OrdinalIgnoreCase));
        var decision = Decisions(observer, "cancel_shipment").Should().ContainSingle().Subject;
        decision.Outcome.Should().Be(MentorActionOutcome.Denied);
        decision.ReasonCode.Should().Be(MentorActionReasons.NotOffered);
        decision.AgentKind.Should().Be(MentorAgentKind.Specialist);
        decision.AgentName.Should().Be("ShippingAgent");
    }

    [Fact]
    public async Task A_specialists_call_to_another_agents_action_is_decided_not_offered_and_starts_nothing()
    {
        // count_open_orders is the coordinator's: the specialist does not hold it. Nothing more than the decision: no run,
        // no confirmation.
        var observer = new RecordingActionObserver();
        await using var host = Create(observer, governor: null);
        var confirmations = 0;
        host.Get<IMentorStateService>().OnConfirmationRequired += _ => Interlocked.Increment(ref confirmations);
        host.Chat!.Respond(ShippingCalls(host, "count_open_orders"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        (await host.RunTurnAsync("where is order 7?", deadline.Token)).Should().BeNull();
        await host.Get<MentorActionDecisions>().FlushAsync();

        host.Get<TestOrderService>().Invoked.Should().NotContain("CountOpenOrders");
        confirmations.Should().Be(0, "nothing is asked for a call that runs nothing");
        var decision = Decisions(observer, "count_open_orders").Should().ContainSingle().Subject;
        decision.Outcome.Should().Be(MentorActionOutcome.Denied);
        decision.ReasonCode.Should().Be(MentorActionReasons.NotOffered);
        decision.AgentKind.Should().Be(MentorAgentKind.Specialist);
    }

    [Fact]
    public async Task A_specialists_call_to_a_name_the_application_does_not_declare_has_no_decision()
    {
        var observer = new RecordingActionObserver();
        await using var host = Create(observer, governor: null);
        host.Chat!.Respond(ShippingCalls(host, "launch_rockets"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        (await host.RunTurnAsync("where is order 7?", deadline.Token)).Should().BeNull();
        await host.Get<MentorActionDecisions>().FlushAsync();

        host.Chat.Calls.Should().Contain(c => c.ToolNames.Contains("get_tracking_status"), "the specialist worked");
        Decisions(observer, "launch_rockets").Should().BeEmpty("a name the application does not have is not an action");
    }

    /// <summary>Asks its inner client for the whole reply, and hands it on as updates: an agent whose client does not stream.</summary>
    private sealed class BufferingChatClient(IChatClient inner) : DelegatingChatClient(inner)
    {
        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await base.GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in response.ToChatResponseUpdates()) yield return update;
        }
    }

    /// <summary>An agent of a source that holds every action of the application but <c>count_open_orders</c>, and does not stream.</summary>
    private sealed class LedgerWithoutCountSource : IMentorAgentSource
    {
        public Task<IReadOnlyList<Microsoft.Agents.AI.AIAgent>> GetAgentsAsync(MentorAgentSourceContext context, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Microsoft.Agents.AI.AIAgent>>(
            [
                new Microsoft.Agents.AI.ChatClientAgent(new BufferingChatClient(context.ChatClient), new Microsoft.Agents.AI.ChatClientAgentOptions
                {
                    Name = "LedgerAgent",
                    Description = "Keeps the order ledger",
                    ChatOptions = new ChatOptions
                    {
                        Instructions = "You are the ledger specialist.",
                        Tools = [.. context.Tools.Where(t => t.Name != "count_open_orders")],
                    },
                }),
            ]);
    }

    [Fact]
    public async Task An_agent_sources_call_to_an_action_it_does_not_hold_is_decided_not_offered_when_it_does_not_stream()
    {
        var observer = new RecordingActionObserver();
        await using var host = Create(observer, governor: null, s => s.AddSingleton<IMentorAgentSource>(new LedgerWithoutCountSource()));
        var delegating = ActionDecisionTests.Delegating(host, "how many open orders?", "ledger", "show_order_card", []);
        static bool OfTheAgent(ChatOptions? o) => (o?.Instructions ?? "").Contains("You are the ledger specialist.", StringComparison.Ordinal);
        host.Chat!.Respond((messages, options) =>
            OfTheAgent(options) && !Answered(messages)
                ? Invoke(host, "count_open_orders", [])
                : OfTheAgent(options) ? Text(host, "I could not count them.") : delegating(messages, options));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        (await host.RunTurnAsync("how many open orders?", deadline.Token)).Should().BeNull();
        await host.Get<MentorActionDecisions>().FlushAsync();

        var agent = host.Chat.Calls.Where(c => (c.Instructions ?? "").Contains("You are the ledger specialist.")).ToList();
        agent.Should().NotBeEmpty("the source's agent ran. Warnings: {0}", host.Logs.WarningText);
        agent.Should().OnlyContain(c => !c.Streaming, "the agent's client asks for the whole reply");
        host.Get<TestOrderService>().Invoked.Should().NotContain("CountOpenOrders");
        var decision = Decisions(observer, "count_open_orders").Should().ContainSingle().Subject;
        decision.Outcome.Should().Be(MentorActionOutcome.Denied);
        decision.ReasonCode.Should().Be(MentorActionReasons.NotOffered);
        decision.AgentKind.Should().Be(MentorAgentKind.AgentSource);
    }

    // ── Team members ─────────────────────────────────────────────────────────────

    private static bool Is(ChatOptions? options, string role)
        => (options?.Instructions ?? "").Contains($"Your role in the team is: **{role}**", StringComparison.Ordinal);

    /// <summary>The coordinator asks the team; the Data Analyst calls <paramref name="analystCalls"/> once, then speaks.</summary>
    private static Func<IReadOnlyList<ChatMessage>, ChatOptions?, ChatResponse> AskTheTeam(MentorHostFixture host, string analystCalls)
        => (messages, options) =>
        {
            var tools = options?.Tools?.Select(t => t.Name).ToList() ?? [];
            if (Is(options, "Data Analyst"))
                return Answered(messages) ? Text(host, "Revenue is steady.") : Invoke(host, analystCalls, []);
            if (Is(options, "Strategist")) return Text(host, "Keep the budget. [TEAM:APPROVED]");
            if (tools.Contains("business_analysis_team"))
                return Answered(messages)
                    ? Text(host, "relayed")
                    : Invoke(host, "business_analysis_team", new() { ["task"] = "review the quarter" });
            return Text(host, "SAFE");
        };

    [Fact]
    public async Task A_team_members_call_to_an_action_the_governor_turned_off_is_decided_not_offered()
    {
        var observer = new RecordingActionObserver();
        await using var host = Create(observer, TurningOff("read_quarter_revenue"));
        host.Chat!.Respond(AskTheTeam(host, "read_quarter_revenue"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        (await host.RunTurnAsync("review the quarter", deadline.Token)).Should().BeNull();
        await host.Get<MentorActionDecisions>().FlushAsync();

        host.Chat.Calls.Should().Contain(c => Is(c.Instructions, "Data Analyst"), "the team ran. Warnings: {0}", host.Logs.WarningText);
        var decision = Decisions(observer, "read_quarter_revenue").Should().ContainSingle().Subject;
        decision.Outcome.Should().Be(MentorActionOutcome.Denied);
        decision.ReasonCode.Should().Be(MentorActionReasons.NotOffered);
        decision.AgentKind.Should().Be(MentorAgentKind.TeamMember);
        decision.AgentName.Should().Be("Data Analyst");
    }

    private static bool Is(string? instructions, string role)
        => (instructions ?? "").Contains($"Your role in the team is: **{role}**", StringComparison.Ordinal);

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

    [Fact]
    public async Task A_yaml_team_members_call_to_an_action_the_governor_turned_off_is_decided_not_offered()
    {
        var observer = new RecordingActionObserver();
        await using var host = Create(observer, TurningOff("count_open_orders"),
            s => s.AddMentorAgentDeclarative(d => d.TeamDefinitions.Add(ClerkReview)));
        host.Chat!.Respond((messages, options) =>
        {
            var tools = options?.Tools?.Select(t => t.Name).ToList() ?? [];
            if (Is(options?.Instructions, "Clerk"))
                return Answered(messages) ? Text(host, "The order looks right.") : Invoke(host, "count_open_orders", []);
            if (Is(options?.Instructions, "Approver")) return Text(host, "Fine. [TEAM:APPROVED]");
            if (tools.Contains("order_review"))
                return Answered(messages) ? Text(host, "relayed") : Invoke(host, "order_review", new() { ["task"] = "review order 7" });
            return Text(host, "SAFE");
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        (await host.RunTurnAsync("review order 7", deadline.Token)).Should().BeNull();
        await host.Get<MentorActionDecisions>().FlushAsync();

        host.Chat.Calls.Should().Contain(c => Is(c.Instructions, "Clerk"), "the team ran. Warnings: {0}", host.Logs.WarningText);
        host.Get<TestOrderService>().Invoked.Should().NotContain("CountOpenOrders");
        var decision = Decisions(observer, "count_open_orders").Should().ContainSingle().Subject;
        decision.Outcome.Should().Be(MentorActionOutcome.Denied);
        decision.ReasonCode.Should().Be(MentorActionReasons.NotOffered);
        decision.AgentKind.Should().Be(MentorAgentKind.TeamMember);
        decision.AgentName.Should().Be("Clerk");
    }
}
