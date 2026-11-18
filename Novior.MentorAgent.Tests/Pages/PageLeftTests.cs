using FluentAssertions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.AI;
using Xunit;

namespace Novior.MentorAgent.Tests.Pages;

/// <summary>
/// BUG-234 — after a navigation the next request of the turn still offered the UI actions of the page being left:
/// Blazor disposes it, which clears its actions, only after the next page has initialized, and <c>navigate_to</c> does
/// not wait at all for a page without UI actions. A call to one ran its handler on a page that was going away. Now
/// <c>navigate_to</c>, and an action that opens another page, take the actions of the page being left back before they
/// ask for the navigation (<c>page.left-not-offered</c>).
///
/// <para>
/// And the policies are asked about the actions of the page now shown even when they have the names of the page left
/// (<c>page.offered</c>): the run's tools were refreshed only when the names changed, so an action a policy does not
/// offer on the new page was offered there, and ran.
/// </para>
/// </summary>
public sealed class PageLeftTests
{
    private static MentorHostFixture Host(TestActionPolicy? policy = null, RecordingActionObserver? observer = null)
        => MentorHostFixture.Create(o => o.MemoryAutoCapture = false, s =>
        {
            if (observer is not null) s.AddMentorActionObserver(observer);
            if (policy is not null) s.AddMentorActionPolicy(policy);
        });

    private static ChatResponse Navigations(MentorHostFixture host, params string[] urls)
        => new(new ChatMessage(ChatRole.Assistant,
            [.. urls.Select(u => (AIContent)new FunctionCallContent(Guid.NewGuid().ToString("N"), "navigate_to",
                new Dictionary<string, object?> { ["url"] = u }))]))
        {
            ModelId = host.Chat!.ModelId,
        };

    /// <summary>The orders page is shown, with one UI action.</summary>
    private static IMentorPageContext OnOrders(MentorHostFixture host, Action<object?>? handler = null)
    {
        host.Navigation.StartAt("/orders");
        return host.Get<IMentorPageContext>().SetPageName("Orders")
            .RegisterUIAction("highlight_order", "Highlights an order", handler ?? (_ => { }));
    }

    /// <summary>
    /// As Blazor does it, on the next navigation: the page at <c>/orders</c> initializes and registers
    /// <c>highlight_order</c>, then signals; the page left is disposed only when <paramref name="disposeLeft"/> says.
    /// </summary>
    private static void OrdersOpensOnNavigation(MentorHostFixture host, Action<object?> handler, string? disposeLeft,
        bool atOnce = false)
    {
        var page = host.Get<IMentorPageContext>();
        var pages = host.Get<MentorDiscoveryService>().Discover().Pages;
        var orders = pages.FindIndex(p => p.Url == "/orders");
        pages[orders] = pages[orders] with { ReadyTimeout = 30_000 };

        void Open()
        {
            page.SetPageName("Orders").RegisterUIAction("highlight_order", "Highlights an order", handler);
            page.SignalReady();
            if (disposeLeft is not null) page.Clear(disposeLeft);
        }

        // At once: Blazor's renderer runs a navigation inline when nothing holds it, so the new page can register
        // before NotifyNavigationRequested returns.
        if (atOnce)
            host.Get<IMentorStateService>().OnNavigationRequested += _ => Open();
        else
            host.Get<IMentorStateService>().OnNavigationRequested += _ => Task.Run(async () =>
            {
                await Task.Delay(100);
                Open();
            });
    }

    // ── page.left-not-offered ───────────────────────────────────────────────────

    [Fact]
    [Trait("Rule", "page.left-not-offered")]
    public async Task Two_navigations_in_one_reply_leave_none_of_the_actions_of_the_page_left()
    {
        // The case seen live: navigate_to the page shown, then to a page without UI actions, in one reply.
        await using var host = Host();
        OnOrders(host);
        host.Chat!.Respond((_, _) => Navigations(host, "/orders", "/reports")).Say("Here are the reports.");

        (await host.RunTurnAsync("open the orders, then the reports")).Should().BeNull();

        host.Chat.Calls[1].ToolNames.Should().Contain("navigate_to").And.NotContain("highlight_order",
            "the page shown at the end is the reports, which offer no UI action");
    }

