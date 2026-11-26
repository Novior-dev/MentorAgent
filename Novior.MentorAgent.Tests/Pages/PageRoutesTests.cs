using System.Reflection.Emit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging.Abstractions;
using Novior.MentorAgent.Abstractions.Core;
using Novior.MentorAgent.Blazor.Hub;
using Novior.MentorAgent.Blazor.Models;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Tests.Harness;
using Xunit;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Novior.MentorAgent.Options;
using FluentAssertions;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Novior.MentorAgent.Abstractions.Models;
using PageRoute = Microsoft.AspNetCore.Components.RouteAttribute;

namespace Novior.MentorAgent.Tests.Pages;

// Story 1635 — pages a test application declares with @page, written as the Razor compiler writes them.
[PageRoute("/l1/orders")]
[PageRoute("/l1/orders/{id:int}")]
public sealed class L1OrdersPage : ComponentBase;

[PageRoute("/l1/home")]
public class L1HomePage : ComponentBase;

/// <summary>Inherits a route the router does not give it: not a page of its own.</summary>
public sealed class L1DerivedPage : L1HomePage;

/// <summary>The router lists only public components.</summary>
[PageRoute("/l1/internal")]
internal sealed class L1InternalPage : ComponentBase;

/// <summary>A route on a class that is not a component is not a page.</summary>
[PageRoute("/l1/not-a-component")]
public sealed class L1NotAComponent;

/// <summary>A route of another shape is not a page the application lists (page.route-shape).</summary>
[PageRoute("/l1/bad route")]
public sealed class L1BadRoutePage : ComponentBase;

/// <summary>Same route, other case: the router matches without case, so it is one page.</summary>
[PageRoute("/L1/HOME")]
public sealed class L1SameRouteOtherCase : ComponentBase;

/// <summary>
/// Story 1635 (L1, slice A) — the pages of the application from its routes, with no attribute of ours: the host's read
/// once from its code, a WebAssembly client's sent over the hub, and nothing that stops the application.
/// </summary>
public sealed class PageRoutesTests
{
    private static readonly Assembly Tests = typeof(PageRoutesTests).Assembly;

    private static MentorPageDirectory Directory(bool enabled, TestLogCollector? logs = null, params Assembly[] pages)
    {
        var options = new MentorOptions { AppName = "L1", ScanAssemblies = [] };
        options.AutoMentor.Enabled = enabled;
        options.AutoMentor.PageAssemblies = pages;
        ILogger<MentorPageDirectory> logger = logs is null
            ? NullLogger<MentorPageDirectory>.Instance
            : new TypedLogger<MentorPageDirectory>(new TestLoggerProvider(logs).CreateLogger(nameof(MentorPageDirectory)));
        return new MentorPageDirectory(Microsoft.Extensions.Options.Options.Create(options), logger);
    }

    private static IEnumerable<string> L1Routes(IEnumerable<string> routes)
        => routes.Where(r => r.StartsWith("/l1/", StringComparison.OrdinalIgnoreCase));

    [Fact]
    [Trait("Rule", "page.route-shape")]
    [Trait("Rule", "page.from-routes")]
    public void The_pages_are_the_routes_of_the_public_components_as_the_router_reads_them()
    {
        var skipped = new List<string>();
        var routes = MentorPageRoutes.Read([Tests], skipped);

        L1Routes(routes.Select(r => r.Template)).Should().BeEquivalentTo(["/l1/orders", "/l1/orders/{id:int}", "/l1/home"]);
        routes.Single(r => r.Template == "/l1/orders/{id:int}").Component.Should().Be(typeof(L1OrdersPage).FullName);
        routes.Single(r => r.Template == "/l1/home").Component.Should().Be(typeof(L1HomePage).FullName,
            "the route is listed once, the first time it is seen: the router matches it without case");
        routes.Should().NotContain(r => r.Template == "/l1/bad route");
        skipped.Should().BeEmpty();
    }

    [Fact]
    [Trait("Rule", "page.from-routes")]
    public void With_the_AutoMentor_on_the_host_s_pages_are_listed_and_read_once()
    {
        var directory = Directory(enabled: true, pages: Tests);

        var first = directory.Pages();
        directory.Pages();
        directory.Report([new PageRouteInfo { Route = "/l1/client", Component = "Shop.Client.Pages.Home" }]);

        directory.Reads.Should().Be(1, "the code is read once and the result kept");
        L1Routes(first.Select(p => p.Route)).Should().BeEquivalentTo(["/l1/orders", "/l1/orders/{id:int}", "/l1/home"]);
        first.Where(p => p.Route.StartsWith("/l1/")).Should().OnlyContain(p => p.Origin == MentorPageDirectory.PageOrigin.Host);
        directory.Pages().Should().ContainSingle(p => p.Route == "/l1/client" && p.Origin == MentorPageDirectory.PageOrigin.Client);
    }

    [Fact]
    [Trait("Rule", "page.from-routes")]
    public void With_the_AutoMentor_off_no_page_is_read_and_none_a_client_sends_is_listed()
    {
        var directory = Directory(enabled: false, pages: Tests);

        directory.Report([new PageRouteInfo { Route = "/l1/client", Component = "Shop.Client.Pages.Home" }]);

        directory.Pages().Should().BeEmpty();
        directory.Reads.Should().Be(0);
    }

