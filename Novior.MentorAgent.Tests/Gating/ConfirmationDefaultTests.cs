using FluentAssertions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.App;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Novior.MentorAgent.Tests.Gating;

/// <summary>
/// Stories 152 and 198 — from 1.0.0-rc.15 a <c>[MentorAction]</c> that declares neither <c>ReadOnly</c> nor
/// <c>RequiresConfirmation</c> asks for confirmation (<see cref="MentorOptions.RequireConfirmationForWrites"/>),
/// and the writes that still run unasked are listed at startup.
///
/// <para>
/// Up to rc.14 an action asked only when it said so. An author who forgot — or never knew — got a write that
/// ran on the model's word, and since BUG-106 the zero-match fallback could hand such an action to the model on
/// a message that matched nothing, a greeting included. The default now errs the other way: a write that
/// should run unasked has to be declared so.
/// </para>
///
/// <para>
/// The fixture's <c>mark_order_reviewed</c> declares nothing; <c>show_order_card</c>, <c>export_ledger</c> and
/// <c>failing_action</c> declare <c>RequiresConfirmation = false</c>; <c>count_open_orders</c> is
/// <c>ReadOnly</c>; <c>issue_refund</c> declares <c>RequiresConfirmation = true</c>; <c>get_active_orders</c> has
/// <c>[Description]</c> alone.
/// </para>
/// </summary>
public sealed class ConfirmationDefaultTests
{
    /// <summary>Runs one turn calling <paramref name="tool"/>, approving any banner; returns the banners raised.</summary>
    private static async Task<List<string>> BannersFor(MentorHostFixture host, string tool, object? args = null)
    {
        host.Chat!.CallTool(tool, args).Say("done");
        var raised = new List<string>();
        var state = host.Get<IMentorStateService>();
        state.OnConfirmationRequired += req =>
        {
            raised.Add(req.ToolName);
            _ = state.ConfirmAsync(req.ActionId);
        };

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await host.RunTurnAsync("go", deadline.Token);
        return raised;
    }

    private static string Warnings(MentorHostFixture host)
        => string.Join("\n", host.Logs.Records.Where(r => r.Level >= LogLevel.Warning).Select(r => r.Message));

    // ── The banner ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(MentorHitlMode.Blocking)]
    [InlineData(MentorHitlMode.Native)]
    public async Task An_action_that_declares_nothing_asks_and_runs_once_approved(MentorHitlMode mode)
    {
        await using var host = MentorHostFixture.Create(o => { o.HitlMode = mode; o.MemoryAutoCapture = false; });

        var banners = await BannersFor(host, "mark_order_reviewed", new { orderId = 7 });

        banners.Should().Equal(["mark_order_reviewed"], "mode {0}: a write that says nothing is a write that asks", mode);
        host.Get<TestOrderService>().Invoked.Should().Contain("MarkOrderReviewed(7)");
    }

    [Theory]
    [InlineData(MentorHitlMode.Blocking)]
    [InlineData(MentorHitlMode.Native)]
    public async Task An_action_that_opts_out_runs_without_a_banner(MentorHitlMode mode)
    {
        await using var host = MentorHostFixture.Create(o => { o.HitlMode = mode; o.MemoryAutoCapture = false; });

        var banners = await BannersFor(host, "show_order_card", new { orderId = 7 });

        banners.Should().BeEmpty("RequiresConfirmation = false is a decision, and it is kept");
        host.Get<TestOrderService>().Invoked.Should().Contain(nameof(TestOrderService.ShowOrderCard));
    }

    [Fact]
    public async Task A_read_only_action_never_asks()
    {
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false);

