using FluentAssertions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Attributes;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.App;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Novior.MentorAgent.Tests.Gating;

/// <summary>
/// Story 177 — the action knows who confirmed it and when. The approval event came when the action's continuation
/// was already queued, and the action itself had no way to know: a ledger of the writes could not say who approved
/// one. <see cref="MentorActionConfirmation.Current"/> carries it while the action runs, in both modes.
/// </summary>
public sealed class ConfirmationContextTests
{
    /// <summary>
    /// A payments service that writes down what its actions read as their confirmation: the README's example, with a
    /// ledger that keeps what it is given.
    /// </summary>
    private sealed class Payments
    {
        public List<(string Tool, MentorActionConfirmation? Confirmation)> Seen { get; } = [];
        public List<(int PaymentId, string? Who, DateTimeOffset? When)> Ledger { get; } = [];

        [MentorAction(Description = "Refunds a payment", RequiresConfirmation = true)]
        public async Task<string> RefundPaymentAsync(int paymentId)
        {
            var confirmation = MentorActionConfirmation.Current;   // null when nobody confirmed
            Seen.Add(("refund_payment", confirmation));
            await WriteAsync(paymentId, confirmation?.ConfirmedBy?.Identity?.Name, confirmation?.ConfirmedAt);
            return $"Payment {paymentId} refunded.";
        }

        private Task WriteAsync(int paymentId, string? who, DateTimeOffset? when)
        {
            Ledger.Add((paymentId, who, when));
            return Task.CompletedTask;
        }

        [MentorAction(Description = "Reads a payment", ReadOnly = true)]
        public string ReadPayment(int paymentId)
        {
            Seen.Add(("read_payment", MentorActionConfirmation.Current));
            return $"payment {paymentId}";
        }
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly DateTimeOffset Noon = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private static MentorHostFixture Host(Action<MentorOptions>? configure = null) => MentorHostFixture.Create(o =>
    {
        o.MemoryAutoCapture = false;
        o.ScanAssemblies = [typeof(TestOrderService).Assembly, typeof(Payments).Assembly];
        configure?.Invoke(o);
    }, services =>
    {
        services.AddScoped<Payments>();
        services.AddSingleton<TimeProvider>(new TestClock(Noon));
    });

    private static ChatResponse Calls(MentorHostFixture host, params (string Tool, int Id)[] calls)
        => new(new ChatMessage(ChatRole.Assistant,
            [.. calls.Select(c => (AIContent)new FunctionCallContent($"call-{c.Tool}", c.Tool,
                new Dictionary<string, object?> { ["paymentId"] = c.Id }))]))
        {
            ModelId = host.Chat!.ModelId,
        };

    [Theory]
    [InlineData(MentorHitlMode.Blocking)]
    [InlineData(MentorHitlMode.Native)]
    public async Task A_confirmed_action_reads_who_confirmed_it_and_when(MentorHitlMode mode)
    {
        await using var host = Host(o => o.HitlMode = mode);
        host.Auth.SignIn("ada");
        host.Chat!.Respond((_, _) => Calls(host, ("refund_payment", 7))).Say("Refunded.");
        Guid? asked = null;
        var state = host.Get<IMentorStateService>();
        state.OnConfirmationRequired += req =>
        {
            asked = req.ActionId;
            _ = state.ConfirmAsync(req.ActionId);
        };

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        (await host.RunTurnAsync("refund payment 7", deadline.Token)).Should().BeNull();

        var (tool, confirmation) = host.Get<Payments>().Seen.Should().ContainSingle("mode {0}", mode).Subject;
        tool.Should().Be("refund_payment");
        confirmation.Should().NotBeNull("mode {0}: the action was confirmed", mode);
        confirmation!.ConfirmationId.Should().Be(asked!.Value, "the id the banner carried");
        confirmation.ToolName.Should().Be("refund_payment");
        confirmation.ConfirmedAt.Should().Be(Noon, "when the user answered");
        confirmation.ConfirmedBy!.Identity!.Name.Should().Be("ada");
        host.Get<Payments>().Ledger.Should().Equal([(7, "ada", Noon)], "the ledger names who approved the refund, and when");
        MentorActionConfirmation.Current.Should().BeNull("it lasts as long as the action");
    }

    [Theory]
    [InlineData(MentorHitlMode.Blocking)]
    [InlineData(MentorHitlMode.Native)]
    public async Task An_action_nobody_confirmed_finds_no_confirmation_even_after_a_confirmed_one(MentorHitlMode mode)
    {
        await using var host = Host(o => o.HitlMode = mode);
        host.Chat!.Respond((_, _) => Calls(host, ("refund_payment", 7)))
            .Respond((_, _) => Calls(host, ("read_payment", 7)))
            .Say("Done.");
        var state = host.Get<IMentorStateService>();
        state.OnConfirmationRequired += req => _ = state.ConfirmAsync(req.ActionId);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        (await host.RunTurnAsync("refund payment 7, then show it", deadline.Token)).Should().BeNull();

        var seen = host.Get<Payments>().Seen;
        seen.Select(s => s.Tool).Should().Equal("refund_payment", "read_payment");
        seen[0].Confirmation.Should().NotBeNull();
        seen[1].Confirmation.Should().BeNull("mode {0}: nobody confirmed the read, and it must not see the refund's", mode);
    }

    [Fact]
    public async Task An_action_an_auto_approval_rule_let_through_finds_no_confirmation()
    {
        await using var host = Host(o =>
        {
            o.HitlMode = MentorHitlMode.Native;
            o.AutoApprovalRules = [ctx => ValueTask.FromResult(ctx.FunctionCallContent.Name == "refund_payment")];
        });
        host.Chat!.Respond((_, _) => Calls(host, ("refund_payment", 7))).Say("Refunded.");

        (await host.RunTurnAsync("refund payment 7")).Should().BeNull();

        host.Get<Payments>().Seen.Should().ContainSingle().Which.Confirmation.Should().BeNull("a rule is not a person");
    }

    [Theory]
    [InlineData(MentorHitlMode.Blocking)]
    [InlineData(MentorHitlMode.Native)]
    public async Task A_confirmation_nobody_answered_in_time_is_nobodys_to_read(MentorHitlMode mode)
    {
        // Story 176: the refund's confirmation expires, so the refund never runs; the read that follows finds no
        // confirmation, neither the expired one nor any other.
        await using var host = Host(o =>
        {
            o.HitlMode = mode;
            o.ConfirmationTimeout = TimeSpan.FromMilliseconds(50);
        });
        host.Chat!.Respond((_, _) => Calls(host, ("refund_payment", 7)))
            .Respond((_, _) => Calls(host, ("read_payment", 7)))
            .Say("The refund was not confirmed in time.");

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        (await host.RunTurnAsync("refund payment 7, then show it", deadline.Token)).Should().BeNull();

        host.Get<Payments>().Seen.Should().Equal([("read_payment", null)], "mode {0}: the refund did not run", mode);
        host.Get<Payments>().Ledger.Should().BeEmpty();
    }
}
