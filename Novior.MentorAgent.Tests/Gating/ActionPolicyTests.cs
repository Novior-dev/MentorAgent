using System.Collections.Concurrent;
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
using Microsoft.Extensions.Logging;
using Xunit;

namespace Novior.MentorAgent.Tests.Gating;

/// <summary>
/// Stories 311 and 153 — the application's <see cref="IMentorActionPolicy"/> decides, in its own code, whether a user
/// may use an action and whether a call may run: before it runs, after the roles and before the confirmation, in
/// Blocking and in Native mode alike, for the coordinator, a specialist and the host's own agent. A Stop runs nothing
/// and shows no banner; a Confirm asks the user where there is one; Proceed never removes a confirmation the action
/// asks for. And BUG-203: in Native mode an action gated after the coordinator was built is confirmed at the call.
///
/// <para>
/// In the test application <c>issue_refund</c> asks for confirmation, <c>show_order_card</c> and
/// <c>count_open_orders</c> ask for nothing and need no role.
/// </para>
/// </summary>
public sealed class ActionPolicyTests
{
    private static MentorHostFixture Create(RecordingActionObserver observer, Action<MentorOptions>? configure = null,
        params IMentorActionPolicy[] policies)
        => MentorHostFixture.Create(o =>
        {
            o.MemoryAutoCapture = false;
            configure?.Invoke(o);
        }, s =>
        {
            s.AddMentorActionObserver(observer);
            foreach (var policy in policies) s.AddMentorActionPolicy(policy);
        });

    private static Task FlushAsync(MentorHostFixture host) => host.Get<MentorActionDecisions>().FlushAsync();

