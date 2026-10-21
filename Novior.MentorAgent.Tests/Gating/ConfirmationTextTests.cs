using FluentAssertions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.App;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Novior.MentorAgent.Tests.Gating;

/// <summary>
/// BUG-186 — what the confirmation banner says about the arguments, and who writes it.
///
/// <para>
/// The arguments are the model's. With <c>RequireActionForChangeRequests</c> the banner is the last look a person
/// gets at what an action is about to do, and it showed each value whole, control characters included, in the
/// order of the model's JSON; the text could not be changed. The fixture's <c>cancel_order(orderId, reason)</c>
/// needs the Manager role and a confirmation.
/// </para>
/// </summary>
public sealed class ConfirmationTextTests
{
    private const string Default = "Cancels an existing order — Order Id: 1001 · Reason: late";

    /// <summary>Runs one turn calling cancel_order with <paramref name="args"/>, declines the banner, returns its text.</summary>
    private static async Task<string> BannerAsync(MentorHostFixture host, object args)
    {
        host.Auth.SignIn("dana", "Manager");
        host.Chat!.CallTool("cancel_order", args).Say("done");
        string? message = null;
        var state = host.Get<IMentorStateService>();
        state.OnConfirmationRequired += req =>
        {
            message = req.Message;
            state.Cancel(req.ActionId);
        };

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await host.RunTurnAsync("cancel order 1001", deadline.Token);
        return message ?? throw new InvalidOperationException("no banner was raised");
    }

    private static MentorHostFixture Host(MentorHitlMode mode = MentorHitlMode.Blocking, Action<MentorOptions>? configure = null)
        => MentorHostFixture.Create(o =>
        {
            o.HitlMode = mode;
            o.MemoryAutoCapture = false;
            configure?.Invoke(o);
        });

    private static string Warnings(MentorHostFixture host)
        => string.Join("\n", host.Logs.Records.Where(r => r.Level >= LogLevel.Warning).Select(r => r.Message));

    // ── The default text ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(MentorHitlMode.Blocking)]
    [InlineData(MentorHitlMode.Native)]
    public async Task The_arguments_follow_the_order_of_the_methods_parameters(MentorHitlMode mode)
    {
        await using var host = Host(mode);

        // The model wrote the reason first.
        (await BannerAsync(host, new { reason = "late", orderId = 1001 })).Should().Be(Default);
    }

    [Fact]
    public async Task A_long_value_is_cut_and_control_characters_become_spaces()
    {
        await using var host = Host();
        var reason = "first\nsecond" + (char)0x202E + "third" + (char)7 + "  " + new string('x', 300);

        var banner = await BannerAsync(host, new { orderId = 1001, reason });

        banner.Should().Be("Cancels an existing order — Order Id: 1001 · Reason: first second third "
                           + new string('x', 181) + "…");
    }

    [Fact]
    public void A_cut_never_splits_a_surrogate_pair()
    {
        var value = new string('x', MentorConfirmationBuilder.ArgumentValueLimit - 1) + "\U0001F600" + "y";

        MentorConfirmationBuilder.ForBanner(value)
            .Should().Be(new string('x', MentorConfirmationBuilder.ArgumentValueLimit - 1) + "…");
    }

    // ── The host's text ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(MentorHitlMode.Blocking)]
    [InlineData(MentorHitlMode.Native)]
    public async Task The_host_writes_the_text_from_the_arguments_the_language_and_its_own_services(MentorHitlMode mode)
    {
        MentorConfirmationContext? seen = null;
        await using var host = Host(mode, o =>
        {
            o.Language = MentorLanguage.Italian;
            o.ConfirmationMessage = c =>
            {
                seen = c;
                return $"Annullare l'ordine {c.Arguments[0].Value}? Motivo: {c.Arguments[1].Value}.";
            };
        });

        (await BannerAsync(host, new { reason = "late", orderId = 1001 }))
            .Should().Be("Annullare l'ordine 1001? Motivo: late.");

        seen!.ToolName.Should().Be("cancel_order");
        seen.Description.Should().Be("Cancels an existing order");
        seen.Arguments.Select(a => a.Name).Should().Equal("orderId", "reason");
        seen.Arguments[0].Label.Should().Be("Order Id");
        seen.Language.Should().Be(MentorLanguage.Italian);
        seen.DefaultMessage.Should().Be(Default);
        seen.Services.GetService<TestOrderService>().Should().BeSameAs(host.Get<TestOrderService>(),
            "the services are the conversation's scope");
    }

    [Theory]
    [InlineData("throws", true)]
    [InlineData("blank", true)]
    [InlineData("null", false)]
    public async Task A_function_that_throws_or_writes_nothing_leaves_the_default_text(string kind, bool warned)
    {
        await using var host = Host(configure: o => o.ConfirmationMessage = _ => kind switch
        {
            "throws" => throw new InvalidOperationException("order 1001 belongs to dana@example.com"),
            "blank"  => "   ",
            _        => null,
        });

        (await BannerAsync(host, new { orderId = 1001, reason = "late" })).Should().Be(Default,
            "a banner never fails to appear because of the host's text");

        Warnings(host).Contains("ConfirmationMessage").Should().Be(warned);
        Warnings(host).Should().NotContain("dana@example.com", "the exception's message stays out of the log");
        host.Logs.ExceptionText.Should().NotContain("dana@example.com");
    }
}
