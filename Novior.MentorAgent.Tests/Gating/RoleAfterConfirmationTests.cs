using FluentAssertions;
using Novior.MentorAgent.Abstractions.Core;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Middleware;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.App;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.AI;
using Xunit;

namespace Novior.MentorAgent.Tests.Gating;

/// <summary>
/// A role taken away while the confirmation banner waits (BUG-143).
///
/// <para>
/// The roles of a <c>RequiredRoles</c> action are read before the banner, and the banner waits as long as the
/// user takes to answer. In Blocking mode the Confirm then ran the action straight away, so a user whose role
/// was withdrawn in the meantime — signed out elsewhere, demoted by an administrator — still ran it. Native
/// mode did not: the approved call comes back through the gate, which reads the roles again. The two modes
/// are documented as indistinguishable from the outside, so both are asserted here.
/// </para>
/// </summary>
public sealed class RoleAfterConfirmationTests
{
    private static string RoleRequired(MentorHostFixture host)
        => string.Format(new MentorLocalizer(host.Options.Language).Get("auth.role.required"), "Manager");

    private static string ToolResults(MentorHostFixture host)
        => string.Join(" ", host.Chat!.Calls
            .SelectMany(c => c.Messages)
            .SelectMany(m => m.Contents)
            .OfType<FunctionResultContent>()
            .Select(c => c.Result?.ToString() ?? ""));

    /// <summary>Signs in as a Manager, and answers the banner after replacing the user with <paramref name="whileWaiting"/>.</summary>
    private static async Task<(Exception? Error, int Raised)> RunCancelOrderAsync(
        MentorHostFixture host, Action whileWaiting)
    {
        host.Auth.SignIn("ada", "Manager");
        host.Chat!.CallTool("cancel_order", new { orderId = 1001, reason = "test" }).Say("done");

        var raised = 0;
        var state = host.Get<IMentorStateService>();
        state.OnConfirmationRequired += req =>
        {
            Interlocked.Increment(ref raised);
            whileWaiting();
            _ = state.ConfirmAsync(req.ActionId);
        };

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var error = await host.RunTurnAsync("cancel order 1001", deadline.Token);
        return (error, raised);
    }

    [Theory]
    [InlineData(MentorHitlMode.Blocking)]
    [InlineData(MentorHitlMode.Native)]
    public async Task A_role_taken_away_while_the_banner_waits_stops_the_action(MentorHitlMode mode)
    {
        await using var host = MentorHostFixture.Create(o => o.HitlMode = mode);

        var (error, raised) = await RunCancelOrderAsync(host, whileWaiting: () => host.Auth.SignIn("ada"));

        error.Should().BeNull();
        raised.Should().Be(1, "the user had the role when the action was proposed, so the banner was shown");
        string.Join(", ", host.Get<TestOrderService>().Invoked)
            .Should().NotContain(nameof(TestOrderService.CancelOrderAsync),
                "mode {0}: the user confirmed after losing the Manager role", mode);
        ToolResults(host).Should().Contain(RoleRequired(host),
            "the model is told the refusal is about the role, so it does not say the action ran");
    }

    [Theory]
    [InlineData(MentorHitlMode.Blocking)]
    [InlineData(MentorHitlMode.Native)]
    public async Task A_role_kept_while_the_banner_waits_lets_the_action_run(MentorHitlMode mode)
    {
        // The control. The principal is replaced by an equal one while the banner waits, so the second read
        // really happens on a new object: reading the roles again must not refuse a user who still has them.
        await using var host = MentorHostFixture.Create(o => o.HitlMode = mode);

        var (error, raised) = await RunCancelOrderAsync(host, whileWaiting: () => host.Auth.SignIn("ada", "Manager"));

        error.Should().BeNull();
        raised.Should().Be(1);
        string.Join(", ", host.Get<TestOrderService>().Invoked)
            .Should().Contain(nameof(TestOrderService.CancelOrderAsync));
        host.Logs.WarningText.Should().NotContain("'cancel_order' blocked");
    }

    [Fact]
    public async Task In_a_nested_agent_a_role_taken_away_while_the_banner_waits_stops_the_action()
    {
        // Specialists, teams and declarative agents reach the gate through GatedAIFunction, which always takes
        // the blocking flow (their approval request could never surface), even on a host that runs Native.
        await using var host = MentorHostFixture.Create(o => o.HitlMode = MentorHitlMode.Native);
        host.Auth.SignIn("ada", "Manager");
        var state = host.Get<IMentorStateService>();
        state.OnConfirmationRequired += req =>
        {
            host.Auth.SignIn("ada");
            _ = state.ConfirmAsync(req.ActionId);
        };
        var ran = false;

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await host.Get<MentorAgentUIMiddleware>().InvokeGatedAsync(
            "cancel_order",
            new Dictionary<string, object?> { ["orderId"] = 1001, ["reason"] = "test" },
            _ => { ran = true; return ValueTask.FromResult<object?>("cancelled"); },
            deadline.Token,
            alwaysBlockForApproval: true);

        ran.Should().BeFalse("the user confirmed after losing the Manager role");
        result?.ToString().Should().Contain(RoleRequired(host));
    }
}
