using System.Text.Json;
using FluentAssertions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Novior.MentorAgent.Tests.Discovery;

/// <summary>
/// Story 1288, slice C — the trace of the pages' UI actions in <see cref="IMentorActionCatalog.GetRegistrations"/>: one
/// registration per page, with the names its pages declared since the application started. A page declares from the
/// browser, so the trace keeps names only, within <see cref="MentorPageActionLimits"/>, and writes what goes beyond
/// them in <see cref="MentorActionRegistration.Rejected"/> by its rule.
/// </summary>
public sealed class PageActionTraceTests
{
    private static MentorHostFixture Host() => MentorHostFixture.Create(o => o.MemoryAutoCapture = false);

    private static IReadOnlyList<MentorActionRegistration> Pages(MentorHostFixture host)
        => [.. host.Get<IMentorActionCatalog>().GetRegistrations().Where(r => r.Origin == MentorActionOrigins.Page)];

    private static MentorActionRegistration Page(MentorHostFixture host, string name)
        => Pages(host).Should().ContainSingle(r => r.Page == name).Which;

    /// <summary>A page context of its own, as another circuit or another hub connection has.</summary>
    private static IMentorPageContext OtherSession(MentorHostFixture host)
        => host.Get<IServiceScopeFactory>().CreateScope().ServiceProvider.GetRequiredService<IMentorPageContext>();

    [Fact]
    [Trait("Rule", "page.trace-names-only")]
    public async Task A_Blazor_page_is_in_the_trace_with_the_names_of_its_actions_only()
    {
        await using var host = Host();
        host.Get<IMentorPageContext>().SetPageName("Orders")
            .RegisterUIAction("select_order", "Selects an order in the grid", _ => { })
            .RegisterUIAction<int>("highlight_row", "Highlights a row", _ => { }, "integer: row id");

        var page = Page(host, "Orders");
        page.Id.Should().Be("page:Orders");
        page.Origin.Should().Be("page");
        page.Source.Should().Be("Orders");
        page.Enabled.Should().BeTrue();
        page.Actions.Should().Equal(["select_order", "highlight_row"]);
        page.Rejected.Should().BeEmpty();
        page.Revision.Should().BePositive();
        JsonSerializer.Serialize(page).Should().NotContain("Selects").And.NotContain("integer", "neither a description nor a hint");

        host.Get<IMentorActionCatalog>().GetActions().Should().NotContain(a => a.ToolName == "select_order",
            "a page's UI actions are not actions of the application");
    }

    [Fact]
    [Trait("Rule", "page.trace-names-only")]
    public async Task A_page_on_the_hub_is_in_the_trace_from_the_client_snapshot()
    {
        await using var app = await ServerAppFactory.CreateAsync();
        await using var hub = app.CreateHubConnection();
        await hub.StartAsync();

        await hub.InvokeAsync("UpdatePageContext", new PageContextSnapshot
        {
            PageName = "Orders",
            UIActions = [new UIActionInfo { Name = "select_order", Description = "Selects an order in the grid", ParameterHint = "integer: order id" }],
        });

        var page = app.Services.GetRequiredService<IMentorActionCatalog>().GetRegistrations()
            .Should().ContainSingle(r => r.Origin == MentorActionOrigins.Page).Which;
        page.Id.Should().Be("page:Orders");
        page.Actions.Should().Equal(["select_order"]);
        JsonSerializer.Serialize(page).Should().NotContain("Selects").And.NotContain("order id");
    }

    [Fact]
    [Trait("Rule", "page.union-since-start")]
    public async Task A_page_lists_what_every_session_declared_and_keeps_what_a_page_dropped()
    {
        await using var host = Host();
        var first = host.Get<IMentorPageContext>();
        first.SetPageName("Orders").RegisterUIAction("select_order", "Selects", _ => { });
        OtherSession(host).SetPageName("Orders").RegisterUIAction("filter_orders", "Filters", _ => { });

        first.UnregisterUIAction("select_order");
        first.Clear("Orders");

        Page(host, "Orders").Actions.Should().Equal(["select_order", "filter_orders"],
            "the union since the start, in the order first seen: the trace says what the pages declared");
    }