        (await BannersFor(host, "count_open_orders")).Should().BeEmpty();
    }

    [Fact]
    public async Task An_action_the_host_declares_read_only_by_name_does_not_ask()
    {
        await using var host = MentorHostFixture.Create(o =>
        {
            o.MemoryAutoCapture = false;
            o.IsReadOnlyAction = name => name == "mark_order_reviewed";
        });

        (await BannersFor(host, "mark_order_reviewed", new { orderId = 7 })).Should().BeEmpty();
    }

    [Fact]
    public async Task With_the_default_off_an_undeclared_action_runs_unasked_as_in_rc14()
    {
        await using var host = MentorHostFixture.Create(o =>
        {
            o.MemoryAutoCapture = false;
            o.RequireConfirmationForWrites = false;
        });

        (await BannersFor(host, "mark_order_reviewed", new { orderId = 7 })).Should().BeEmpty();
        host.Get<TestOrderService>().Invoked.Should().Contain("MarkOrderReviewed(7)");
    }

    [Fact]
    public async Task A_declared_confirmation_still_wins_over_a_read_only_claim()
    {
        // rc.14: "a confirmation-gated action is never read-only". issue_refund declares the confirmation; a
        // predicate calling it read-only must not take the banner away.
        await using var host = MentorHostFixture.Create(o =>
        {
            o.MemoryAutoCapture = false;
            o.IsReadOnlyAction = name => name == "issue_refund";
        });

        host.Get<MentorConfirmationBuilder>().IsDeclaredReadOnly("issue_refund").Should().BeFalse();
        (await BannersFor(host, "issue_refund", new { orderId = 1001 })).Should().Equal(["issue_refund"]);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Native_auto_approval_treats_a_defaulted_action_like_a_declared_one(bool approves, bool expectBanner)
    {
        await using var host = MentorHostFixture.Create(o =>
        {
            o.HitlMode = MentorHitlMode.Native;
            o.MemoryAutoCapture = false;
            o.AutoApprovalRules = [_ => ValueTask.FromResult(approves)];
        });

        var banners = await BannersFor(host, "mark_order_reviewed", new { orderId = 7 });

        banners.Count.Should().Be(expectBanner ? 1 : 0,
            "a rule that approves waives the prompt for any gated call, and one that does not leaves it");
        host.Get<TestOrderService>().Invoked.Should().Contain("MarkOrderReviewed(7)");
    }

    // ── What the default withholds from other surfaces ─────────────────────────

    [Fact]
    public async Task A_defaulted_action_is_withheld_from_mcp_and_an_opted_out_one_is_published()
    {
        await using var app = await ServerAppFactory.CreateAsync(
            configure: o => o.McpServerEnabled = true,
            mapEndpoints: e => e.MapMentorAgentMcp());

        var names = await (await McpTestSession.OpenAsync(app.Client)).ListToolNamesAsync();

        names.Should().NotContain("mark_order_reviewed", "nobody is on the far end of an MCP call to confirm (BUG-035)");
        names.Should().Contain("show_order_card");
        names.Should().Contain("get_active_orders", "[Description] alone is not covered by the default: still published");
    }

    [Fact]
    public async Task With_the_default_off_mcp_publishes_the_undeclared_action_again()
    {
        await using var app = await ServerAppFactory.CreateAsync(
            configure: o => { o.McpServerEnabled = true; o.RequireConfirmationForWrites = false; },
            mapEndpoints: e => e.MapMentorAgentMcp());

        var names = await (await McpTestSession.OpenAsync(app.Client)).ListToolNamesAsync();

        names.Should().Contain("mark_order_reviewed");
    }

    [Fact]
    public async Task A_defaulted_action_is_not_a_skill_on_the_a2a_card()
    {
        await using var app = await ServerAppFactory.CreateAsync(
            configure: o => { o.A2AServerEnabled = true; o.AppName = "Acme"; },
            configureApp: a => a.UseEndpoints(e => e.MapMentorAgentA2A()));

        var card = await app.Client.GetStringAsync("/.well-known/agent-card.json");

        card.Should().NotContain("\"mark_order_reviewed\"", "an A2A task has nobody to confirm");
        card.Should().Contain("\"show_order_card\"");
    }

    // ── The startup warning (198) ───────────────────────────────────────────────

    [Fact]
    public async Task The_warning_lists_the_opted_out_actions_as_the_hosts_choice()
    {
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false);

        host.Get<MentorDiscoveryService>().Discover();

        var line = host.Logs.Records.Single(r => r.Level == LogLevel.Warning && r.Message.Contains("[MentorAction] method(s)")).Message;
        line.Should().Contain("export_ledger").And.Contain("failing_action").And.Contain("show_order_card")
            .And.Contain("RequiresConfirmation = false");
        line.Should().NotContain("mark_order_reviewed", "it asks").And.NotContain("count_open_orders", "it only reads")
            .And.NotContain("get_active_orders", "a [Description] method belongs to the other list");
    }

    [Fact]
    public async Task The_warning_lists_the_description_only_methods_as_not_covered()
    {
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false);

        host.Get<MentorDiscoveryService>().Discover();

        var line = host.Logs.Records.Single(r => r.Level == LogLevel.Warning && r.Message.Contains("[Description] alone")).Message;
        line.Should().Contain("not covered by RequireConfirmationForWrites").And.Contain("get_active_orders");
        line.Should().NotContain("show_order_card", "a [MentorAction] belongs to the other list");
    }

    [Fact]
    public async Task A_description_only_method_declared_read_only_is_not_listed()
    {
        await using var host = MentorHostFixture.Create(o =>
        {
            o.MemoryAutoCapture = false;
            o.IsReadOnlyAction = name => name == "get_active_orders";
        });

        host.Get<MentorDiscoveryService>().Discover();

        Warnings(host).Should().NotContain("get_active_orders");
    }

    [Fact]
    public async Task Nothing_is_listed_when_every_write_asks()
    {
        // The control: the warning is about writes that run unasked, not about every action there is.
        await using var host = MentorHostFixture.Create(o =>
        {
            o.MemoryAutoCapture = false;
            o.IsReadOnlyAction = _ => true;   // whatever is not declared confirmation-gated only reads
        });

        host.Get<MentorDiscoveryService>().Discover();

        Warnings(host).Should().NotContain("without confirmation");
    }
}