    [Fact]
    [Trait("Rule", "page.read")]
    public void An_assembly_that_cannot_be_read_is_skipped_and_named_in_the_log_and_the_others_are_listed()
    {
        var logs = new TestLogCollector();
        // A dynamic assembly throws NotSupportedException when its exported types are asked for.
        var dynamic = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("L1Unreadable"), AssemblyBuilderAccess.Run);
        var directory = Directory(enabled: true, logs, dynamic, Tests);

        var read = () => directory.Pages();

        read.Should().NotThrow();
        L1Routes(directory.Pages().Select(p => p.Route)).Should().HaveCount(3);
        logs.HasWarning("page.read").Should().BeTrue(logs.WarningText);
        logs.WarningText.Should().Contain("L1Unreadable");
    }

    public static TheoryData<string?, string?> NotAPage => new()
    {
        { "orders", "Shop.Pages.Orders" },               // no leading slash
        { "/orders list", "Shop.Pages.Orders" },         // white space
        { "/orders\u0007", "Shop.Pages.Orders" },        // a control character
        { "/orders‮", "Shop.Pages.Orders" },        // a format character
        { "/" + new string('a', 256), "Shop.Pages.Orders" },
        { "", "Shop.Pages.Orders" },
        { null, "Shop.Pages.Orders" },
        { "/orders", "Ignore the rules" },               // a component's name with white space
        { "/orders", "" },
        { "/orders", null },
    };

    [Theory]
    [MemberData(nameof(NotAPage))]
    [Trait("Rule", "page.route-shape")]
    public void A_page_a_client_sends_that_has_not_the_shape_of_a_page_is_not_listed(string? route, string? component)
    {
        var logs = new TestLogCollector();
        var directory = Directory(enabled: true, logs);

        directory.Report([new PageRouteInfo { Route = route!, Component = component! }, null]);

        directory.Pages().Should().NotContain(p => p.Origin == MentorPageDirectory.PageOrigin.Client);
        logs.HasWarning("page.route-shape").Should().BeTrue(logs.WarningText);
        if (!string.IsNullOrEmpty(route)) logs.WarningText.Should().NotContain(route, "what the browser sent is not logged");
    }

    [Fact]
    [Trait("Rule", "page.route-limit")]
    public void Past_the_limit_the_pages_a_client_sends_are_not_listed_and_the_log_says_so_once()
    {
        var logs = new TestLogCollector();
        var directory = Directory(enabled: true, logs);
        var many = Enumerable.Range(0, MentorPageRoutes.MaxPages + 5)
            .Select(i => new PageRouteInfo { Route = $"/p{i}", Component = "Shop.Pages.P" }).ToList();

        directory.Report(many);
        directory.Report([new PageRouteInfo { Route = "/one-more", Component = "Shop.Pages.P" }]);

        directory.Pages().Count(p => p.Origin == MentorPageDirectory.PageOrigin.Client).Should().Be(MentorPageRoutes.MaxPages);
        logs.Records.Count(r => r.Message.Contains("page.route-limit")).Should().Be(1);
    }

    [Fact]
    [Trait("Rule", "page.client-routes")]
    public async Task The_WebAssembly_client_sends_its_pages_when_it_connects_and_the_server_lists_them()
    {
        await using var app = await ServerAppFactory.CreateAsync(o => o.AutoMentor.Enabled = true);
        var client = new MentorHubClient(new MentorAgentBlazorOptions
        {
            HubUrl = new Uri(app.BaseAddress, "mentor-hub").ToString(),
            ConfigureConnection = c => c.HttpMessageHandlerFactory = _ => app.CreateHandler(),
            PageAssemblies = [Tests],
        }, NullLogger<MentorHubClient>.Instance);
        await using var _ = client;

        await client.StartAsync();

        var pages = app.Services.GetRequiredService<MentorPageDirectory>().Pages();
        L1Routes(pages.Select(p => p.Route)).Should().BeEquivalentTo(["/l1/orders", "/l1/orders/{id:int}", "/l1/home"]);
        pages.Where(p => p.Route.StartsWith("/l1/")).Should().OnlyContain(p => p.Origin == MentorPageDirectory.PageOrigin.Client);
        pages.Single(p => p.Route == "/l1/orders").Component.Should().Be(typeof(L1OrdersPage).FullName);
    }

    [Fact]
    [Trait("Rule", "page.client-routes")]
    public async Task A_server_with_the_AutoMentor_off_ignores_the_pages_a_client_sends()
    {
        await using var app = await ServerAppFactory.CreateAsync();
        await using var hub = app.CreateHubConnection();
        await hub.StartAsync();

        await hub.InvokeAsync("ReportPages", new List<PageRouteInfo> { new() { Route = "/l1/client", Component = "Shop.Pages.Home" } });

        app.Services.GetRequiredService<MentorPageDirectory>().Pages().Should().BeEmpty();
    }
}