    [Fact]
    [Trait("Rule", "page.union-since-start")]
    public async Task The_revision_moves_only_when_a_page_changes_and_only_for_that_page()
    {
        await using var host = Host();
        var context = host.Get<IMentorPageContext>();
        context.SetPageName("Orders").RegisterUIAction("select_order", "Selects", _ => { });
        context.SetPageName("Customers").RegisterUIAction("open_customer", "Opens", _ => { });
        var orders = Page(host, "Orders").Revision;
        var customers = Page(host, "Customers").Revision;
        customers.Should().BeGreaterThan(orders, "one counter for the process");

        // The same declaration again, as the hub's snapshot at every change of page: nothing moves.
        context.SetPageName("Orders").RegisterUIAction("select_order", "Selects, said again", _ => { });
        Page(host, "Orders").Revision.Should().Be(orders);

        context.RegisterUIAction("filter_orders", "Filters", _ => { });
        Page(host, "Orders").Revision.Should().BeGreaterThan(customers);
        Page(host, "Customers").Revision.Should().Be(customers, "only the page that changed takes the new value");

        // A rejection the page did not list before is a change too; the same one again is not.
        var beforeRejection = Page(host, "Orders").Revision;
        context.RegisterUIAction("filter orders", "A name out of shape", _ => { });
        var afterRejection = Page(host, "Orders").Revision;
        afterRejection.Should().BeGreaterThan(beforeRejection);
        context.RegisterUIAction("filter orders", "The same name out of shape", _ => { });
        Page(host, "Orders").Revision.Should().Be(afterRejection);

        var all = host.Get<IMentorActionCatalog>().GetRegistrations();
        all.Where(r => r.Origin != MentorActionOrigins.Page).Should().NotBeEmpty()
            .And.OnlyContain(r => r.Revision == 0, "the registrations of the code are fixed at start");
        all.SkipWhile(r => r.Origin != MentorActionOrigins.Page).Should().OnlyContain(r => r.Origin == MentorActionOrigins.Page,
            "the pages come after the registrations of the code");
    }

    [Theory]
    [Trait("Rule", "page.name-shape")]
    [InlineData("select order")]
    [InlineData("seleziona_l'ordine")]
    [InlineData("città")]
    [InlineData("select\norder")]
    [InlineData("")]
    [InlineData("a_name_of_sixty_five_characters_is_one_more_than_a_tool_may_have_")]
    public async Task An_action_name_a_model_could_not_call_is_rejected_by_name_shape(string name)
    {
        await using var host = Host();
        host.Get<IMentorPageContext>().SetPageName("Orders")
            .RegisterUIAction("select_order", "Selects", _ => { })
            .RegisterUIAction(name, "Something", _ => { });

        var page = Page(host, "Orders");
        page.Actions.Should().Equal(["select_order"]);
        page.Rejected.Should().ContainSingle().Which.Rule.Should().Be("page.name-shape");
    }

    [Fact]
    [Trait("Rule", "page.name-shape")]
    public async Task A_name_of_sixty_four_tool_characters_is_listed()
    {
        await using var host = Host();
        var name = new string('a', 60) + "_-Z9";
        host.Get<IMentorPageContext>().SetPageName("Orders").RegisterUIAction(name, "Something", _ => { });

        Page(host, "Orders").Actions.Should().Equal([name]);
    }

    [Fact]
    [Trait("Rule", "page.name-shape")]
    public async Task A_rejected_name_is_cut_and_cannot_carry_control_characters()
    {
        await using var host = Host();
        host.Get<IMentorPageContext>().SetPageName("Orders")
            .RegisterUIAction("bad\u0007name\u202E" + new string('x', 10_000), "Something", _ => { });

        var rejected = Page(host, "Orders").Rejected.Should().ContainSingle().Which;
        rejected.Name.Should().HaveLength(MentorPageActionLimits.MaxActionNameLength)
            .And.StartWith("bad�name�xxx", "a control or format character is written as U+FFFD");
    }

    [Theory]
    [Trait("Rule", "page.name-shape")]
    [InlineData("Full Demo")]
    [InlineData("Ordini — riepilogo")]
    [InlineData("Ordini 📦")]
    public async Task A_page_name_with_spaces_and_letters_is_listed(string name)
    {
        await using var host = Host();
        host.Get<IMentorPageContext>().SetPageName(name).RegisterUIAction("select_order", "Selects", _ => { });

        Page(host, name).Id.Should().Be("page:" + name);
    }

    [Fact]
    [Trait("Rule", "page.name-shape")]
    public async Task A_page_name_of_128_characters_is_listed()
    {
        await using var host = Host();
        var name = new string('P', MentorPageActionLimits.MaxPageNameLength);
        host.Get<IMentorPageContext>().SetPageName(name).RegisterUIAction("select_order", "Selects", _ => { });

        Page(host, name).Actions.Should().Equal(["select_order"]);
    }

