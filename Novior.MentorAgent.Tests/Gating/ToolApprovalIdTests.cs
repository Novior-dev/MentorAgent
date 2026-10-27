using FluentAssertions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.App;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Novior.MentorAgent.Tests.Gating;

/// <summary>
/// The Agent Framework's <c>ToolApprovalAgent</c>, which <see cref="MentorOptions.AutoApprovalRules"/> puts in the
/// Native pipeline, refuses from 1.26.0 an approval request whose id is already active (agent-framework #9542):
/// up to 1.25 the later request replaced the earlier one, now the run throws. The way MentorAgent can bring one is a
/// request a stopped or failed turn left open, which it answers at the start of the next turn (BUG-116): that request
/// must still be closed by its answer, and the next turn must work. A call a rule approves runs once, with no banner.
/// </summary>
public sealed class ToolApprovalIdTests
{
    private static MentorHostFixture Host(Func<string, bool>? approve = null)
        => MentorHostFixture.Create(o =>
        {
            o.HitlMode = MentorHitlMode.Native;
            o.MemoryAutoCapture = false;
            o.AutoApprovalRules = [ctx => ValueTask.FromResult(approve?.Invoke(ctx.FunctionCallContent.Name) == true)];
        });

    private static void ConfirmEveryBanner(MentorHostFixture host)
    {
        var state = host.Get<IMentorStateService>();
        state.OnConfirmationRequired += req => _ = state.ConfirmAsync(req.ActionId);
    }

    private static void StopAtTheFirstBanner(MentorHostFixture host)
    {
        var first = true;
        host.Get<IMentorStateService>().OnConfirmationRequired += _ =>
        {
            if (!first) return;
            first = false;
            host.Orchestrator.CancelCurrentRequest();
        };
    }

    private static int Refunds(MentorHostFixture host)
        => host.Get<TestOrderService>().Invoked.Count(i => i == nameof(TestOrderService.IssueRefundAsync));

    private static void TheNextTurnWorks(MentorHostFixture host, Exception? error, string question)
    {
        error.Should().BeNull("the turn after must work; exceptions: {0}", host.Logs.ExceptionText);
        host.Chat!.Calls[^1].Messages.Should().Contain(m => m.Text.Contains(question),
            "the turn after must reach the model; exceptions: {0}", host.Logs.ExceptionText);
    }

    [Fact]
    public async Task The_middleware_is_in_the_pipeline_of_these_tests()
    {
        await using var host = Host();
        host.Chat!.Say("ciao");

        await host.RunTurnAsync("ciao");

        host.Logs.Records.Should().Contain(r => r.Message.Contains("Tool approval middleware"));
    }

    [Fact]
    public async Task A_request_a_Stop_left_open_is_answered_on_the_next_turn_with_the_middleware()
    {
        await using var host = Host();
        host.Chat!.CallTool("issue_refund", new { orderId = 1001 }).Say("second answer");
        StopAtTheFirstBanner(host);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await host.RunTurnAsync("refund order 1001", deadline.Token);
        var error = await host.RunTurnAsync("how many open orders?", deadline.Token);

        TheNextTurnWorks(host, error, "how many open orders?");
        string.Concat(host.Chat.Calls[^1].Messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>()
                .Select(r => r.Result?.ToString()))
            .Should().Contain("not_run", "the request left open is closed by its answer");
        Refunds(host).Should().Be(0);
    }

    [Fact]
    public async Task A_request_a_failed_turn_left_open_is_answered_on_the_turn_after_with_the_middleware()
    {
        await using var host = Host();
        host.Chat!.CallTool("issue_refund", new { orderId = 1001 }).Say("third answer");
        StopAtTheFirstBanner(host);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await host.RunTurnAsync("refund order 1001", deadline.Token);
        host.Chat.ThrowOnNextCall = new InvalidOperationException("HTTP 429 (rate_limit_exceeded)");
        await host.RunTurnAsync("how many open orders?", deadline.Token);
        var error = await host.RunTurnAsync("and the delivered ones?", deadline.Token);

        TheNextTurnWorks(host, error, "and the delivered ones?");
        Refunds(host).Should().Be(0);
    }

    [Fact]
    public async Task A_call_a_rule_approves_runs_once_without_a_banner_and_the_next_turn_works()
    {
        await using var host = Host(approve: name => name == "issue_refund");
        host.Chat!.CallTool("issue_refund", new { orderId = 1001 }).Say("refunded").Say("two open orders");
        var banners = 0;
        host.Get<IMentorStateService>().OnConfirmationRequired += _ => banners++;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await host.RunTurnAsync("refund order 1001", deadline.Token);
        var error = await host.RunTurnAsync("how many open orders?", deadline.Token);

        banners.Should().Be(0, "the rule approved the call");
        Refunds(host).Should().Be(1);
        TheNextTurnWorks(host, error, "how many open orders?");
        host.Logs.Records.Should().NotContain(r => r.Level >= LogLevel.Error,
            "nothing failed; exceptions: {0}", host.Logs.ExceptionText);
    }
}
