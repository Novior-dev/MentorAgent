using System.Diagnostics;
using FluentAssertions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Declarative;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.App;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Novior.MentorAgent.Tests.Gating;

/// <summary>
/// Story 180 — every decision of the tool gate reaches the <see cref="IMentorActionObserver"/>s, with who asked,
/// where, in which turn and why.
///
/// <para>
/// A refusal for a missing role was a Warning line and nothing else, a Stop or a rejection a Debug line, and in
/// Native mode an anonymous user refused before the banner left a Debug line only, where Blocking wrote a
/// Warning. Native and Blocking are documented as the same from the outside, so the decisions and their log are
/// asserted in both.
/// </para>
/// </summary>
public sealed class ActionDecisionTests
{
    private static MentorHostFixture Create(RecordingActionObserver observer, Action<MentorOptions>? configure = null)
        => MentorHostFixture.Create(o =>
        {
            o.MemoryAutoCapture = false;
            configure?.Invoke(o);
        }, s => s.AddMentorActionObserver(observer));

    private static Task FlushAsync(MentorHostFixture host) => host.Get<MentorActionDecisions>().FlushAsync();

    private static ChatResponse ToolCall(MentorHostFixture host, string callId, string tool, Dictionary<string, object?>? args = null)
        => new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(callId, tool, args)])) { ModelId = host.Chat!.ModelId };

    private static IReadOnlyList<(LogLevel Level, string Message)> GateLines(MentorHostFixture host, string tool)
        => [.. host.Logs.Records
               .Where(r => r.Level >= LogLevel.Warning && r.Message.Contains($"'{tool}'") && !r.Message.Contains("without confirmation"))
               .Select(r => (r.Level, r.Message))];

    // ── What a decision says ─────────────────────────────────────────────────────

    [Fact]
    public async Task A_call_is_allowed_then_executed_with_who_asked_where_and_in_which_turn()
    {
        var observer = new RecordingActionObserver();
        await using var host = Create(observer);
        host.Auth.SignIn("ada");
        host.Chat!.Respond((_, _) => ToolCall(host, "call-1", "count_open_orders")).Say("Three.");

        (await host.RunTurnAsync("how many open orders?")).Should().BeNull();
        await FlushAsync(host);

        observer.Steps("count_open_orders").Should().Equal("Allowed/allowed", "Executed/completed");
        var decisions = observer.Seen.Where(d => d.ToolName == "count_open_orders").ToList();
        decisions.Should().AllSatisfy(d =>
        {
            d.Surface.Should().Be(MentorActionSurface.InProcess);
            d.AgentKind.Should().Be(MentorAgentKind.Coordinator);
            d.CallId.Should().Be("call-1", "the model's id for the call");
            d.User!.Identity!.Name.Should().Be("ada", "the user is read for an action without roles too");
            d.RequiredRoles.Should().BeEmpty();
            d.DisplayName.Should().NotBeNullOrWhiteSpace().And.NotBe("count_open_orders", "the text shown is the description");
            d.ConfirmationId.Should().BeNull();
            d.ExceptionType.Should().BeNull();
            d.Timestamp.Offset.Should().Be(TimeSpan.Zero);
        });
        decisions.Select(d => d.TurnId).Distinct().Should().ContainSingle().Which.Should().NotBeNullOrEmpty();
        decisions.Select(d => d.SessionId).Distinct().Should().ContainSingle().Which.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Every_message_is_a_turn_of_its_own_in_the_same_session()
    {
        var observer = new RecordingActionObserver();
        await using var host = Create(observer);
        host.Chat!.CallTool("count_open_orders").Say("Three.").CallTool("count_open_orders").Say("Still three.");

        (await host.RunTurnAsync("how many?")).Should().BeNull();
        (await host.RunTurnAsync("and now?")).Should().BeNull();
        await FlushAsync(host);

        var executed = observer.Seen.Where(d => d.Outcome == MentorActionOutcome.Executed).ToList();
        executed.Should().HaveCount(2);
        executed[0].TurnId.Should().NotBe(executed[1].TurnId);
        executed[0].SessionId.Should().Be(executed[1].SessionId);
    }

    [Fact]
    public async Task An_agent_the_host_built_raises_the_same_decisions_with_its_name()
    {
        // Path B: the host's own agent, wrapped in the gate by MentorAgent.
        var observer = new RecordingActionObserver();
        var inner = new FakeChatClient();
        await using var host = MentorHostFixture.Create(o =>
        {
            o.ChatClient = null;
            o.Agent = inner.AsAIAgent(instructions: "You are a test agent.", name: "HostAgent");
            o.MemoryAutoCapture = false;
        }, s => s.AddMentorActionObserver(observer), withChatClient: false);
        inner.CallTool("count_open_orders").Say("Three.");

        (await host.RunTurnAsync("how many open orders?")).Should().BeNull();
        await FlushAsync(host);

        observer.Steps("count_open_orders").Should().Equal("Allowed/allowed", "Executed/completed");
        observer.Seen.Should().AllSatisfy(d =>
        {
            d.AgentKind.Should().Be(MentorAgentKind.Coordinator);
            d.AgentName.Should().Be("HostAgent");
        });
    }

    [Fact]
    public async Task A_failed_action_carries_the_type_of_its_exception_and_never_its_message()
    {
        var observer = new RecordingActionObserver();
        await using var host = Create(observer);
        host.Get<TestOrderService>().NextFailure = new InvalidOperationException("mario.rossi@example.com is not allowed");
        host.Chat!.CallTool("failing_action").Say("Sorry.");

        (await host.RunTurnAsync("run it")).Should().BeNull();
        await FlushAsync(host);

        observer.Steps("failing_action").Should().Equal("Allowed/allowed", "Failed/exception");
        var failed = observer.Seen.Single(d => d.Outcome == MentorActionOutcome.Failed);
        failed.ExceptionType.Should().Be("System.InvalidOperationException");
        failed.ToString().Should().NotContain("mario.rossi", "the exception's text never reaches a decision");
    }

    [Fact]
    public async Task An_action_whose_result_hook_throws_ran_and_says_its_result_was_withheld()
    {
        var observer = new RecordingActionObserver();
        await using var host = Create(observer, o => o.OnToolResult = (_, _) => throw new InvalidOperationException("hook down"));
        host.Chat!.CallTool("count_open_orders").Say("Three.");

        (await host.RunTurnAsync("how many open orders?")).Should().BeNull();
        await FlushAsync(host);

        observer.Steps("count_open_orders").Should().Equal("Allowed/allowed", "Executed/result-withheld");
    }

    [Fact]
    public async Task A_user_who_cannot_be_read_is_refused_with_auth_check_failed()
    {
        var observer = new RecordingActionObserver();
        await using var host = Create(observer);
        host.Auth.ThrowOnRead = new InvalidOperationException("the token service is down");
        host.Chat!.CallTool("export_ledger").Say("I cannot.");

        await host.RunTurnAsync("export the ledger");
        await FlushAsync(host);

        observer.Steps("export_ledger").Should().Equal("Denied/auth-check-failed");
    }

    [Theory]
    [InlineData(MentorHitlMode.Blocking)]
    [InlineData(MentorHitlMode.Native)]
    public async Task A_task_from_another_agent_is_refused_an_action_that_asks_with_nobody_to_confirm(MentorHitlMode mode)
    {
        var observer = new RecordingActionObserver();
        await using var host = Create(observer, o => o.HitlMode = mode);
        host.Get<MentorTurnOrigin>().FromAnotherAgent = true;
        host.Chat!.CallTool("issue_refund", new { orderId = 3 }).Say("Not run.");

        await host.RunTurnAsync("refund order 3");
        await FlushAsync(host);

        observer.Steps("issue_refund").Should().Equal(["Denied/nobody-to-confirm"],
            "mode {0}: decided before Allowed, as on the MCP server", mode);
    }

    [Fact]
    public async Task Without_an_observer_the_gate_reads_nobody_for_an_action_without_roles()
    {
        // The decision names the user of an action that requires no role, so the gate reads the user for it: once
        // per call, and only when somebody listens. A host without observers pays nothing for them.
        async Task<int> ReadsAsync(bool observed)
        {
            await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false, s =>
            {
                if (observed) s.AddMentorActionObserver(new RecordingActionObserver());
            });
            host.Auth.SignIn("ada");
            host.Chat!.CallTool("count_open_orders").Say("Three.");
            (await host.RunTurnAsync("how many open orders?")).Should().BeNull();
            return host.Auth.Reads;
        }

        var without = await ReadsAsync(observed: false);
        var with = await ReadsAsync(observed: true);

        with.Should().Be(without + 1, "one read for the call's decisions, and none without an observer");
    }

    /// <summary>An observer that throws on every decision, or waits until the test lets it go.</summary>
    private sealed class BadObserver(bool throws) : IMentorActionObserver
    {
        internal TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask OnDecisionAsync(MentorActionDecision decision, CancellationToken cancellationToken)
        {
            if (throws) throw new InvalidOperationException("observer down");
            await Gate.Task;
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_observer_that_throws_or_hangs_neither_stops_nor_holds_the_action(bool throws)
    {
        var bad = new BadObserver(throws);
        var observer = new RecordingActionObserver();
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false, s =>
        {
            s.AddMentorActionObserver(bad);
            s.AddMentorActionObserver(observer);
        });
        host.Chat!.CallTool("count_open_orders").Say("Three.");

        try
        {
            // On another thread: a gate that waited for the observer fails this test instead of hanging it.
            var turn = Task.Run(() => host.RunTurnAsync("how many open orders?"));
            (await Task.WhenAny(turn, Task.Delay(TimeSpan.FromSeconds(10)))).Should().BeSameAs(turn,
                "the turn never waits for an observer");
            (await turn).Should().BeNull();
            host.Get<TestOrderService>().Invoked.Should().Contain(nameof(TestOrderService.CountOpenOrders));

            await UntilAsync(() => observer.Seen.Count == 2);
            observer.Steps("count_open_orders").Should().Equal("Allowed/allowed", "Executed/completed");
        }
        finally
        {
            bad.Gate.TrySetResult();
        }

        await FlushAsync(host);
        if (throws)
            host.Logs.HasWarning($"Action observer {typeof(BadObserver).FullName} threw (InvalidOperationException)")
                .Should().BeTrue(host.Logs.WarningText);
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++) await Task.Delay(20);
        condition().Should().BeTrue();
    }

    // ── Refusals ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(MentorHitlMode.Blocking)]
    [InlineData(MentorHitlMode.Native)]
    public async Task A_missing_role_is_one_decision_and_one_Warning_in_both_modes(MentorHitlMode mode)
    {
        // cancel_order asks for confirmation and requires Manager: in Native mode the orchestrator refuses it
        // before the banner, in Blocking mode the middleware does.
        var observer = new RecordingActionObserver();
        await using var host = Create(observer, o => o.HitlMode = mode);
        host.Auth.SignIn("eve", "Viewer");
        host.Chat!.CallTool("cancel_order", new { orderId = 7, reason = "test" }).Say("I cannot.");

        (await host.RunTurnAsync("cancel order 7")).Should().BeNull();
        await FlushAsync(host);

        observer.Steps("cancel_order").Should().Equal("Denied/role-missing");
        var denied = observer.Seen.Single();
        denied.User!.Identity!.Name.Should().Be("eve");
        denied.RequiredRoles.Should().Equal("Manager");
        GateLines(host, "cancel_order").Should().ContainSingle("mode {0}: one line for one refusal", mode)
            .Which.Level.Should().Be(LogLevel.Warning);
    }

    [Theory]
    [InlineData(MentorHitlMode.Blocking)]
    [InlineData(MentorHitlMode.Native)]
    public async Task Nobody_signed_in_is_refused_with_a_Warning_in_both_modes(MentorHitlMode mode)
    {
        var observer = new RecordingActionObserver();
        await using var host = Create(observer, o => o.HitlMode = mode);
        host.Chat!.CallTool("cancel_order", new { orderId = 7, reason = "test" }).Say("Please sign in.");

        (await host.RunTurnAsync("cancel order 7")).Should().BeNull();
        await FlushAsync(host);

        observer.Steps("cancel_order").Should().Equal("Denied/login-required");
        observer.Seen.Single().User.Should().BeNull();
        GateLines(host, "cancel_order").Should().ContainSingle(
                "mode {0}: Native wrote a Debug line only. {1}", mode, host.Logs.WarningText)
            .Which.Level.Should().Be(LogLevel.Warning);
    }

    // ── The confirmation, in both modes ──────────────────────────────────────────

    public enum Banner { Confirm, Reject, Stop, ConfirmThenStop, SessionEnds, RoleTakenAway }

    [Theory]
    [InlineData(MentorHitlMode.Blocking, Banner.Confirm, "Confirmed/operator", "Executed/completed")]
    [InlineData(MentorHitlMode.Native, Banner.Confirm, "Confirmed/operator", "Executed/completed")]
    [InlineData(MentorHitlMode.Blocking, Banner.Reject, "Rejected/operator")]
    [InlineData(MentorHitlMode.Native, Banner.Reject, "Rejected/operator")]
    [InlineData(MentorHitlMode.Blocking, Banner.Stop, "Stopped/stop")]
    [InlineData(MentorHitlMode.Native, Banner.Stop, "Stopped/stop")]
    [InlineData(MentorHitlMode.Blocking, Banner.ConfirmThenStop, "Confirmed/operator", "Stopped/stop")]
    [InlineData(MentorHitlMode.Native, Banner.ConfirmThenStop, "Confirmed/operator", "Stopped/stop")]
    [InlineData(MentorHitlMode.Blocking, Banner.SessionEnds, "Stopped/session-ended")]
    [InlineData(MentorHitlMode.Native, Banner.SessionEnds, "Stopped/session-ended")]
    [InlineData(MentorHitlMode.Blocking, Banner.RoleTakenAway, "Confirmed/operator", "Denied/role-missing")]
    [InlineData(MentorHitlMode.Native, Banner.RoleTakenAway, "Confirmed/operator", "Denied/role-missing")]
    public async Task A_confirmation_reads_the_same_in_both_modes(MentorHitlMode mode, Banner banner, params string[] then)
    {
        var observer = new RecordingActionObserver();
        await using var host = Create(observer, o => o.HitlMode = mode);
        host.Auth.SignIn("ada", "Manager");
        host.Chat!.Respond((_, _) => ToolCall(host, "call-7", "cancel_order", new() { ["orderId"] = 7, ["reason"] = "test" }))
            .Say("done");

        using var session = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var state = host.Get<IMentorStateService>();
        state.OnConfirmationRequired += req =>
        {
            switch (banner)
            {
                case Banner.Confirm:       _ = state.ConfirmAsync(req.ActionId); break;
                case Banner.Reject:        state.Cancel(req.ActionId); break;
                case Banner.Stop:          host.Orchestrator.CancelCurrentRequest(); break;
                // Stop right after the answer, before the action starts: the Stop wins (BUG-204).
                case Banner.ConfirmThenStop:
                    _ = state.ConfirmAsync(req.ActionId);
                    host.Orchestrator.CancelCurrentRequest();
                    break;
                case Banner.SessionEnds:   session.Cancel(); break;
                case Banner.RoleTakenAway: host.Auth.SignIn("ada"); _ = state.ConfirmAsync(req.ActionId); break;
            }
        };

        await host.RunTurnAsync("cancel order 7", session.Token);
        await FlushAsync(host);

        observer.Steps("cancel_order").Should().Equal(
            ["Allowed/allowed", "ConfirmationRequested/operator", .. then], "mode {0}", mode);
        var asked = observer.Seen.Where(d => d.Outcome != MentorActionOutcome.Allowed).ToList();
        asked.Select(d => d.ConfirmationId).Distinct().Should().ContainSingle()
            .Which.Should().NotBeNull("every decision after the banner names its confirmation");
        asked.Should().AllSatisfy(d => d.CallId.Should().Be("call-7"));
        observer.Seen.Select(d => d.TurnId).Distinct().Should().ContainSingle();

        if (banner == Banner.Confirm)
            observer.Seen.Single(d => d.Outcome == MentorActionOutcome.Confirmed)
                .ConfirmedBy!.Identity!.Name.Should().Be("ada", "in process, the one who confirms is the session's user");
        if (banner == Banner.Stop)
            host.Logs.Records.Should().Contain(r => r.Level == LogLevel.Debug && r.Message.Contains("cancelled (Stop pressed"),
                "mode {0}: Native wrote nothing", mode);
        host.Get<TestOrderService>().Invoked.Should().HaveCount(banner is Banner.Confirm ? 1 : 0, "mode {0}", mode);
    }

    [Theory]
    [InlineData(MentorHitlMode.Blocking, "Executed/completed")]
    [InlineData(MentorHitlMode.Native, "Stopped/stop")]
    public async Task Every_confirmation_ends_in_exactly_one_outcome_in_its_own_turn(MentorHitlMode mode, string firstOfTwo)
    {
        // Three turns in one session. The first is stopped at its banner, so Native leaves the approval open for the
        // next turn to close. In the second the model asks for two refunds at once: the first is confirmed, the
        // second stopped at its banner. Blocking ran the first as soon as it was confirmed; Native asks for both
        // before it runs either, so the first, confirmed, never runs. The third turn confirms and runs.
        var observer = new RecordingActionObserver();
        await using var host = Create(observer, o => o.HitlMode = mode);
        var turn = 0;
        var call = 0;
        host.Chat!.Respond((messages, options) =>
        {
            if (options?.Tools is not { Count: > 0 }) return new(new ChatMessage(ChatRole.Assistant, "SAFE"));
            if (messages.LastOrDefault()?.Contents.OfType<FunctionResultContent>().Any() == true)
                return new(new ChatMessage(ChatRole.Assistant, "done")) { ModelId = host.Chat.ModelId };
            List<AIContent> calls = [new FunctionCallContent($"call-{++call}", "issue_refund", new Dictionary<string, object?> { ["orderId"] = call })];
            if (turn == 2) calls.Add(new FunctionCallContent($"call-{++call}", "issue_refund", new Dictionary<string, object?> { ["orderId"] = call }));
            return new(new ChatMessage(ChatRole.Assistant, calls)) { ModelId = host.Chat.ModelId };
        });

        var state = host.Get<IMentorStateService>();
        var banners = 0;
        state.OnConfirmationRequired += req =>
        {
            banners++;
            if (turn == 1 || (turn == 2 && banners == 3)) host.Orchestrator.CancelCurrentRequest();
            else _ = state.ConfirmAsync(req.ActionId);
        };

        for (turn = 1; turn <= 3; turn++)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await host.RunTurnAsync($"refund {turn}", deadline.Token);
        }
        await FlushAsync(host);

        EveryConfirmationEndsOnce(observer.Seen);
        foreach (var confirmation in observer.Seen.Where(d => d.ConfirmationId is not null).GroupBy(d => d.ConfirmationId))
            confirmation.Select(d => d.TurnId).Distinct().Should().ContainSingle(
                "a call ends in the turn that asked for it, not in the one that closes it in the conversation");

        var firstOfTurnTwo = observer.Seen.Where(d => d.CallId == "call-2").Select(d => $"{d.Outcome}/{d.ReasonCode}").ToList();
        firstOfTurnTwo.Should().Equal(
            ["Allowed/allowed", "ConfirmationRequested/operator", "Confirmed/operator", firstOfTwo], "mode {0}", mode);
        observer.Seen.Where(d => d.CallId == "call-3").Select(d => d.Outcome).Last().Should().Be(MentorActionOutcome.Stopped);
        observer.Seen.Where(d => d.CallId == "call-4").Select(d => d.Outcome).Last().Should().Be(MentorActionOutcome.Executed);
    }

    [Fact]
    public async Task The_approval_round_cap_leaves_no_confirmation_without_its_outcome()
    {
        // A model that asks again after every answer: Native gives up after eight rounds, and the request it never
        // showed is answered "not run" by the next turn. Eight banners, eight runs, nothing half-told.
        var observer = new RecordingActionObserver();
        await using var host = Create(observer, o => o.HitlMode = MentorHitlMode.Native);
        var call = 0;
        host.Chat!.Respond((_, options) => options?.Tools is not { Count: > 0 }
            ? new(new ChatMessage(ChatRole.Assistant, "SAFE"))
            : ToolCall(host, $"call-{++call}", "issue_refund", new() { ["orderId"] = call }));
        var state = host.Get<IMentorStateService>();
        state.OnConfirmationRequired += req => _ = state.ConfirmAsync(req.ActionId);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await host.RunTurnAsync("refund everything", deadline.Token);
        await FlushAsync(host);

        host.Logs.HasWarning("gave up after 8 approval rounds").Should().BeTrue(host.Logs.WarningText);
        observer.Seen.Count(d => d.Outcome == MentorActionOutcome.ConfirmationRequested).Should().Be(8);
        EveryConfirmationEndsOnce(observer.Seen);
    }

    [Fact]
    public async Task An_auto_approval_rule_is_named_in_the_decision()
    {
        var observer = new RecordingActionObserver();
        await using var host = Create(observer, o =>
        {
            o.HitlMode = MentorHitlMode.Native;
            o.AutoApprovalRules = [ctx => ValueTask.FromResult(ctx.FunctionCallContent.Name == "issue_refund")];
        });
        host.Chat!.CallTool("issue_refund", new { orderId = 3 }).Say("Refunded.");

        (await host.RunTurnAsync("refund order 3")).Should().BeNull();
        await FlushAsync(host);

        observer.Steps("issue_refund").Should().Equal("Allowed/auto-approved", "Executed/completed");
    }

    private static void EveryConfirmationEndsOnce(IReadOnlyList<MentorActionDecision> seen)
    {
        var groups = seen.Where(d => d.ConfirmationId is not null).GroupBy(d => d.ConfirmationId).ToList();
        groups.Should().NotBeEmpty();
        foreach (var group in groups)
        {
            var steps = group.Select(d => d.Outcome).ToList();
            var ends = steps is [MentorActionOutcome.ConfirmationRequested, MentorActionOutcome.Rejected]
                    or [MentorActionOutcome.ConfirmationRequested, MentorActionOutcome.Stopped]
                    or [MentorActionOutcome.ConfirmationRequested, MentorActionOutcome.Denied]
                    or [MentorActionOutcome.ConfirmationRequested, MentorActionOutcome.Confirmed,
                        MentorActionOutcome.Executed or MentorActionOutcome.Failed
                        or MentorActionOutcome.Stopped or MentorActionOutcome.Denied];
            ends.Should().BeTrue("confirmation {0} went {1}", group.Key, string.Join(" → ", steps));
        }
    }

    // ── Nested agents ────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_specialists_call_names_the_specialist()
    {
        var observer = new RecordingActionObserver();
        await using var host = Create(observer);
        host.Chat!.Respond(Delegating(host, "where is order 7", "shipping", "get_tracking_status",
            new() { ["orderId"] = 7 }));

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        (await host.RunTurnAsync("where is order 7?", deadline.Token)).Should().BeNull();
        await FlushAsync(host);

        observer.Steps("get_tracking_status").Should().Equal("Allowed/allowed", "Executed/completed");
        observer.Seen.Where(d => d.ToolName == "get_tracking_status").Should().AllSatisfy(d =>
        {
            d.AgentKind.Should().Be(MentorAgentKind.Specialist);
            d.AgentName.Should().Be("ShippingAgent");
        });
    }

    [Fact]
    public async Task A_declarative_agents_call_names_the_agent()
    {
        var observer = new RecordingActionObserver();
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false, s =>
        {
            s.AddMentorActionObserver(observer);
            s.AddMentorAgentDeclarative(d => d.Definitions.Add("""
                kind: GptComponentMetadata
                name: OrdersDesk
                description: Counts and lists the orders.
                instructions: You count orders.
                tools:
                  - kind: function
                    name: count_open_orders
                """));
        });
        host.Chat!.Respond(Delegating(host, "count the open orders", "orders", "count_open_orders", []));

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        (await host.RunTurnAsync("how many open orders?", deadline.Token)).Should().BeNull(host.Logs.WarningText);
        await FlushAsync(host);

        observer.Steps("count_open_orders").Should().Equal(["Allowed/allowed", "Executed/completed"], host.Logs.WarningText);
        observer.Seen.Where(d => d.ToolName == "count_open_orders").Should().AllSatisfy(d =>
        {
            d.AgentKind.Should().Be(MentorAgentKind.AgentSource);
            d.AgentName.Should().Be("OrdersDesk", "the agents of a source share one tool list: the running agent names itself");
        });
    }

    [Fact]
    public async Task A_team_members_call_names_the_member()
    {
        // The team's own workflow is driven by its manager; the member's tools carry the gate with them, and the
        // member's name with it.
        var observer = new RecordingActionObserver();
        await using var host = Create(observer);
        host.Auth.SignIn("ada", "Manager");
        var gate = host.Get<Novior.MentorAgent.Middleware.MentorAgentUIMiddleware>();
        var tool = (AIFunction)Novior.MentorAgent.Middleware.GatedAIFunction.Wrap(
            AIFunctionFactory.Create(host.Get<TestAnalysisTools>().ReadQuarterRevenue, name: "read_quarter_revenue"),
            gate, MentorAgentKind.TeamMember, "Analyst");

        await tool.InvokeAsync(new AIFunctionArguments());
        await FlushAsync(host);

        observer.Steps("read_quarter_revenue").Should().Equal("Allowed/allowed", "Executed/completed");
        observer.Seen.Should().AllSatisfy(d =>
        {
            d.AgentKind.Should().Be(MentorAgentKind.TeamMember);
            d.AgentName.Should().Be("Analyst");
        });
    }

    /// <summary>
    /// The model of a delegation: the coordinator routes, the router hands off to the specialist whose handoff
    /// tool names <paramref name="handoffTo"/>, which calls <paramref name="tool"/> once and answers.
    /// </summary>
    internal static Func<IReadOnlyList<ChatMessage>, ChatOptions?, ChatResponse> Delegating(
        MentorHostFixture host, string request, string handoffTo, string tool, Dictionary<string, object?> args)
        => (messages, options) =>
        {
            var tools = options?.Tools?.Select(t => t.Name).ToList() ?? [];
            var answered = messages.LastOrDefault()?.Contents.OfType<FunctionResultContent>().Any() == true;

            if (tools.Count == 0) return Text("SAFE");
            if (tools.Contains("route_to_specialist"))
                return answered ? Text("relayed") : Invoke("route_to_specialist", new() { ["request"] = request });
            if (tools.All(t => t.StartsWith("handoff_to", StringComparison.Ordinal)))
            {
                // handoff_to_1, handoff_to_2…: the target is in the description.
                var target = options!.Tools!.FirstOrDefault(t =>
                    (t.Description ?? "").Contains(handoffTo, StringComparison.OrdinalIgnoreCase)) ?? options.Tools![0];
                return Invoke(target.Name, []);
            }
            if (tools.Contains(tool))
                return answered ? Text("done") : Invoke(tool, args);
            return Text("SAFE");

            ChatResponse Text(string text) => new(new ChatMessage(ChatRole.Assistant, text)) { ModelId = host.Chat!.ModelId };
            ChatResponse Invoke(string name, Dictionary<string, object?> a) =>
                new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(Guid.NewGuid().ToString("N"), name, a)]))
                {
                    ModelId = host.Chat!.ModelId,
                };
        };

    // ── Spans ────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(MentorHitlMode.Blocking, true, "executed", "confirmed")]
    [InlineData(MentorHitlMode.Native, true, "executed", "confirmed")]
    [InlineData(MentorHitlMode.Blocking, false, "rejected", "rejected")]
    [InlineData(MentorHitlMode.Native, false, "rejected", "rejected")]
    public async Task The_tool_span_carries_the_outcome_and_the_confirmation(
        MentorHitlMode mode, bool confirm, string outcome, string confirmation)
    {
        var source = "MentorAgent.Tests.Decisions." + Guid.NewGuid().ToString("N");
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == source,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => { lock (spans) spans.Add(a); },
        };
        ActivitySource.AddActivityListener(listener);

        var observer = new RecordingActionObserver();
        await using var host = Create(observer, o =>
        {
            o.HitlMode = mode;
            o.EnableObservability = true;
            o.ObservabilitySourceName = source;
        });
        host.Auth.SignIn("ada", "Manager");
        host.Chat!.CallTool("cancel_order", new { orderId = 7, reason = "test" }).Say("done");
        var state = host.Get<IMentorStateService>();
        state.OnConfirmationRequired += req =>
        {
            if (confirm) _ = state.ConfirmAsync(req.ActionId);
            else state.Cancel(req.ActionId);
        };

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        (await host.RunTurnAsync("cancel order 7", deadline.Token)).Should().BeNull();

        List<Activity> tools;
        lock (spans) tools = [.. spans.Where(s => s.OperationName == "mentor.tool.cancel_order")];
        var span = tools.Should().ContainSingle("mode {0}: one span per call", mode).Subject;
        span.GetTagItem("mentoragent.tool.outcome").Should().Be(outcome);
        span.GetTagItem("mentoragent.tool.confirmation").Should().Be(confirmation);
        span.GetTagItem("mentoragent.tool.reason").Should().Be(confirm ? "completed" : "operator");
        span.GetTagItem("mentoragent.surface").Should().Be("in-process");
    }

    [Fact]
    public async Task The_tool_span_names_the_agent()
    {
        var source = "MentorAgent.Tests.Decisions." + Guid.NewGuid().ToString("N");
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == source,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => { lock (spans) spans.Add(a); },
        };
        ActivitySource.AddActivityListener(listener);
        var inner = new FakeChatClient();
        await using var host = MentorHostFixture.Create(o =>
        {
            o.ChatClient = null;
            o.Agent = inner.AsAIAgent(instructions: "You are a test agent.", name: "HostAgent");
            o.MemoryAutoCapture = false;
            o.EnableObservability = true;
            o.ObservabilitySourceName = source;
        }, withChatClient: false);
        inner.CallTool("count_open_orders").Say("Three.");

        (await host.RunTurnAsync("how many open orders?")).Should().BeNull();

        Activity span;
        lock (spans) span = spans.Single(s => s.OperationName == "mentor.tool.count_open_orders");
        span.GetTagItem("mentoragent.agent").Should().Be("HostAgent");
        span.GetTagItem("mentoragent.tool.outcome").Should().Be("executed");
    }

    // ── The log ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_coordinators_call_is_logged_once()
    {
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false);
        host.Chat!.CallTool("count_open_orders").Say("Three.");

        (await host.RunTurnAsync("how many open orders?")).Should().BeNull();

        host.Logs.Records.Count(r => r.Message == "[MentorAgent] Tool call: count_open_orders")
            .Should().Be(1, "the orchestrator and the gate both wrote it");
    }
}