    private static ChatResponse ToolCall(MentorHostFixture host, string callId, string tool, Dictionary<string, object?>? args = null)
        => new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(callId, tool, args)])) { ModelId = host.Chat!.ModelId };

    /// <summary>Confirms every banner of the turn.</summary>
    private static void ConfirmEveryBanner(MentorHostFixture host)
    {
        var state = host.Get<IMentorStateService>();
        state.OnConfirmationRequired += req => _ = state.ConfirmAsync(req.ActionId);
    }

    /// <summary>What the model was told about its calls: the tool results, and in Native mode the refusals' reasons.</summary>
    private static IReadOnlyList<string> WhatTheModelRead(FakeChatClient chat)
        => [.. chat.Calls
               .SelectMany(c => c.Messages)
               .SelectMany(m => m.Contents)
               .Select(c => c switch
               {
                   FunctionResultContent r => r.Result?.ToString(),
                   ToolApprovalResponseContent a => a.Reason,
                   _ => null,
               })
               .OfType<string>()];

    // ── What a policy sees ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(MentorHitlMode.Blocking)]
    [InlineData(MentorHitlMode.Native)]
    public async Task A_policy_sees_the_call_before_it_runs_with_its_arguments_user_agent_and_page(MentorHitlMode mode)
    {
        var observer = new RecordingActionObserver();
        var policy = new TestActionPolicy();
        await using var host = Create(observer, o => o.HitlMode = mode, policy);
        host.Auth.SignIn("ada");   // no role: the user is read for an action that needs none too
        host.Get<IMentorPageContext>().SetPageName("Orders");
        host.Chat!.Respond((_, _) => ToolCall(host, "call-1", "show_order_card", new() { ["orderId"] = 7 })).Say("Shown.");

        (await host.RunTurnAsync("show order 7")).Should().BeNull();

        host.Get<TestOrderService>().Invoked.Should().Contain(nameof(TestOrderService.ShowOrderCard));
        policy.Asked("show_order_card").Should().Equal(["IsAllowedAsync", "BeforeActionAsync"], "mode {0}", mode);
        policy.Seen.Where(s => s.Request.Stage == MentorActionStage.Call).Should().AllSatisfy(s =>
        {
            var r = s.Request;
            r.ToolName.Should().Be("show_order_card");
            r.Description.Should().Contain("Shows an order as a card");
            r.Arguments!["orderId"]!.ToString().Should().Be("7");
            r.User!.Identity!.Name.Should().Be("ada");
            r.Surface.Should().Be(MentorActionSurface.InProcess);
            r.AgentKind.Should().Be(MentorAgentKind.Coordinator);
            r.CallId.Should().Be("call-1");
            r.TurnId.Should().NotBeNullOrEmpty();
            r.SessionId.Should().NotBeNullOrEmpty();
            r.PageName.Should().Be("Orders");
            r.RequiredRoles.Should().BeEmpty();
            r.RequiresConfirmation.Should().BeFalse();
            r.Services.GetRequiredService<IMentorStateService>().Should().BeSameAs(host.Get<IMentorStateService>(),
                "the services are the conversation's scope, never the root ones");
        });

        // As the turn decided what to offer: no arguments yet, and no call.
        var offer = policy.Seen.Single(s => s.Request.ToolName == "show_order_card" && s.Request.Stage == MentorActionStage.Offer);
        offer.Method.Should().Be("IsAllowedAsync");
        offer.Request.Arguments.Should().BeNull();
        offer.Request.CallId.Should().BeNull();

        policy.Seen.Select(s => s.Request.ToolName).Should().NotContain(
            ["navigate_to", "remember", "forget_all", "load_skill", "read_skill_resource", "route_to_specialist", "invoke_ui_action"],
            "MentorAgent's own tools are not the policies' to decide");
    }

    [Fact]
    public async Task A_specialists_call_is_the_policys_to_decide_too()
    {
        var observer = new RecordingActionObserver();
        var policy = new TestActionPolicy
        {
            Verdict = r => r.ToolName == "get_tracking_status" ? MentorActionVerdict.Stop("tracking is closed today") : MentorActionVerdict.Proceed,
        };
        await using var host = Create(observer, null, policy);
        host.Chat!.Respond(ActionDecisionTests.Delegating(host, "where is order 7", "shipping", "get_tracking_status",
            new() { ["orderId"] = 7 }));

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        (await host.RunTurnAsync("where is order 7?", deadline.Token)).Should().BeNull();
        await FlushAsync(host);

        host.Get<TestShippingAgent>().Invoked.Should().BeEmpty("the policy stopped it");
        observer.Steps("get_tracking_status").Should().Equal("Blocked/policy");
        var asked = policy.Seen.Where(s => s.Request.ToolName == "get_tracking_status").Select(s => s.Request).ToList();
        asked.Should().NotBeEmpty().And.AllSatisfy(r =>
        {
            r.AgentKind.Should().Be(MentorAgentKind.Specialist);
            r.AgentName.Should().Be("ShippingAgent");
        });
    }

    [Fact]
    public async Task On_path_B_the_policy_decides_the_host_agents_calls()
    {
        var observer = new RecordingActionObserver();
        var policy = new TestActionPolicy { Verdict = _ => MentorActionVerdict.Stop("closed") };
        var inner = new FakeChatClient();
        await using var host = MentorHostFixture.Create(o =>
        {
            o.ChatClient = null;
            o.Agent = inner.AsAIAgent(instructions: "You are a test agent.", name: "HostAgent");
            o.MemoryAutoCapture = false;
        }, s =>
        {
            s.AddMentorActionObserver(observer);
            s.AddMentorActionPolicy(policy);
        }, withChatClient: false);
        inner.CallTool("count_open_orders").Say("It is closed.");

        (await host.RunTurnAsync("how many open orders?")).Should().BeNull();
        await FlushAsync(host);

        host.Get<TestOrderService>().Invoked.Should().BeEmpty();
        observer.Steps("count_open_orders").Should().Equal("Blocked/policy");
        policy.Seen.Single(s => s.Method == "BeforeActionAsync").Request.AgentName.Should().Be("HostAgent");
    }

    // ── Stop, Confirm, Proceed ───────────────────────────────────────────────────

    [Theory]
    [InlineData(MentorHitlMode.Blocking)]
    [InlineData(MentorHitlMode.Native)]
    public async Task A_Stop_runs_nothing_shows_no_banner_and_tells_the_model_the_rule(MentorHitlMode mode)
    {
        var observer = new RecordingActionObserver();
        var policy = new TestActionPolicy { Verdict = _ => MentorActionVerdict.Stop("order 3 is locked by accounting") };
        await using var host = Create(observer, o => o.HitlMode = mode, policy);
        var banners = 0;
        host.Get<IMentorStateService>().OnConfirmationRequired += _ => banners++;
        host.Chat!.CallTool("issue_refund", new { orderId = 3 }).Say("It is locked.");

        (await host.RunTurnAsync("refund order 3")).Should().BeNull();
        await FlushAsync(host);

        host.Get<TestOrderService>().Invoked.Should().BeEmpty();
        banners.Should().Be(0, "mode {0}: a call the rule stops is never shown for confirmation", mode);
        observer.Steps("issue_refund").Should().Equal(["Blocked/policy"], "mode {0}: Allowed only once every check passed", mode);
        observer.Seen.Single().Detail.Should().Be("order 3 is locked by accounting");
        WhatTheModelRead(host.Chat).Should().Contain(t => t.Contains("Stopped by a rule: order 3 is locked by accounting"))
            .And.NotContain(t => t.Contains("declined", StringComparison.OrdinalIgnoreCase), "nobody declined anything");
    }

    [Theory]
    [InlineData(MentorHitlMode.Blocking)]
    [InlineData(MentorHitlMode.Native)]
    public async Task A_Confirm_asks_the_user_about_an_action_that_does_not_ask_itself(MentorHitlMode mode)
    {
        // count_open_orders has no confirmation, so in Native mode it carries no approval marker: the gate asks.
        var observer = new RecordingActionObserver();
        var policy = new TestActionPolicy { Verdict = _ => MentorActionVerdict.Confirm("over the daily limit") };
        await using var host = Create(observer, o => o.HitlMode = mode, policy);
        ConfirmEveryBanner(host);
        host.Chat!.CallTool("count_open_orders").Say("Three.");

        (await host.RunTurnAsync("how many open orders?")).Should().BeNull();
        await FlushAsync(host);

        host.Get<TestOrderService>().Invoked.Should().Contain(nameof(TestOrderService.CountOpenOrders));
        observer.Steps("count_open_orders").Should().Equal(
            ["Allowed/allowed", "ConfirmationRequested/policy", "Confirmed/operator", "Executed/completed"], "mode {0}", mode);
        observer.Seen.Single(d => d.Outcome == MentorActionOutcome.ConfirmationRequested).Detail.Should().Be("over the daily limit");
    }

    [Theory]
    [InlineData(MentorHitlMode.Blocking)]
    [InlineData(MentorHitlMode.Native)]
    public async Task Proceed_never_removes_a_confirmation_and_the_policy_is_asked_again_after_it(MentorHitlMode mode)
    {
        var observer = new RecordingActionObserver();
        var policy = new TestActionPolicy();
        await using var host = Create(observer, o => o.HitlMode = mode, policy);
        ConfirmEveryBanner(host);
        host.Chat!.CallTool("issue_refund", new { orderId = 3 }).Say("Refunded.");

        (await host.RunTurnAsync("refund order 3")).Should().BeNull();
        await FlushAsync(host);

        observer.Steps("issue_refund").Should().Equal(
            ["Allowed/allowed", "ConfirmationRequested/operator", "Confirmed/operator", "Executed/completed"], "mode {0}", mode);
        policy.Asked("issue_refund").Should().Equal(["IsAllowedAsync", "BeforeActionAsync", "IsAllowedAsync"],
            "mode {0}: the permission is read again once the user has confirmed", mode);
        policy.Seen.Where(s => s.Request.Stage == MentorActionStage.Call)
            .Should().AllSatisfy(s => s.Request.RequiresConfirmation.Should().BeTrue());
    }

    [Theory]
    [InlineData(MentorHitlMode.Blocking)]
    [InlineData(MentorHitlMode.Native)]
    public async Task A_permission_taken_away_while_the_banner_waits_stops_the_action(MentorHitlMode mode)
    {
        var observer = new RecordingActionObserver();
        var allowed = true;
        var policy = new TestActionPolicy { Allowed = r => r.Stage == MentorActionStage.Offer || allowed };
        await using var host = Create(observer, o => o.HitlMode = mode, policy);
        var state = host.Get<IMentorStateService>();
        state.OnConfirmationRequired += req =>
        {
            allowed = false;
            _ = state.ConfirmAsync(req.ActionId);
        };
        host.Chat!.CallTool("issue_refund", new { orderId = 3 }).Say("Not allowed any more.");

        (await host.RunTurnAsync("refund order 3")).Should().BeNull();
        await FlushAsync(host);

        host.Get<TestOrderService>().Invoked.Should().BeEmpty();
        observer.Steps("issue_refund").Should().Equal(
            ["Allowed/allowed", "ConfirmationRequested/operator", "Confirmed/operator", "Denied/not-authorized"], "mode {0}", mode);
    }

    // ── More than one policy ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("proceed", "confirm:b", "ConfirmationRequested/policy", "b")]
    [InlineData("confirm:a", "stop:b", "Blocked/policy", "b")]
    [InlineData("stop:a", "confirm:b", "Blocked/policy", "a")]
    [InlineData("stop:a", "stop:b", "Blocked/policy", "a")]
    public async Task The_strictest_answer_wins_and_every_policy_is_asked_in_order(
        string first, string second, string decided, string detail)
    {
        static Func<MentorActionRequest, MentorActionVerdict> Answer(string verdict) => verdict.Split(':') switch
        {
            ["confirm", var reason] => _ => MentorActionVerdict.Confirm(reason),
            ["stop", var reason]    => _ => MentorActionVerdict.Stop(reason),
            _                       => _ => MentorActionVerdict.Proceed,
        };

        var observer = new RecordingActionObserver();
        var order = new ConcurrentQueue<string>();
        var p1 = new TestActionPolicy("p1", order) { Verdict = Answer(first) };
        var p2 = new TestActionPolicy("p2", order) { Verdict = Answer(second) };
        await using var host = Create(observer, null, p1, p2);
        ConfirmEveryBanner(host);
        host.Chat!.CallTool("count_open_orders").Say("Done.");

        (await host.RunTurnAsync("how many open orders?")).Should().BeNull();
        await FlushAsync(host);

        observer.Steps("count_open_orders").Should().Contain(decided);
        observer.Seen.First(d => d.ReasonCode == MentorActionReasons.Policy).Detail.Should().Be(detail);
        order.Where(e => e.EndsWith(".Call", StringComparison.Ordinal)).Take(4).Should().Equal(
            "p1.IsAllowedAsync.Call", "p2.IsAllowedAsync.Call", "p1.BeforeActionAsync.Call", "p2.BeforeActionAsync.Call");
    }

    [Fact]
    public async Task The_first_no_ends_the_question_and_the_call_is_denied()
    {
        var observer = new RecordingActionObserver();
        var p1 = new TestActionPolicy("p1") { Allowed = r => r.Stage == MentorActionStage.Offer };
        var p2 = new TestActionPolicy("p2");
        await using var host = Create(observer, null, p1, p2);
        host.Chat!.CallTool("count_open_orders").Say("Not allowed.");

        (await host.RunTurnAsync("how many open orders?")).Should().BeNull();
        await FlushAsync(host);

        host.Get<TestOrderService>().Invoked.Should().BeEmpty();
        observer.Steps("count_open_orders").Should().Equal("Denied/not-authorized");
        p2.Asked("count_open_orders").Should().BeEmpty("p1 said no");
        WhatTheModelRead(host.Chat).Should().Contain(t => t.Contains("you are not allowed to use this action"));
    }

    // ── A policy that fails ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_policy_that_throws_or_does_not_answer_stops_the_call(bool throws)
    {
        var observer = new RecordingActionObserver();
        var policy = new TestActionPolicy
        {
            Before = async (r, ct) =>
            {
                if (r.Stage != MentorActionStage.Call) return;
                if (throws) throw new InvalidOperationException("secret ledger 42");
                await Task.Delay(Timeout.Infinite, ct);
            },
        };
        await using var host = Create(observer, o => o.ActionPolicyTimeout = TimeSpan.FromMilliseconds(200), policy);
        host.Chat!.CallTool("count_open_orders").Say("It could not be checked.");

        (await host.RunTurnAsync("how many open orders?")).Should().BeNull();
        await FlushAsync(host);

        host.Get<TestOrderService>().Invoked.Should().BeEmpty();
        observer.Steps("count_open_orders").Should().Equal(throws ? "PolicyFailed/policy-exception" : "PolicyFailed/policy-timeout");
        observer.Seen.Single().ExceptionType.Should().Be(throws ? typeof(InvalidOperationException).FullName : null);
        host.Logs.HasWarning(throws ? "threw (InvalidOperationException)" : "did not answer within 0.2 s").Should().BeTrue(host.Logs.WarningText);
        host.Logs.Records.Should().NotContain(r => r.Message.Contains("secret ledger 42"), "never the policy's message (BUG-148)");
        WhatTheModelRead(host.Chat).Should().Contain(t => t.Contains("the check before this action could not be completed"));
    }

    [Fact]
    public async Task A_policy_that_fails_as_the_turn_decides_what_to_offer_leaves_the_action_out()
    {
        var policy = new TestActionPolicy
        {
            Before = (r, _) => r.Stage == MentorActionStage.Offer && r.ToolName == "count_open_orders"
                ? throw new InvalidOperationException("down")
                : Task.CompletedTask,
        };
        await using var host = Create(new RecordingActionObserver(), null, policy);
        host.Chat!.Say("Hello.");

        (await host.RunTurnAsync("hello")).Should().BeNull();

        var call = host.Chat.Calls.Last(c => c.ToolNames.Count > 0);
        call.ToolNames.Should().NotContain("count_open_orders").And.Contain("show_order_card");
    }

    // ── 153: who may use an action ───────────────────────────────────────────────

    [Fact]
    public async Task An_action_a_policy_does_not_allow_a_user_is_left_out_for_that_user_only()
    {
        var observer = new RecordingActionObserver();
        var policy = new TestActionPolicy { Allowed = r => r.ToolName != "count_open_orders" || r.User?.Identity?.Name == "ada" };
        await using var host = Create(observer, null, policy);
        host.Chat!.Say("Hello.").CallTool("count_open_orders").Say("Not for you.");

        // Both hold every role: only the policy leaves an action out.
        host.Auth.SignIn("ada", "Manager", "Admin");
        (await host.RunTurnAsync("hello")).Should().BeNull();
        var ada = host.Chat.Calls.Last(c => c.ToolNames.Count > 0);

        var before = host.Chat.Calls.Count;
        host.Auth.SignIn("bob", "Manager", "Admin");
        (await host.RunTurnAsync("how many open orders?")).Should().BeNull();
        var bob = host.Chat.Calls.Skip(before).First(c => c.ToolNames.Count > 0);
        await FlushAsync(host);

        ada.ToolNames.Should().Contain("count_open_orders");
        (ada.Instructions ?? "").Should().NotContain("NOT available to this user");
        bob.ToolNames.Should().NotContain("count_open_orders").And.Contain("show_order_card");
        (bob.Instructions ?? "").Should().Contain("Some other actions of this application are NOT available to this user on this turn")
            .And.NotContain("count_open_orders", "the notice names no action");

        // bob's model calls it anyway: refused, and never run.
        host.Get<TestOrderService>().Invoked.Should().BeEmpty();
        observer.Steps("count_open_orders").Should().Equal("Denied/not-authorized");
    }

    // ── A task from another agent ────────────────────────────────────────────────

    [Theory]
    [InlineData(MentorHitlMode.Blocking)]
    [InlineData(MentorHitlMode.Native)]
    public async Task A_task_from_another_agent_cannot_confirm_what_a_policy_asks(MentorHitlMode mode)
    {
        var observer = new RecordingActionObserver();
        var policy = new TestActionPolicy { Verdict = _ => MentorActionVerdict.Confirm("over the daily limit") };
        await using var host = Create(observer, o => o.HitlMode = mode, policy);
        host.Get<MentorTurnOrigin>().FromAnotherAgent = true;
        host.Chat!.CallTool("count_open_orders").Say("Not run.");

        (await host.RunTurnAsync("how many open orders?")).Should().BeNull();
        await FlushAsync(host);

        host.Get<TestOrderService>().Invoked.Should().BeEmpty();
        observer.Steps("count_open_orders").Should().Equal(["Denied/nobody-to-confirm"],
            "mode {0}: alone, as on the MCP server", mode);
        observer.Seen.Last().Detail.Should().Be("over the daily limit");
        policy.Seen.Should().AllSatisfy(s => s.Request.PageName.Should().BeNull("a task from another agent has no page"));
    }

    // ── What a host without policies pays ────────────────────────────────────────

    [Fact]
    public async Task Without_a_policy_the_gate_reads_nobody_more()
    {
        async Task<int> ReadsAsync(bool withPolicy)
        {
            await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false, s =>
            {
                if (withPolicy) s.AddMentorActionPolicy(new TestActionPolicy());
            });
            host.Auth.SignIn("ada");
            host.Chat!.CallTool("count_open_orders").Say("Three.");
            (await host.RunTurnAsync("how many open orders?")).Should().BeNull();
            return host.Auth.Reads;
        }

        var without = await ReadsAsync(withPolicy: false);
        var with = await ReadsAsync(withPolicy: true);

        with.Should().Be(without + 1, "one read for the call's policies, and none without a policy");
    }

    // ── BUG-203 ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task In_native_mode_an_action_gated_after_the_coordinator_was_built_is_confirmed(bool fromAnotherAgent)
    {
        // The approval markers are decided when the coordinator is built. A RequiresApproval that turns true later
        // left the tool unmarked, and it ran with no banner: the gate now asks for it.
        var observer = new RecordingActionObserver();
        var gated = false;
        await using var host = Create(observer, o =>
        {
            o.HitlMode = MentorHitlMode.Native;
            o.RequiresApproval = name => gated && name == "count_open_orders";
        });
        ConfirmEveryBanner(host);
        host.Chat!.Say("Hello.").CallTool("count_open_orders").Say("Three.");
        (await host.RunTurnAsync("hello")).Should().BeNull();   // the coordinator is built

        gated = true;
        host.Get<MentorTurnOrigin>().FromAnotherAgent = fromAnotherAgent;
        (await host.RunTurnAsync("how many open orders?")).Should().BeNull();
        await FlushAsync(host);

        observer.Steps("count_open_orders").Should().Equal(fromAnotherAgent
            ? ["Denied/nobody-to-confirm"]
            : ["Allowed/allowed", "ConfirmationRequested/operator", "Confirmed/operator", "Executed/completed"]);
        host.Get<TestOrderService>().Invoked.Contains(nameof(TestOrderService.CountOpenOrders)).Should().Be(!fromAnotherAgent);
    }
}