    public static TheoryData<string?> PageNamesOutOfShape => new()
    {
        null,                                    // registered before SetPageName, or a snapshot without PageName
        "",
        "   ",                                   // white space only
        " \t ",
        "Orders\n",                              // a line break
        "Or\rders",
        "Or\tders",                              // a tab
        "Or\u0000ders",                          // a control character
        "Or\u200Bders",                          // a format character (zero-width space)
        "Or\u202Eders",                          // a format character (right-to-left override)
        new string('P', MentorPageActionLimits.MaxPageNameLength + 1),
    };

    [Theory]
    [Trait("Rule", "page.name-shape")]
    [MemberData(nameof(PageNamesOutOfShape))]
    public async Task A_page_without_a_name_or_with_a_name_out_of_shape_is_not_in_the_trace(string? name)
    {
        await using var host = Host();
        var context = host.Get<IMentorPageContext>();
        if (name is not null) context.SetPageName(name);
        context.RegisterUIAction("select_order", "Selects", _ => { });
        context.RegisterUIAction("filter_orders", "Filters", _ => { });

        Pages(host).Should().BeEmpty();
        host.Logs.Records.Count(r => r.Level == Microsoft.Extensions.Logging.LogLevel.Warning && r.Message.Contains("page.name-shape"))
            .Should().Be(1, "said once, without the name, which comes from the browser");
        context.UIActions.Should().HaveCount(2, "the trace only: the page still offers its actions");
    }

    [Fact]
    [Trait("Rule", "page.name-shape")]
    public async Task A_page_name_with_a_lone_surrogate_is_not_in_the_trace()
    {
        // Built here: xUnit writes a theory's data as text, and a lone surrogate comes back as U+FFFD.
        await using var host = Host();
        host.Get<IMentorPageContext>().SetPageName(new string(['O', 'r', (char)0xD800, 'd', 'e', 'r', 's']))
            .RegisterUIAction("select_order", "Selects", _ => { });

        Pages(host).Should().BeEmpty();
    }

    [Fact]
    [Trait("Rule", "page.limit")]
    public async Task A_page_lists_64_actions_and_rejects_the_ones_after()
    {
        await using var host = Host();
        var context = host.Get<IMentorPageContext>().SetPageName("Orders");
        for (var i = 1; i <= 66; i++) context.RegisterUIAction($"action_{i}", "Something", _ => { });

        var page = Page(host, "Orders");
        page.Actions.Should().HaveCount(MentorPageActionLimits.MaxActionsPerPage).And.EndWith("action_64");
        page.Rejected.Select(r => (r.Name, r.Rule)).Should().Equal([("action_65", "page.limit"), ("action_66", "page.limit")]);
    }

    [Fact]
    [Trait("Rule", "page.limit")]
    public async Task A_page_writes_down_64_rejections_at_most()
    {
        await using var host = Host();
        var context = host.Get<IMentorPageContext>().SetPageName("Orders");
        for (var i = 1; i <= 100; i++) context.RegisterUIAction($"bad name {i}", "Something", _ => { });
        var revision = Page(host, "Orders").Revision;

        context.RegisterUIAction("bad name 101", "Something", _ => { });

        var page = Page(host, "Orders");
        page.Rejected.Should().HaveCount(MentorPageActionLimits.MaxRejectedPerPage);
        page.Revision.Should().Be(revision, "a rejection not written down is no change");
    }

    [Fact]
    [Trait("Rule", "page.limit")]
    public async Task The_trace_lists_200_pages_and_says_once_that_it_leaves_out_the_others()
    {
        await using var host = Host();
        var context = host.Get<IMentorPageContext>();
        for (var i = 1; i <= 203; i++) context.SetPageName($"Page {i}").RegisterUIAction("open", "Opens", _ => { });

        Pages(host).Select(p => p.Page).Should().HaveCount(MentorPageActionLimits.MaxPages).And.EndWith("Page 200");
        host.Logs.Records.Count(r => r.Level == Microsoft.Extensions.Logging.LogLevel.Warning && r.Message.Contains("page.limit"))
            .Should().Be(1);

        context.SetPageName("Page 1").RegisterUIAction("close", "Closes", _ => { });
        Page(host, "Page 1").Actions.Should().Equal(["open", "close"], "a page already listed still takes new names");
    }
}
