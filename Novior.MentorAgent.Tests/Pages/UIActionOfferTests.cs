using FluentAssertions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.Discovery;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Novior.MentorAgent.Tests.Pages;

/// <summary>
/// Story 1501, slice b (<c>page.offered</c>): the action policies decide whether a page's UI action is offered, as they
/// do for the application's actions. One they do not offer is left out of every request of the turn, a page a call of
/// the run opens included; a call to it anyway reaches the gate, which refuses it.
/// </summary>
[Trait("Rule", "page.offered")]
public sealed class UIActionOfferTests
{
    private static MentorHostFixture Host(RecordingActionObserver observer, TestActionPolicy policy,
        Action<MentorOptions>? configure = null)
        => MentorHostFixture.Create(o =>
        {
            o.MemoryAutoCapture = false;
            configure?.Invoke(o);
        }, s =>
        {
            s.AddScoped<FluentActionTests.StockActions>();
            s.AddMentorActionObserver(observer);
            s.AddMentorActionPolicy(policy);
        });

    private static Task FlushAsync(MentorHostFixture host) => host.Get<MentorActionDecisions>().FlushAsync();

    [Fact]
    public async Task A_UI_action_the_policies_do_not_offer_is_left_out_of_the_request()
    {
        var policy = new TestActionPolicy
        {
            Allowed = r => !(r.Kind == MentorActionKind.UI && r.PageName == "Orders" && r.ToolName == "highlight_order"),
        };
        await using var host = Host(new RecordingActionObserver(), policy, o =>
            // An action of the application with the name of a UI action, off: nothing of it is in the UI action's offer.
            o.Actions.Add<FluentActionTests.StockActions>(nameof(FluentActionTests.StockActions.Restock),
                a => a.Describe("Restocks a product").RequireRoles("Manager").RequireConfirmation().Off()));
        host.Get<IMentorPageContext>().SetPageName("Orders")
            .RegisterUIAction("highlight_order", "Highlights an order", _ => { })
            .RegisterUIAction("restock", "Marks the shelf to restock", _ => { });
        host.Chat!.Say("Here are your orders.");

        (await host.RunTurnAsync("show me my orders")).Should().BeNull();

        host.Chat!.Calls[0].ToolNames.Should().NotContain("highlight_order", "the policy does not offer it on this page")
            .And.Contain("restock", "the policy offers the page's other action");
        var asked = policy.Seen.Where(s => s.Method == nameof(IMentorActionPolicy.IsAllowedAsync)
                                           && s.Request.Stage == MentorActionStage.Offer
                                           && s.Request.Kind == MentorActionKind.UI).Select(s => s.Request).ToList();
        asked.Select(r => r.ToolName).Should().BeEquivalentTo(["highlight_order", "restock"]);
        asked.Should().OnlyContain(r => r.PageName == "Orders" && r.RequiredRoles.Count == 0 && !r.RequiresConfirmation,
            "a UI action has no roles and no confirmation of its own, whatever action of the application has its name");
    }

    [Fact]
    public async Task A_call_to_a_UI_action_left_out_is_refused_by_the_gate_and_decided_as_one()
    {
        var observer = new RecordingActionObserver();
        var policy = new TestActionPolicy { Allowed = r => r.Kind != MentorActionKind.UI };
        await using var host = Host(observer, policy);
        var invoked = 0;
        host.Get<IMentorPageContext>().SetPageName("Orders").RegisterUIAction("highlight_order", "Highlights an order", _ => invoked++);
        host.Chat!.CallTool("highlight_order", new { parameter = 42 }).Say("I cannot do that.");

        (await host.RunTurnAsync("highlight order 42")).Should().BeNull();
        await FlushAsync(host);

        invoked.Should().Be(0);
        host.Chat!.Calls[0].ToolNames.Should().NotContain("highlight_order");
        observer.Seen.Where(d => d.ToolName == "highlight_order").Should().ContainSingle()
            .Which.Should().Match<MentorActionDecision>(d => d.Outcome == MentorActionOutcome.Denied
                && d.ReasonCode == MentorActionReasons.NotOffered && d.Kind == MentorActionKind.UI && d.PageName == "Orders",
                "BUG-233: refused as not offered in the turn, without asking the policies about the call");
    }

    [Fact]
    public async Task The_actions_of_a_page_a_call_opens_are_offered_only_when_the_policies_offer_them()
    {
        // A whole page: the console turns off every UI action of Admin, those it declares after the turn started too.
        var policy = new TestActionPolicy { Allowed = r => !(r.Kind == MentorActionKind.UI && r.PageName == "Admin") };
        await using var host = Host(new RecordingActionObserver(), policy);
        var page = host.Get<IMentorPageContext>();
        var pages = host.Get<MentorDiscoveryService>().Discover().Pages;
        var orders = pages.FindIndex(p => p.Url == "/orders");
        pages[orders] = pages[orders] with { ReadyTimeout = 30_000 };

        page.SetPageName("Home").RegisterUIAction("show_tips", "Shows the tips", _ => { });
        host.Get<IMentorStateService>().OnNavigationRequested += url => _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            page.SetPageName("Admin").RegisterUIAction("purge_cache", "Purges the cache", _ => { });
            page.SignalReady();
            page.Clear("Home");
        });
        host.Chat!.CallTool("navigate_to", new { url = "/orders" }).Say("Here is the page.");

        (await host.RunTurnAsync("open the orders page")).Should().BeNull();

        host.Chat!.Calls[0].ToolNames.Should().Contain("show_tips", "the policies offer the actions of Home");
        host.Chat!.Calls[1].ToolNames.Should().NotContain("purge_cache", "the page the call opened is asked about as it opens")
            .And.NotContain("show_tips").And.Contain("navigate_to");
    }
}