    [Fact]
    [Trait("Rule", "page.left-not-offered")]
    public async Task A_navigation_to_a_page_without_UI_actions_leaves_none_of_the_actions_of_the_page_left()
    {
        await using var host = Host();
        OnOrders(host);
        host.Chat!.CallTool("navigate_to", new { url = "/reports" }).Say("Here are the reports.");

        (await host.RunTurnAsync("open the reports")).Should().BeNull();

        host.Chat.Calls[1].ToolNames.Should().NotContain("highlight_order");
    }

    [Fact]
    [Trait("Rule", "page.left-not-offered")]
    public async Task An_action_of_the_page_left_called_all_the_same_does_not_run()
    {
        var invoked = 0;
        await using var host = Host();
        OnOrders(host, _ => invoked++);
        host.Chat!.CallTool("navigate_to", new { url = "/reports" })
            .CallTool("highlight_order", new { parameter = 7 })
            .Say("Done.");

        (await host.RunTurnAsync("open the reports and highlight order 7")).Should().BeNull();

        invoked.Should().Be(0, "the page that registered it is going away");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Rule", "page.left-not-offered")]
    public async Task A_navigation_to_a_page_with_UI_actions_offers_its_own_and_none_of_the_page_left(bool atOnce)
    {
        await using var host = Host();
        host.Get<IMentorPageContext>().SetPageName("Home").RegisterUIAction("show_tips", "Shows the tips", _ => { });
        OrdersOpensOnNavigation(host, _ => { }, disposeLeft: null, atOnce);   // Home is not disposed yet
        host.Chat!.CallTool("navigate_to", new { url = "/orders" }).Say("Here are the orders.");

        (await host.RunTurnAsync("open the orders")).Should().BeNull();

        host.Chat.Calls[1].ToolNames.Should().Contain("highlight_order", "registered at once: {0}", atOnce)
            .And.NotContain("show_tips");
    }

    [Fact]
    [Trait("Rule", "page.left-not-offered")]
    public async Task The_page_already_shown_keeps_its_actions()
    {
        // Blazor does not initialize it again, so nothing would register them again.
        await using var host = Host();
        OnOrders(host);
        host.Chat!.CallTool("navigate_to", new { url = "/orders" }).Say("Here are the orders.");

        (await host.RunTurnAsync("open the orders")).Should().BeNull();

        host.Chat.Calls[1].ToolNames.Should().Contain("highlight_order");
    }

    [Fact]
    [Trait("Rule", "page.left-not-offered")]
    public async Task An_action_that_opens_another_page_leaves_none_of_the_actions_of_the_page_left()
    {
        // mark_order_reviewed opens /orders when it has run.
        await using var host = Host();
        host.Navigation.StartAt("/reports");
        host.Get<IMentorPageContext>().SetPageName("Reports").RegisterUIAction("show_chart", "Shows the chart", _ => { });
        var state = host.Get<IMentorStateService>();
        state.OnConfirmationRequired += req => _ = state.ConfirmAsync(req.ActionId);
        var opened = new List<string>();
        state.OnNavigationRequested += opened.Add;
        host.Chat!.CallTool("mark_order_reviewed", new { orderId = 7 }).Say("Reviewed.");

        (await host.RunTurnAsync("mark order 7 as reviewed")).Should().BeNull();

        opened.Should().Equal(["/orders"], "the action opened the orders. Warnings: {0}", host.Logs.WarningText);
        host.Chat.Calls[1].ToolNames.Should().NotContain("show_chart");
    }

    // ── page.offered: the page now shown is asked about, whatever the names ─────

    private static TestActionPolicy OffersOn(string page)
        => new()
        {
            // Decides at the offer alone, as the README's example did before 1.3.1: every call is allowed.
            Allowed = r => r.Stage != MentorActionStage.Offer
                           || !(r.Kind == MentorActionKind.UI && r.ToolName == "highlight_order") || r.PageName == page,
        };

    [Fact]
    [Trait("Rule", "page.offered")]
    public async Task An_action_with_the_name_of_one_of_the_page_left_is_asked_about_for_the_page_now_shown()
    {
        var policy = OffersOn("Home");
        await using var host = Host(policy);
        host.Get<IMentorPageContext>().SetPageName("Home").RegisterUIAction("highlight_order", "Highlights an order", _ => { });
        OrdersOpensOnNavigation(host, _ => { }, disposeLeft: "Home");
        host.Chat!.CallTool("navigate_to", new { url = "/orders" }).Say("Here are the orders.");

        (await host.RunTurnAsync("open the orders")).Should().BeNull();

        policy.Seen.Should().Contain(s => s.Request.Stage == MentorActionStage.Offer && s.Request.ToolName == "highlight_order"
                                          && s.Request.PageName == "Orders", "the page now shown is asked about");
        host.Chat.Calls[1].ToolNames.Should().NotContain("highlight_order", "the policy does not offer it on the orders page");
    }

    [Fact]
    [Trait("Rule", "page.offered")]
    public async Task A_call_to_an_action_offered_on_the_page_left_and_not_on_the_page_now_shown_does_not_run()
    {
        var observer = new RecordingActionObserver();
        var policy = OffersOn("Home");
        await using var host = Host(policy, observer);
        var onHome = 0;
        var onOrders = 0;
        host.Get<IMentorPageContext>().SetPageName("Home").RegisterUIAction("highlight_order", "Highlights an order", _ => onHome++);
        OrdersOpensOnNavigation(host, _ => onOrders++, disposeLeft: "Home");
        host.Chat!.CallTool("navigate_to", new { url = "/orders" })
            .CallTool("highlight_order", new { parameter = 7 })
            .Say("Done.");

        (await host.RunTurnAsync("highlight order 7 on the orders page")).Should().BeNull();
        await host.Get<MentorActionDecisions>().FlushAsync();

        (onHome, onOrders).Should().Be((0, 0));
        observer.Steps("highlight_order").Should().Equal(["Denied/not-offered"],
            "offered on Home earlier in the turn does not make it offered on the orders page");
        policy.Asked("highlight_order").Should().BeEmpty("the call is refused without asking the policy about it");
    }

    [Fact]
    [Trait("Rule", "page.offered")]
    public async Task The_actions_of_a_page_that_stays_are_asked_about_once_a_turn()
    {
        // Calls that open no page do not make the page's actions new: the policies are not asked again after each one.
        var policy = OffersOn("Orders");
        await using var host = Host(policy);
        OnOrders(host);
        host.Chat!.CallTool("count_open_orders").CallTool("count_open_orders").Say("Two counts.");

        (await host.RunTurnAsync("count the open orders twice")).Should().BeNull();

        policy.Seen.Count(s => s.Request.Stage == MentorActionStage.Offer && s.Request.ToolName == "highlight_order")
            .Should().Be(1);
        host.Chat.Calls[2].ToolNames.Should().Contain("highlight_order");
    }

    [Fact]
    [Trait("Rule", "page.offered")]
    public async Task An_action_the_policies_do_not_offer_on_the_page_left_stays_out_for_the_rest_of_the_turn()
    {
        // The safe way round: offered on the orders page, not on Home, where the turn started.
        var observer = new RecordingActionObserver();
        var policy = OffersOn("Orders");
        await using var host = Host(policy, observer);
        var onOrders = 0;
        host.Get<IMentorPageContext>().SetPageName("Home").RegisterUIAction("highlight_order", "Highlights an order", _ => { });
        OrdersOpensOnNavigation(host, _ => onOrders++, disposeLeft: "Home");
        host.Chat!.CallTool("navigate_to", new { url = "/orders" })
            .CallTool("highlight_order", new { parameter = 7 })
            .Say("Done.");

        (await host.RunTurnAsync("highlight order 7 on the orders page")).Should().BeNull();
        await host.Get<MentorActionDecisions>().FlushAsync();

        host.Chat.Calls[1].ToolNames.Should().NotContain("highlight_order");
        onOrders.Should().Be(0);
        observer.Steps("highlight_order").Should().Equal(["Denied/not-offered"]);
    }
}
