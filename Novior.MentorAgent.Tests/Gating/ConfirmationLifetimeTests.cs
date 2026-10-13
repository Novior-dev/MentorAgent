using FluentAssertions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.App;
using Novior.MentorAgent.Tests.Harness;
using Xunit;

namespace Novior.MentorAgent.Tests.Gating;

/// <summary>
/// How long a confirmation lives <b>in process</b> — the Blazor Server path, where the banner and the turn
/// share a scope (BUG-128, BUG-129).
///
/// <para>
/// A confirmation is answered by a turn that is waiting for it, or by nothing. Up to rc.14 it outlived its
/// turn twice over: Stop cancelled the wait but left the request pending, so a late Confirm raised
/// <c>OnApprovalResponse(true)</c> for an action that never ran; and the end of the scope — a closed circuit
/// — did not cancel the turn at all, which went on waiting for a page that was gone.
/// </para>
/// </summary>
public sealed class ConfirmationLifetimeTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    /// <summary>Starts a turn that parks on the refund's confirmation, and returns it with the action id.</summary>
    private static async Task<(Task<Exception?> Turn, Guid ActionId)> ParkAsync(MentorHostFixture host)
    {
        var raised = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Get<IMentorStateService>().OnConfirmationRequired += req => raised.TrySetResult(req.ActionId);

        var turn = Task.Run(() => host.RunTurnAsync("refund order 1001"));
        (await Task.WhenAny(raised.Task, Task.Delay(Patience))).Should().BeSameAs(raised.Task,
            "the scripted refund has to raise a confirmation");
        return (turn, await raised.Task);
    }

    private static List<(Guid ActionId, bool Approved)> RecordAnswers(MentorHostFixture host)
    {
        var answers = new List<(Guid, bool)>();
        host.Get<IMentorStateService>().OnApprovalResponse += (req, approved) =>
        {
            lock (answers) answers.Add((req.ActionId, approved));
        };
        return answers;
    }

    [Theory]
    [InlineData(MentorHitlMode.Blocking)]
    [InlineData(MentorHitlMode.Native)]
    public async Task A_confirm_after_Stop_is_no_approval_and_runs_nothing(MentorHitlMode mode)
    {
        await using var host = MentorHostFixture.Create(o => o.HitlMode = mode);
        host.Chat!.CallTool("issue_refund", new { orderId = 1001 }).Say("done");
        var service = host.Get<TestOrderService>();
        var answers = RecordAnswers(host);

        var (turn, actionId) = await ParkAsync(host);
        host.Orchestrator.CancelCurrentRequest();
        (await Task.WhenAny(turn, Task.Delay(Patience))).Should().BeSameAs(turn, "Stop ends the turn");

        await host.Get<IMentorStateService>().ConfirmAsync(actionId);

        answers.Should().BeEmpty("the click came after Stop: no turn received it, so no approval was given");
        host.Get<MentorApprovalStore>().IsPending(actionId).Should().BeFalse();
        service.Invoked.Should().NotContain(nameof(TestOrderService.IssueRefundAsync));
    }

    [Theory]
    [InlineData(MentorHitlMode.Blocking)]
    [InlineData(MentorHitlMode.Native)]
    public async Task The_end_of_the_scope_cancels_a_turn_parked_on_a_confirmation(MentorHitlMode mode)
    {
        // A closed circuit disposes its scope, and with it the orchestrator. The parked turn must end there:
        // nobody can answer its banner any more.
        await using var host = MentorHostFixture.Create(o => o.HitlMode = mode);
        host.Chat!.CallTool("issue_refund", new { orderId = 1001 }).Say("done");
        var service = host.Get<TestOrderService>();
        var state = host.Get<IMentorStateService>();
        var store = host.Get<MentorApprovalStore>();
        var answers = RecordAnswers(host);

        var (turn, actionId) = await ParkAsync(host);
        await ((IAsyncDisposable)host.Scope).DisposeAsync();

        (await Task.WhenAny(turn, Task.Delay(Patience))).Should().BeSameAs(turn,
            "the turn's scope has ended, so has its wait for an answer");
        store.IsPending(actionId).Should().BeFalse();

        await state.ConfirmAsync(actionId);

        answers.Should().BeEmpty("the answer reached nothing");
        service.Invoked.Should().NotContain(nameof(TestOrderService.IssueRefundAsync));
    }

    [Theory]
    [InlineData(MentorHitlMode.Blocking)]
    [InlineData(MentorHitlMode.Native)]
    public async Task An_answer_in_time_is_reported_once(MentorHitlMode mode)
    {
        // The control: without it, "never raise OnApprovalResponse" satisfies both tests above.
        await using var host = MentorHostFixture.Create(o => o.HitlMode = mode);
        host.Chat!.CallTool("issue_refund", new { orderId = 1001 }).Say("done");
        var service = host.Get<TestOrderService>();
        var answers = RecordAnswers(host);

        var (turn, actionId) = await ParkAsync(host);
        var state = host.Get<IMentorStateService>();
        await state.ConfirmAsync(actionId);
        (await Task.WhenAny(turn, Task.Delay(Patience))).Should().BeSameAs(turn);
        await state.ConfirmAsync(actionId);   // a double click

        answers.Should().ContainSingle().Which.Should().Be((actionId, true));
        service.Invoked.Should().Contain(nameof(TestOrderService.IssueRefundAsync));
    }
}
