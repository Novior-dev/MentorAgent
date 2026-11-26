using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Tests.Harness;
using Xunit;

namespace Novior.MentorAgent.Tests.Pages;

/// <summary>
/// Story 1635 (L1, slice C) — the pages are public: <see cref="IMentorActionCatalog.GetPages"/> gives each with its
/// route, its component, whether a scan read it and the elements it read, as the bridge reads them in-process.
/// </summary>
[Trait("Rule", "page.catalog")]
public sealed class PageCatalogTests
{
    private static MentorHostFixture Host(bool autoMentor = true) => MentorHostFixture.Create(o =>
    {
        o.MemoryAutoCapture = false;
        o.AutoMentor.Enabled = autoMentor;
        o.AutoMentor.PageAssemblies = [typeof(PageCatalogTests).Assembly];
    });

    [Fact]
    public async Task The_catalogue_lists_the_pages_with_their_coverage_and_the_elements_a_scan_read()
    {
        await using var host = Host();

        var pages = host.Get<IMentorActionCatalog>().GetPages();

        pages.Single(p => p.Route == "/l1/orders").Should().BeEquivalentTo(new MentorPageInfo
        {
            Route     = "/l1/orders",
            Component = typeof(L1OrdersPage).FullName,
            Coverage  = MentorPageCoverage.Scanned,
            ScannedAt = DateTimeOffset.Parse("2026-11-17T09:00:00Z"),
            Elements  =
            [
                new MentorPageElement { Element = "input", Role = "textbox", Label = "Customer", Type = "text", Form = "order", Selector = "#customer", Roles = ["Sales"] },
                new MentorPageElement { Element = "select", Role = "combobox", Label = "Status", Options = ["Open", "Closed"], Form = "order", Selector = "[name=status]" },
                new MentorPageElement { Element = "button", Role = "button", Label = "Save", Selector = "[data-testid=save]" },
            ],
        }, o => o.WithStrictOrdering());

        pages.Single(p => p.Route == "/l1/home").Should().BeEquivalentTo(new MentorPageInfo
        {
            Route = "/l1/home", Component = typeof(L1HomePage).FullName, Coverage = MentorPageCoverage.ToScan,
        });
        pages.Single(p => p.Route == "/m1/manifest-only").Should().Match<MentorPageInfo>(p =>
            p.Component == null && p.Coverage == MentorPageCoverage.Scanned && p.Elements.Count == 0);
    }

    [Fact]
    public async Task Nothing_the_catalogue_gives_carries_what_never_comes_out()
    {
        await using var host = Host();

        var all = JsonSerializer.Serialize(host.Get<IMentorActionCatalog>().GetPages());

        foreach (var never in new[] { "Password", "Card number", "Hidden action", "Disabled action", "Excluded action", "Mario Rossi" })
            all.Should().NotContain(never);
    }

    [Fact]
    public async Task A_reader_cannot_change_the_pages_through_what_it_was_given()
    {
        await using var host = Host();
        var catalog = host.Get<IMentorActionCatalog>();

        var status = catalog.GetPages().Single(p => p.Route == "/l1/orders").Elements[1];
        if (status.Options is IList<string> { IsReadOnly: false } options) options.Clear();

        catalog.GetPages().Single(p => p.Route == "/l1/orders").Elements[1].Options.Should().Equal("Open", "Closed");
    }

    [Fact]
    public async Task With_the_AutoMentor_off_the_catalogue_lists_no_page()
    {
        await using var host = Host(autoMentor: false);

        host.Get<IMentorActionCatalog>().GetPages().Should().BeEmpty();
    }

    [Fact]
    public async Task The_pages_a_WebAssembly_client_sends_are_in_the_catalogue_at_the_next_call()
    {
        await using var app = await ServerAppFactory.CreateAsync(o => o.AutoMentor.Enabled = true);
        var catalog = app.Services.GetRequiredService<IMentorActionCatalog>();
        catalog.GetPages().Should().NotContain(p => p.Route == "/shop/cart");

        await using var hub = app.CreateHubConnection();
        await hub.StartAsync();
        await hub.InvokeAsync("ReportPages", new List<PageRouteInfo> { new() { Route = "/shop/cart", Component = "Shop.Client.Pages.Cart" } });

        catalog.GetPages().Should().ContainSingle(p => p.Route == "/shop/cart")
            .Which.Should().Match<MentorPageInfo>(p => p.Component == "Shop.Client.Pages.Cart" && p.Coverage == MentorPageCoverage.ToScan);
    }

    [Fact]
    public void A_catalogue_written_before_the_pages_lists_none()
    {
        IMentorActionCatalog catalog = new OlderCatalog();

        catalog.GetPages().Should().BeEmpty();
    }

    private sealed class OlderCatalog : IMentorActionCatalog
    {
        public IReadOnlyList<MentorActionInfo> GetActions() => [];
    }
}
