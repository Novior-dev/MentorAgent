using System.Diagnostics;
using FluentAssertions;
using Novior.MentorAgent.Tests.Harness;
using Xunit;

namespace Novior.MentorAgent.Tests.Federation;

/// <summary>
/// What a confirmation-required tool does when there is nobody to confirm it.
///
/// <para>
/// Every headless entry point — the A2A handler, the SSE endpoint, a scheduled job — drives the
/// same <c>MentorOrchestrator.SendMessageAsync</c> the widget does. The widget has a user watching,
/// so a <c>RequiresConfirmation</c> tool raises a banner and the turn waits for a click. The others
/// have nobody: the banner is raised into a scope with no subscriber, and the turn waits for a click
/// that can never happen.
/// </para>
///
/// <para>
/// The question is not whether that is inconvenient — it is whether the request eventually ends. A
/// caller's cancellation has to reach the parked approval, or a remote peer holds a connection open
/// until its own timeout and MentorAgent holds a DI scope open with it.
/// </para>
/// </summary>
public sealed class HeadlessApprovalTests
{
    [Fact]
    public async Task An_unanswered_confirmation_ends_when_the_caller_gives_up()
    {
        // The property that makes a headless deployment safe to run at all. Without the
        // cancellation registration inside the approval store this never returns, and every A2A
        // peer that trips a gated tool leaks a scope and a connection until something else times
        // out.
        await using var host = MentorHostFixture.Create();
        host.Chat!.CallTool("issue_refund", new { orderId = 7 }).Say("done");

        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var clock = Stopwatch.StartNew();

        await host.RunTurnAsync("refund order 7", caller.Token);

        clock.Stop();
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20),
            "the caller's cancellation must reach the parked approval");
    }

    [Fact]
    public async Task The_gated_tool_does_not_run_when_nobody_approved_it()
    {
        // Fail closed. Timing out is a refusal, not a licence — an irreversible action must not
        // proceed because the request that asked for it went away.
        await using var host = MentorHostFixture.Create();
        host.Chat!.CallTool("issue_refund", new { orderId = 7 }).Say("done");

        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await host.RunTurnAsync("refund order 7", caller.Token);

        host.Get<Novior.MentorAgent.Tests.App.TestOrderService>().Invoked
            .Should().NotContain(nameof(Novior.MentorAgent.Tests.App.TestOrderService.IssueRefundAsync));
    }

    [Fact]
    public async Task An_ungated_tool_is_unaffected_by_any_of_this()
    {
        // The control. If a headless turn could not run an ordinary action either, the problem
        // would be somewhere else entirely.
        await using var host = MentorHostFixture.Create();
        host.Chat!.CallTool("count_open_orders").Say("there are two");

        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var error = await host.RunTurnAsync("how many open orders", caller.Token);

        error.Should().BeNull();
        host.Get<Novior.MentorAgent.Tests.App.TestOrderService>().Invoked
            .Should().Contain(nameof(Novior.MentorAgent.Tests.App.TestOrderService.CountOpenOrders));
    }

    [Fact]
    public async Task A_subscriber_that_answers_immediately_lets_the_turn_through()
    {
        // The shape a headless host is expected to use if it wants gated tools to run at all: an
        // auto-approval policy subscribed to the confirmation event. Pinning it here records that
        // the seam exists, so "headless cannot use gated tools" is a choice rather than a dead end.
        await using var host = MentorHostFixture.Create();
        var state = host.Get<Novior.MentorAgent.Abstractions.Interfaces.IMentorStateService>();
        state.OnConfirmationRequired += req => _ = state.ConfirmAsync(req.ActionId);

        host.Chat!.CallTool("issue_refund", new { orderId = 7 }).Say("done");

        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await host.RunTurnAsync("refund order 7", caller.Token);

        host.Get<Novior.MentorAgent.Tests.App.TestOrderService>().Invoked
            .Should().Contain(nameof(Novior.MentorAgent.Tests.App.TestOrderService.IssueRefundAsync));
    }
}
