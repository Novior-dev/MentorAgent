using FluentAssertions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.App;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Novior.MentorAgent.Utility;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Novior.MentorAgent.Tests.Gating;

/// <summary>
/// Story 176 — a confirmation nobody answers waited until Stop or the end of the session, however long that took.
/// With <see cref="MentorOptions.ConfirmationTimeout"/> it counts as rejected once that time has passed since the
/// banner was shown: the action does not run, and a late answer finds nothing, as one after Stop does.
/// </summary>
public sealed class ConfirmationTimeoutTests
{
    /// <summary>The system clock, moved forward by the test.</summary>
    private sealed class Clock : TimeProvider
    {
        private long _ahead;

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ahead, by.Ticks);

        private TimeSpan Ahead => TimeSpan.FromTicks(Interlocked.Read(ref _ahead));

        public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + Ahead;

        public override long GetTimestamp()
            => System.GetTimestamp() + (long)(Ahead.TotalSeconds * TimestampFrequency);
    }

    private static MentorHostFixture Create(RecordingActionObserver observer, Action<MentorOptions> configure,
        Clock? clock = null, TestActionPolicy? policy = null)
        => MentorHostFixture.Create(o =>
        {
            o.MemoryAutoCapture = false;
            configure(o);
        }, s =>
        {
            s.AddMentorActionObserver(observer);
            if (clock is not null) s.AddSingleton<TimeProvider>(clock);
            if (policy is not null) s.AddMentorActionPolicy(policy);
        });

    private static Task FlushAsync(MentorHostFixture host) => host.Get<MentorActionDecisions>().FlushAsync();

    private static ChatResponse ToolCall(MentorHostFixture host, string callId, string tool, Dictionary<string, object?>? args = null)
        => new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(callId, tool, args)])) { ModelId = host.Chat!.ModelId };

    /// <summary>What the model was told about the calls, across the turn.</summary>
    private static string ToldTheModel(MentorHostFixture host)
        => string.Concat(host.Chat!.Calls.SelectMany(c => c.Messages).SelectMany(m => m.Contents)
            .OfType<FunctionResultContent>().Select(r => r.Result?.ToString()));

    [Theory]
    [InlineData(MentorHitlMode.Blocking)]
    [InlineData(MentorHitlMode.Native)]
    public async Task A_confirmation_nobody_answers_in_time_counts_as_rejected_and_the_action_does_not_run(MentorHitlMode mode)
    {
        var observer = new RecordingActionObserver();
        var clock = new Clock();
        await using var host = Create(observer, o =>
        {
            o.HitlMode = mode;
            o.ConfirmationTimeout = TimeSpan.FromMinutes(5);
        }, clock);
        host.Chat!.Respond((_, _) => ToolCall(host, "call-1", "issue_refund", new() { ["orderId"] = 1001 }))
            .Say("It was not confirmed in time.");
        var state = host.Get<IMentorStateService>();
        var answered = 0;
        state.OnApprovalResponse += (_, _) => answered++;
        Guid? asked = null;
        state.OnConfirmationRequired += req =>
        {
            asked = req.ActionId;
            clock.Advance(TimeSpan.FromMinutes(5));   // the user walked away
        };

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        (await host.RunTurnAsync("refund order 1001", deadline.Token)).Should().BeNull();
        await FlushAsync(host);

        host.Get<TestOrderService>().Invoked.Should().BeEmpty("mode {0}", mode);
        observer.Steps("issue_refund").Should().Equal(
            ["Allowed/allowed", "ConfirmationRequested/operator", "Rejected/confirmation-timeout"], "mode {0}", mode);
        observer.Seen.Single(d => d.Outcome == MentorActionOutcome.Rejected).Timestamp
            .Should().BeOnOrAfter(clock.GetUtcNow().AddSeconds(-30), "when the deadline passed");
        ToldTheModel(host).Should().Contain("confirmation_timed_out", "mode {0}", mode)
            .And.NotContain(MentorUtils.DeclinedMarker, "nobody chose not to run it");
        host.Logs.Records.Should().ContainSingle(r => r.Level == LogLevel.Information
            && r.Message.Contains("Nobody answered the confirmation for 'issue_refund' within 300 s"), "mode {0}", mode);

        // The answer that comes after the deadline finds nothing, as one after Stop does.
        await state.ConfirmAsync(asked!.Value);
        answered.Should().Be(0, "a deadline is not an answer, and a late answer is not acted on");
        host.Get<MentorApprovalStore>().IsPending(asked.Value).Should().BeFalse();
        host.Get<TestOrderService>().Invoked.Should().BeEmpty();
    }

    [Fact]
    public async Task The_deadline_ends_a_wait_already_under_way()
    {
        // No clock moved: the timer itself ends the wait, with a small timeout.
        var observer = new RecordingActionObserver();
        await using var host = Create(observer, o =>
        {
            o.HitlMode = MentorHitlMode.Blocking;
            o.ConfirmationTimeout = TimeSpan.FromMilliseconds(100);
        });
        host.Chat!.Respond((_, _) => ToolCall(host, "call-1", "issue_refund", new() { ["orderId"] = 1001 }))
            .Say("Not confirmed in time.");

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        (await host.RunTurnAsync("refund order 1001", deadline.Token)).Should().BeNull();
        await FlushAsync(host);

        observer.Steps("issue_refund").Should().EndWith("Rejected/confirmation-timeout");
        host.Get<TestOrderService>().Invoked.Should().BeEmpty();
    }

    [Theory]
    [InlineData(MentorHitlMode.Blocking)]
    [InlineData(MentorHitlMode.Native)]
    public async Task An_answer_before_the_deadline_counts(MentorHitlMode mode)
    {
        var observer = new RecordingActionObserver();
        var clock = new Clock();
        await using var host = Create(observer, o =>
        {
            o.HitlMode = mode;
            o.ConfirmationTimeout = TimeSpan.FromMinutes(5);
        }, clock);
        host.Chat!.Respond((_, _) => ToolCall(host, "call-1", "issue_refund", new() { ["orderId"] = 1001 })).Say("Refunded.");
        var state = host.Get<IMentorStateService>();
        state.OnConfirmationRequired += req =>
        {
            clock.Advance(TimeSpan.FromMinutes(4));
            _ = state.ConfirmAsync(req.ActionId);
        };

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        (await host.RunTurnAsync("refund order 1001", deadline.Token)).Should().BeNull();
        await FlushAsync(host);

        observer.Steps("issue_refund").Should().Equal(
            ["Allowed/allowed", "ConfirmationRequested/operator", "Confirmed/operator", "Executed/completed"], "mode {0}", mode);
        host.Get<TestOrderService>().Invoked.Should().Equal(nameof(TestOrderService.IssueRefundAsync));
    }

    [Fact]
    public async Task A_team_members_confirmation_times_out_the_same_way()
    {
        // A specialist's or a team member's tool carries the gate, and its deadline with it.
        var observer = new RecordingActionObserver();
        var clock = new Clock();
        await using var host = Create(observer, o => o.ConfirmationTimeout = TimeSpan.FromMinutes(5), clock);
        host.Auth.SignIn("ada", "Manager");
        var gate = host.Get<Novior.MentorAgent.Middleware.MentorAgentUIMiddleware>();
        var tool = (AIFunction)Novior.MentorAgent.Middleware.GatedAIFunction.Wrap(
            AIFunctionFactory.Create(host.Get<TestAnalysisTools>().ApproveBudgetLine, name: "approve_budget_line"),
            gate, MentorAgentKind.TeamMember, "Analyst");
        host.Get<IMentorStateService>().OnConfirmationRequired += _ => clock.Advance(TimeSpan.FromMinutes(5));

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await tool.InvokeAsync(new AIFunctionArguments { ["lineId"] = 4 }, deadline.Token);
        await FlushAsync(host);

        result?.ToString().Should().Contain("confirmation_timed_out");
        observer.Steps("approve_budget_line").Should().Equal(
            "Allowed/allowed", "ConfirmationRequested/operator", "Rejected/confirmation-timeout");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Zero_or_a_negative_value_is_no_limit(int seconds)
    {
        var observer = new RecordingActionObserver();
        var clock = new Clock();
        await using var host = Create(observer, o => o.ConfirmationTimeout = TimeSpan.FromSeconds(seconds), clock);
        host.Chat!.Respond((_, _) => ToolCall(host, "call-1", "issue_refund", new() { ["orderId"] = 1001 })).Say("Refunded.");
        var state = host.Get<IMentorStateService>();
        state.OnConfirmationRequired += req =>
        {
            // A day later, and answered once the turn is already waiting: a limit would have ended the wait first.
            clock.Advance(TimeSpan.FromDays(1));
            _ = Task.Run(async () =>
            {
                await Task.Delay(200);
                await state.ConfirmAsync(req.ActionId);
            });
        };

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        (await host.RunTurnAsync("refund order 1001", deadline.Token)).Should().BeNull();
        await FlushAsync(host);

        observer.Steps("issue_refund").Should().Equal(
            "Allowed/allowed", "ConfirmationRequested/operator", "Confirmed/operator", "Executed/completed");
    }

    [Theory]
    [InlineData(MentorHitlMode.Blocking)]
    [InlineData(MentorHitlMode.Native)]
    public async Task A_confirmation_a_policy_asks_for_times_out_the_same_way(MentorHitlMode mode)
    {
        // The same banner, so the same deadline (story 311).
        var observer = new RecordingActionObserver();
        var clock = new Clock();
        var policy = new TestActionPolicy
        {
            Verdict = r => r.ToolName == "count_open_orders" ? MentorActionVerdict.Confirm("after hours") : MentorActionVerdict.Proceed,
        };
        await using var host = Create(observer, o =>
        {
            o.HitlMode = mode;
            o.ConfirmationTimeout = TimeSpan.FromMinutes(5);
        }, clock, policy);
        host.Chat!.Respond((_, _) => ToolCall(host, "call-1", "count_open_orders")).Say("Not confirmed in time.");
        host.Get<IMentorStateService>().OnConfirmationRequired += _ => clock.Advance(TimeSpan.FromMinutes(6));

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        (await host.RunTurnAsync("how many open orders?", deadline.Token)).Should().BeNull();
        await FlushAsync(host);

        observer.Steps("count_open_orders").Should().Equal(
            ["Allowed/allowed", "ConfirmationRequested/policy", "Rejected/confirmation-timeout"], "mode {0}", mode);
        host.Get<TestOrderService>().Invoked.Should().BeEmpty();
    }

    [Fact]
    [Trait("RuntimeOption", nameof(MentorOptions.ConfirmationTimeout))]
    public async Task A_deadline_set_while_the_application_runs_applies_from_the_next_turn()
    {
        // Story 1275: the wait reads the deadline when it starts. The first banner has none and is declined; the second,
        // after the change, ends unanswered at the new deadline.
        var observer = new RecordingActionObserver();
        await using var host = Create(observer, o => o.HitlMode = MentorHitlMode.Blocking);
        var state = host.Get<IMentorStateService>();
        var decline = true;
        state.OnConfirmationRequired += req =>
        {
            if (decline) state.Cancel(req.ActionId);
        };
        host.Chat!.Respond((_, _) => ToolCall(host, "call-1", "issue_refund", new() { ["orderId"] = 1001 })).Say("Declined.")
            .Respond((_, _) => ToolCall(host, "call-2", "issue_refund", new() { ["orderId"] = 1001 })).Say("Not confirmed in time.");

        using (var first = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            (await host.RunTurnAsync("refund order 1001", first.Token)).Should().BeNull();
        host.Options.ConfirmationTimeout = TimeSpan.FromMilliseconds(100);
        decline = false;
        using (var second = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            (await host.RunTurnAsync("refund order 1001", second.Token)).Should().BeNull("the new deadline ends the wait");
        await FlushAsync(host);

        observer.Steps("issue_refund").Should().EndWith("Rejected/confirmation-timeout");
        host.Get<TestOrderService>().Invoked.Should().BeEmpty();
    }
}
