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
/// Story 1501, slice a (<c>page.kind</c>): the requests and the decisions say whether an action is the application's or
/// a UI action the page declared, and name the page; a UI action never takes the settings of an action of the
/// application that has its name and that the session does not offer.
/// </summary>
[Trait("Rule", "page.kind")]
public sealed class UIActionKindTests
{
    private static MentorHostFixture Host(RecordingActionObserver observer, TestActionPolicy? policy,
        Action<MentorOptions> configure)
        => MentorHostFixture.Create(o =>
        {
            o.MemoryAutoCapture = false;
            configure(o);
        }, s =>
        {
            s.AddScoped<FluentActionTests.StockActions>();
            s.AddMentorActionObserver(observer);
            if (policy is not null) s.AddMentorActionPolicy(policy);
        });

    private static Task FlushAsync(MentorHostFixture host) => host.Get<MentorActionDecisions>().FlushAsync();

    [Fact]
    public async Task Requests_and_decisions_say_the_kind_of_the_action_and_name_the_page()
    {
        var observer = new RecordingActionObserver();
        var policy = new TestActionPolicy();
        await using var host = Host(observer, policy, o => o.Actions.Add<FluentActionTests.StockActions>(
            nameof(FluentActionTests.StockActions.CountLowStock), a => a.Describe("Counts the products low on stock").ReadOnly()));
        host.Get<IMentorPageContext>().SetPageName("Orders").RegisterUIAction("highlight_order", "Highlights an order", _ => { });
        host.Chat!.CallTool("count_low_stock").CallTool("highlight_order", new { parameter = 42 }).Say("Done.");

        (await host.RunTurnAsync("count the low stock and highlight order 42")).Should().BeNull();
        await FlushAsync(host);

        var ui = policy.Seen.Where(s => s.Request.ToolName == "highlight_order").Select(s => s.Request).ToList();
        ui.Should().NotBeEmpty().And.OnlyContain(r => r.Kind == MentorActionKind.UI && r.PageName == "Orders");
        var server = policy.Seen.Where(s => s.Request.ToolName == "count_low_stock").Select(s => s.Request).ToList();
        server.Should().NotBeEmpty().And.OnlyContain(r => r.Kind == MentorActionKind.Server && r.PageName == "Orders",
            "the page is the one the user is on, for an action of the application too");

        observer.Seen.Where(d => d.ToolName == "highlight_order").Should().HaveCount(2)
            .And.OnlyContain(d => d.Kind == MentorActionKind.UI && d.PageName == "Orders");
        observer.Seen.Where(d => d.ToolName == "count_low_stock").Should().HaveCount(2)
            .And.OnlyContain(d => d.Kind == MentorActionKind.Server && d.PageName == "Orders");
    }

    [Fact]
    public async Task A_decision_without_a_page_has_none()
    {
        var observer = new RecordingActionObserver();
        await using var host = Host(observer, null, o => o.Actions.Add<FluentActionTests.StockActions>(
            nameof(FluentActionTests.StockActions.CountLowStock), a => a.Describe("Counts the products low on stock").ReadOnly()));
        host.Chat!.CallTool("count_low_stock").Say("Three.");

        (await host.RunTurnAsync("how many products are low on stock?")).Should().BeNull();
        await FlushAsync(host);

        observer.Seen.Where(d => d.ToolName == "count_low_stock").Should().NotBeEmpty()
            .And.OnlyContain(d => d.Kind == MentorActionKind.Server && d.PageName == null);
    }

    [Theory]
    [InlineData(MentorHitlMode.Blocking)]
    [InlineData(MentorHitlMode.Native)]
    public async Task A_UI_action_with_the_name_of_an_action_that_is_off_is_still_the_pages(MentorHitlMode mode)
    {
        // The application's restock is off, needs a manager and asks for a confirmation: none of it is the page's.
        var observer = new RecordingActionObserver();
        await using var host = Host(observer, null, o =>
        {
            o.HitlMode = mode;
            o.RequireConfirmation = true;
            o.RequireConfirmationForWrites = true;
            o.Actions.Add<FluentActionTests.StockActions>(nameof(FluentActionTests.StockActions.Restock),
                a => a.Describe("Restocks a product").RequireRoles("Manager").RequireConfirmation().Off());
        });
        var banners = 0;
        var state = host.Get<IMentorStateService>();
        state.OnConfirmationRequired += req => { banners++; _ = state.ConfirmAsync(req.ActionId); };
        var invoked = 0;
        host.Get<IMentorPageContext>().SetPageName("Shelf").RegisterUIAction("restock", "Marks the shelf to restock", _ => invoked++);
        host.Chat!.CallTool("restock", new { parameter = 3 }).Say("Marked.");

        (await host.RunTurnAsync("mark shelf 3 to restock")).Should().BeNull();
        await FlushAsync(host);

        invoked.Should().Be(1, "mode {0}: the page's action ran", mode);
        banners.Should().Be(0, "mode {0}: the application's confirmation is not the page's", mode);
        observer.Steps("restock").Should().Equal(["Allowed/allowed", "Executed/completed"], "mode {0}", mode);
        observer.Seen.Where(d => d.ToolName == "restock").Should().OnlyContain(d =>
            d.Kind == MentorActionKind.UI && d.PageName == "Shelf" && d.RequiredRoles.Count == 0
            && d.DisplayName == "Marks the shelf to restock");
    }

    [Fact]
    public async Task A_policy_that_asks_to_confirm_a_UI_action_shows_the_pages_text()
    {
        var policy = new TestActionPolicy
        {
            Verdict = r => r.ToolName == "restock" ? MentorActionVerdict.Confirm("ask first") : MentorActionVerdict.Proceed,
        };
        await using var host = Host(new RecordingActionObserver(), policy, o =>
            o.Actions.Add<FluentActionTests.StockActions>(nameof(FluentActionTests.StockActions.Restock),
                a => a.Describe("Restocks a product").Off()));
        var state = host.Get<IMentorStateService>();
        string? banner = null;
        state.OnConfirmationRequired += req => { banner = req.Message; _ = state.ConfirmAsync(req.ActionId); };
        host.Get<IMentorPageContext>().SetPageName("Shelf").RegisterUIAction("restock", "Marks the shelf to restock", _ => { });
        host.Chat!.CallTool("restock", new { parameter = 3 }).Say("Marked.");

        (await host.RunTurnAsync("mark shelf 3 to restock")).Should().BeNull();

        banner.Should().StartWith("Marks the shelf to restock", "the banner is the page's action's, not the application's")
            .And.NotContain("Restocks a product");
    }
}
