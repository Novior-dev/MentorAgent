using FluentAssertions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.App;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Novior.MentorAgent.Tests.Gating;

/// <summary>
/// BUG-204 — a Stop that came after the confirmation, before the action started, did not stop it. Nothing looked at
/// the turn's token between the answer and the action, so an action that does not read the token itself ran anyway,
/// in both modes. The gate reads the token right before the action runs; in Native mode the orchestrator also reads
/// it before the answers are sent.
/// </summary>
public sealed class StopAfterConfirmationTests
{
    private static MentorHostFixture Create(RecordingActionObserver observer, Action<MentorOptions>? configure = null,
        Action<IServiceCollection>? services = null)
        => MentorHostFixture.Create(o =>
        {
            o.MemoryAutoCapture = false;
            configure?.Invoke(o);
        }, s =>
        {
            s.AddMentorActionObserver(observer);
            services?.Invoke(s);
        });

    private static Task FlushAsync(MentorHostFixture host) => host.Get<MentorActionDecisions>().FlushAsync();

    private static ChatResponse ToolCall(MentorHostFixture host, string callId, string tool, Dictionary<string, object?>? args = null)
        => new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(callId, tool, args)])) { ModelId = host.Chat!.ModelId };

    /// <summary>Confirms every banner, then presses Stop, both before the turn reads the answer.</summary>
    private static void ConfirmThenStop(MentorHostFixture host)
    {
        var state = host.Get<IMentorStateService>();
        state.OnConfirmationRequired += req =>
        {
            _ = state.ConfirmAsync(req.ActionId);
            host.Orchestrator.CancelCurrentRequest();
        };
    }

    [Theory]
    [InlineData(MentorHitlMode.Blocking)]
    [InlineData(MentorHitlMode.Native)]
    public async Task A_Stop_right_after_the_confirmation_wins_and_the_action_does_not_run(MentorHitlMode mode)
    {
        var observer = new RecordingActionObserver();
        await using var host = Create(observer, o => o.HitlMode = mode);
        host.Auth.SignIn("ada", "Manager");
        host.Chat!.Respond((_, _) => ToolCall(host, "call-7", "cancel_order", new() { ["orderId"] = 7, ["reason"] = "test" }))
            .Say("done");
        ConfirmThenStop(host);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await host.RunTurnAsync("cancel order 7", deadline.Token);
        await FlushAsync(host);

        host.Get<TestOrderService>().Invoked.Should().BeEmpty("mode {0}: the user stopped it before it started", mode);
        observer.Steps("cancel_order").Should().Equal(
            ["Allowed/allowed", "ConfirmationRequested/operator", "Confirmed/operator", "Stopped/stop"], "mode {0}", mode);
    }

    [Fact]
    public async Task A_specialists_action_stopped_right_after_its_confirmation_does_not_run()
    {
        // A specialist's action is confirmed through the blocking flow in either mode: the same gate, the same check.
        var observer = new RecordingActionObserver();
        await using var host = Create(observer, o => o.HitlMode = MentorHitlMode.Native);
        host.Auth.SignIn("ada", "Manager");
        host.Chat!.Respond(ActionDecisionTests.Delegating(host, "cancel the shipment of order 7", "shipping",
            "cancel_shipment", new() { ["orderId"] = 7 }));
        ConfirmThenStop(host);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await host.RunTurnAsync("cancel the shipment of order 7", deadline.Token);
        await FlushAsync(host);

        host.Get<TestShippingAgent>().Invoked.Should().BeEmpty(
            "the token the specialist's call carries is the workflow's, which Stop reaches a moment later: the turn's own is read");
        observer.Steps("cancel_shipment").Should().Equal(
            "Allowed/allowed", "ConfirmationRequested/operator", "Confirmed/operator", "Stopped/stop");
    }

    [Fact]
    public async Task In_Native_mode_the_answers_are_not_sent_once_Stop_came_and_the_next_turn_reads_not_run()
    {
        // Sent after the Stop, the answers went to a run that failed: the next turn read "may have run" for an action
        // that never did. Not sent, the request is still open, and it is closed as never run.
        var observer = new RecordingActionObserver();
        await using var host = Create(observer, o => o.HitlMode = MentorHitlMode.Native);
        host.Auth.SignIn("ada", "Manager");
        host.Chat!.Respond((_, _) => ToolCall(host, "call-7", "cancel_order", new() { ["orderId"] = 7, ["reason"] = "test" }))
            .Say("Two open orders.");
        ConfirmThenStop(host);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await host.RunTurnAsync("cancel order 7", deadline.Token);
        var calls = host.Chat.CallCount;
        (await host.RunTurnAsync("how many open orders?", deadline.Token)).Should().BeNull();

        host.Chat.Calls.Take(calls).Should().NotContain(c => c.Messages.Any(m => m.Contents.OfType<FunctionResultContent>().Any()),
            "nothing reached the model after the Stop");
        var answered = string.Concat(host.Chat.Calls[^1].Messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>()
            .Select(r => r.Result?.ToString()));
        answered.Should().Contain("not_run").And.NotContain("outcome_unknown");
        host.Get<TestOrderService>().Invoked.Should().BeEmpty();
    }

    [Theory]
    [InlineData(MentorHitlMode.Blocking)]
    [InlineData(MentorHitlMode.Native)]
    public async Task A_Stop_before_an_action_that_asks_for_nothing_wins_too(MentorHitlMode mode)
    {
        // The policy is the last thing asked before the action: Stop pressed while it answers, and the policy
        // answering anyway, must still stop the action.
        var observer = new RecordingActionObserver();
        MentorHostFixture? fixture = null;
        var policy = new TestActionPolicy
        {
            Before = (request, _) =>
            {
                if (request.Stage == MentorActionStage.Call) fixture!.Orchestrator.CancelCurrentRequest();
                return Task.CompletedTask;
            },
        };
        await using var host = fixture = Create(observer, o => o.HitlMode = mode, s => s.AddMentorActionPolicy(policy));
        host.Chat!.Respond((_, _) => ToolCall(host, "call-1", "count_open_orders")).Say("Two.");

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await host.RunTurnAsync("how many open orders?", deadline.Token);
        await FlushAsync(host);

        host.Get<TestOrderService>().Invoked.Should().BeEmpty("mode {0}", mode);
        observer.Steps("count_open_orders").Should().Equal(["Allowed/allowed", "Stopped/stop"], "mode {0}", mode);
    }
}
