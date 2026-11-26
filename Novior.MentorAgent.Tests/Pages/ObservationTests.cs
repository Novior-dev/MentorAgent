using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using Novior.MentorAgent.Abstractions.Core;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.Docs;
using Novior.MentorAgent.Tests.Harness;
using Xunit;

namespace Novior.MentorAgent.Tests.Pages;

/// <summary>
/// Story 1640 (L6) — what the widget observes of the pages in production, as the application keeps it: the page found
/// from the path, the elements checked as the manifest's, and kept only once two users saw them the same. Structure
/// only, in memory, within limits, and off with an option. The browser's side is in <see cref="ObservationBrowserTests"/>.
/// </summary>
public sealed class ObservationTests
{
    private static readonly System.Reflection.Assembly Tests = typeof(ObservationTests).Assembly;

    private static MentorPageDirectory Directory(TestLogCollector? logs = null, bool observe = true, bool enabled = true)
    {
        var options = new MentorOptions { AppName = "L6", ScanAssemblies = [] };
        options.AutoMentor.Enabled = enabled;
        options.AutoMentor.ObservePages = observe;
        options.AutoMentor.PageAssemblies = [Tests];
        ILogger<MentorPageDirectory> logger = logs is null
            ? NullLogger<MentorPageDirectory>.Instance
            : new TypedLogger<MentorPageDirectory>(new TestLoggerProvider(logs).CreateLogger(nameof(MentorPageDirectory)));
        return new MentorPageDirectory(Microsoft.Extensions.Options.Options.Create(options), logger);
    }

    private static MentorActionCatalog Catalog(MentorPageDirectory directory)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new MentorOptions { AppName = "L6", ScanAssemblies = [] });
        // GetPages reads the pages only: no discovery.
        return new MentorActionCatalog(null!, options, new EmptyServices(), pageDirectory: directory);
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    /// <summary>BUG-256: an observed selector the server keeps, the developer's data-mentor-id.</summary>
    private static string M(string id) => $"button[data-mentor-id=\"{id}\"]";

    private static PageObservedElement Button(string selector, string? label) => new() { Element = "button", Label = label, Type = "button", Selector = selector };

    private static MentorPageInfo Page(MentorPageDirectory directory, string route) => Catalog(directory).GetPages().Single(p => p.Route == route);

    /// <summary>What the process keeps of a page's observed elements, labels included: they never leave it.</summary>
    private static IReadOnlyList<MentorUiManifest.Element> Kept(MentorPageDirectory directory, string route)
        => directory.Pages().Single(p => p.Route == route).Observed;

    private const long Anna = 1, Bruno = 2;

    [Theory]
    [Trait("Rule", "page.observe-private")]
    [InlineData("#customer-4711")]
    [InlineData("main > #order-1002 > button")]
    [InlineData("div.row-4711 > button")]
    [InlineData(".btn-4711")]
    [InlineData("input[name=\"mario.rossi\"]")]
    [InlineData("button[data-testid=\"save-4711\"]")]
    [InlineData("button[aria-label=\"Mario Rossi\"]")]
    [InlineData("button[title=\"Mario Rossi\"]")]
    [InlineData("a[href=\"/c/901\"]")]
    [InlineData("button[data-mentor-id='ticket']")]
    [InlineData("button[data-mentor-id=\"ticket\"][title=\"Mario\"]")]
    [InlineData("button:not(.x)")]
    [InlineData("li:has(> a)")]
    [InlineData("button:nth-of-type(odd)")]
    [InlineData("body button")]
    [InlineData("body >button")]
    [InlineData("Button")]
    [InlineData("button[data-mentor-id=\"\"]")]
    public void An_observed_selector_with_anything_of_the_page_in_it_is_not_kept(string selector)
    {
        // BUG-256: what is allowed, not what is not: a tag, a position, the developer's data-mentor-id, and '>'.
        MentorUiManifest.IsObservedSelector(selector).Should().BeFalse();
        var directory = Directory();

        directory.Observe("/l1/home", [Button(selector, "Save")], Anna, 1);
        directory.Observe("/l1/home", [Button(selector, "Save")], Bruno, 1);

        Kept(directory, "/l1/home").Should().BeEmpty("seen the same by two users, a selector of the record is still the record's");
    }

    [Theory]
    [Trait("Rule", "page.observe-private")]
    [InlineData("button")]
    [InlineData("body > main > div:nth-of-type(2) > button")]
    [InlineData("tbody > tr > td:nth-child(3) > a")]
    [InlineData("section[data-mentor-id=\"ticket\"] > button")]
    [InlineData("input[data-mentor-id=\"ticket\\.title\"]")]
    [InlineData("body > x-b12 > button")]
    public void An_observed_selector_of_tags_positions_and_data_mentor_ids_is_kept(string selector)
    {
        MentorUiManifest.IsObservedSelector(selector).Should().BeTrue();
        var directory = Directory();

        directory.Observe("/l1/home", [Button(selector, "Save")], Anna, 1);
        directory.Observe("/l1/home", [Button(selector, "Save")], Bruno, 1);

        Kept(directory, "/l1/home").Select(e => e.Selector).Should().Equal([selector]);
    }

    [Fact]
    [Trait("Rule", "page.observe")]
    [Trait("Rule", "page.observe-shared")]
    public void A_dialogs_elements_seen_by_two_users_are_the_pages_as_observed()
    {
        var directory = Directory();
        PageObservedElement[] dialog = [Button(M("confirm"), "Confirm"), new() { Element = "input", Role = "textbox", Label = "Amount", Type = "number", Selector = M("amount") }];

        directory.Observe("/l1/home", dialog, Anna, 1).Should().BeTrue();
        Page(directory, "/l1/home").Coverage.Should().Be(MentorPageCoverage.ToScan, "one user's view is not the pages' yet");

        directory.Observe("/l1/home", dialog, Bruno, 1).Should().BeTrue();
        var home = Page(directory, "/l1/home");
        home.Coverage.Should().Be(MentorPageCoverage.Observed);
        home.Elements.Select(e => (e.Element, e.Selector, e.Type, e.Coverage)).Should().Equal(
            ("button", M("confirm"), "button", MentorPageCoverage.Observed), ("input", M("amount"), "number", MentorPageCoverage.Observed));
        home.Elements.Should().OnlyContain(e => e.Roles.Count == 0 && e.Options.Count == 0, "the users' roles are not the scan's, and options may be records");
        Kept(directory, "/l1/home").Select(e => e.Label).Should().Equal("Confirm", "Amount");
    }

    [Fact]
    [Trait("Rule", "page.observe-shared")]
    public void A_label_one_user_sees_never_reaches_the_others_however_often_that_user_sends_it()
    {
        var directory = Directory();

        directory.Observe("/l1/home", [Button(M("user-menu"), "Mario Rossi ▾")], Anna, 1);
        directory.Observe("/l1/home", [Button(M("user-menu"), "Mario Rossi ▾")], Anna, 2);
        directory.Observe("/l1/home", [Button(M("user-menu"), "Luigi Bianchi ▾")], Bruno, 1);

        Page(directory, "/l1/home").Elements.Should().BeEmpty("each user saw the menu with a name of their own");
        Kept(directory, "/l1/home").Should().BeEmpty();

        directory.Observe("/l1/home", [Button(M("user-menu"), "Luigi Bianchi ▾")], 3, 1);
        Kept(directory, "/l1/home").Should().ContainSingle().Which.Label.Should().Be("Luigi Bianchi ▾",
            "two users saw it the same: then it is structure, not one user's");
    }

    [Fact]
    [Trait("Rule", "page.observe-shared")]
    public void Two_tabs_of_one_signed_in_user_are_one_user()
    {
        static ClaimsPrincipal User(string id) => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id)], "Cookies"));

        MentorPageDirectory.ViewerOf(User("u-1")).Should().Be(MentorPageDirectory.ViewerOf(User("u-1")));
        MentorPageDirectory.ViewerOf(User("u-1")).Should().NotBe(MentorPageDirectory.ViewerOf(User("u-2")));
        MentorPageDirectory.ViewerOf(new ClaimsPrincipal(new ClaimsIdentity())).Should().BeNull("nobody signed in: each circuit is one");
        MentorPageDirectory.NewViewer().Should().NotBe(MentorPageDirectory.NewViewer());
    }

    [Fact]
    [Trait("Rule", "page.observe-private")]
    [Trait("Rule", "page.observe-shared")]
    public void In_a_MAUI_app_one_user_is_enough_and_what_they_saw_never_leaves_the_process()
    {
        var directory = Directory();
        PageObservedElement[] menu = [new() { Element = "button", Label = "Mario Rossi ▾", Type = "button", Form = "Mario's profile", Selector = M("user-menu") }];

        directory.Observe("/l1/home", menu, Anna, 1, oneUser: true).Should().BeTrue();

        Kept(directory, "/l1/home").Should().ContainSingle().Which.Label.Should().Be("Mario Rossi ▾", "kept in the process");
        var pages = Catalog(directory).GetPages();
        pages.Single(p => p.Route == "/l1/home").Elements.Should().ContainSingle().Which.Should().Match<MentorPageElement>(
            e => e.Selector == M("user-menu") && e.Label == null && e.Form == null && e.Coverage == MentorPageCoverage.Observed);
        JsonSerializer.Serialize(pages).Should().NotContain("Mario", "whoever reads the catalogue, and wherever they send it");
    }

    [Fact]
    [Trait("Rule", "page.observe-shared")]
    public void A_MAUI_app_is_known_by_its_web_views_runtime()
    {
        var directory = Directory();
        var options = Microsoft.Extensions.Options.Options.Create(new MentorOptions { AppName = "L6", ScanAssemblies = [] });

        new MentorHostPageObserver(directory, options, new Runtime(new Microsoft.AspNetCore.Components.WebView.Fake.WebViewRuntime())).OneUser
            .Should().BeTrue();
        new MentorHostPageObserver(directory, options, new Runtime(new OtherRuntime())).OneUser.Should().BeFalse("Blazor Server: many users");
        new MentorHostPageObserver(directory, options, new EmptyServices()).OneUser.Should().BeFalse();
    }

    private sealed class Runtime(IJSRuntime runtime) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(IJSRuntime) ? runtime : null;
    }

    private sealed class OtherRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => throw new NotSupportedException();
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => throw new NotSupportedException();
    }

    [Fact]
    [Trait("Rule", "page.observe")]
    public void On_a_scanned_page_what_the_scan_did_not_read_is_added_as_observed_and_the_page_stays_scanned()
    {
        var directory = Directory();
        PageObservedElement[] seen = [Button("[data-testid=save]", "Save"), Button(M("export"), "Export")];

        directory.Observe("/l1/orders", seen, Anna, 1);
        directory.Observe("/l1/orders", seen, Bruno, 1);

        var orders = Page(directory, "/l1/orders");
        orders.Coverage.Should().Be(MentorPageCoverage.Scanned);
        orders.Elements.Where(e => e.Coverage == MentorPageCoverage.Observed).Select(e => e.Selector).Should().Equal([M("export")],
            "what the scan read is not added again");
        orders.Elements.Where(e => e.Coverage == MentorPageCoverage.Scanned).Should().HaveCount(3);
    }

    [Fact]
    [Trait("Rule", "page.observe-route")]
    public void The_path_finds_its_page_and_is_never_kept_or_logged()
    {
        var logs = new TestLogCollector();
        var directory = Directory(logs);

        directory.Observe("/l1/orders/424242", [Button(M("back"), "Back")], Anna, 1);
        directory.Observe("/l1/orders/424242", [Button(M("back"), "Back")], Bruno, 1);
        directory.Observe("/l1/nowhere/424242", [Button(M("lost"), "Lost")], Anna, 2);
        directory.Observe("/l1/nowhere/424242", [Button(M("lost"), "Lost")], Bruno, 2);

        Page(directory, "/l1/orders/{id:int}").Elements.Select(e => e.Selector).Should().Equal(M("back"));
        Catalog(directory).GetPages().SelectMany(p => p.Elements).Should().NotContain(e => e.Selector == M("lost"), "a path that is no page's");
        logs.Records.Should().Contain(r => r.Message.Contains("page.observe-route"));
        logs.Records.Should().NotContain(r => r.Message.Contains("424242"), "a path may hold a record's id");
        logs.Records.Should().NotContain(r => r.Message.Contains("Back"), "the log never has a text of the page");
    }

    [Theory]
    [Trait("Rule", "page.observe-route")]
    [InlineData("/orders", "/orders")]
    [InlineData("/orders/new", "/orders/new")]
    [InlineData("/orders/42", "/orders/{id:int}")]
    [InlineData("/orders/abc", "/orders/{name}")]
    [InlineData("/orders/42/lines", "/orders/{id:int}/lines")]
    [InlineData("/files/a/b/c", "/files/{*path}")]
    [InlineData("/report", "/report/{year:int?}")]
    [InlineData("/report/2026", "/report/{year:int?}")]
    [InlineData("/ORDERS/%34%32", "/orders/{id:int}")]
    [InlineData("/orders/42/lines/x", null)]
    [InlineData("/report/last", null)]
    [InlineData("orders", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void A_path_opens_the_page_the_router_would_take(string? path, string? page)
    {
        string[] templates = ["/orders/{name}", "/orders/{id:int}", "/orders/new", "/orders", "/orders/{id:int}/lines", "/files/{*path}",
            "/report/{year:int?}", "/mixed/a{b}"];

        MentorPageRoutes.Match(path, templates).Should().Be(page);
    }

    [Fact]
    [Trait("Rule", "page.observe-values")]
    public void What_MentorAgent_never_lets_out_is_not_kept_even_from_a_client_that_sends_it()
    {
        var directory = Directory();
        PageObservedElement[] sent =
        [
            new() { Element = "input", Type = "password", Label = "Password", Selector = M("pwd") },
            new() { Element = "input", Type = "text", Label = "Name", Selector = "" },
            new() { Element = "bad tag", Label = "Bad", Selector = M("bad") },
            new() { Element = "button", Label = "Save\u0007", Selector = M("save") },
        ];

        directory.Observe("/l1/home", sent, Anna, 1);
        directory.Observe("/l1/home", sent, Bruno, 1);

        Kept(directory, "/l1/home").Should().ContainSingle().Which.Should().Match<MentorUiManifest.Element>(e => e.Selector == M("save") && e.Label == "Save");
    }

    [Fact]
    [Trait("Rule", "page.observe-limit")]
    public void Past_its_limits_the_observation_stops_and_says_so_once()
    {
        var logs = new TestLogCollector();
        var directory = Directory(logs);

        directory.Observe("/l1/home", [Button(M("a"), "A")], Anna, MentorPageDirectory.MaxObservedReports).Should().BeTrue();
        directory.Observe("/l1/home", [Button(M("b"), "B")], Anna, MentorPageDirectory.MaxObservedReports + 1).Should().BeFalse(
            "past its messages a connection stops");
        directory.Observe("/l1/home", [Button(M("c"), "C")], Bruno, 1, limit: "elements").Should().BeFalse("the browser's last message");

        // A page keeps at most as many observed elements as the manifest has.
        var many = Enumerable.Range(0, MentorUiManifest.MaxElementsPerPage + 20).Select(i => Button(M("e" + i), "E" + i)).ToArray();
        for (var at = 0; at < many.Length; at += 100)
        {
            directory.Observe("/l1/orders", many[at..Math.Min(at + 100, many.Length)], Anna, 2);
            directory.Observe("/l1/orders", many[at..Math.Min(at + 100, many.Length)], Bruno, 2);
        }
        Page(directory, "/l1/orders").Elements.Count(e => e.Coverage == MentorPageCoverage.Observed).Should().Be(MentorUiManifest.MaxElementsPerPage);

        logs.Records.Count(r => r.Message.Contains("page.observe-limit: the observation of the pages reached")).Should().Be(1, "written once");
        logs.Records.Count(r => r.Message.Contains("page.observe-limit: a browser stopped observing the pages (elements)")).Should().Be(1);
    }

    [Fact]
    [Trait("Rule", "page.observe-limit")]
    public void Elements_waiting_for_a_second_user_are_bounded()
    {
        var logs = new TestLogCollector();
        var directory = Directory(logs);

        for (var i = 0; i < MentorPageDirectory.MaxWaiting / 500 + 1; i++)
            directory.Observe("/l1/home", Enumerable.Range(0, 500).Select(n => Button(M($"w{i}-{n}"), "W")).ToArray(), Anna, i + 1);
        directory.Observe("/l1/home", [Button(M("w0-0"), "W"), Button(M($"w{MentorPageDirectory.MaxWaiting / 500}-0"), "W")], Bruno, 1);

        Page(directory, "/l1/home").Elements.Select(e => e.Selector).Should().Equal([M("w0-0")], "the one past the bound never waited");
        logs.HasWarning("page.observe-limit").Should().BeTrue();
    }

    [Fact]
    [Trait("Rule", "page.observe-off")]
    public void With_ObservePages_off_nothing_is_observed_and_the_widget_does_not_start()
    {
        var directory = Directory(observe: false);

        directory.Observe("/l1/home", [Button(M("a"), "A")], Anna, 1).Should().BeFalse();
        directory.Observe("/l1/home", [Button(M("a"), "A")], Bruno, 1).Should().BeFalse();
        Page(directory, "/l1/home").Elements.Should().BeEmpty();

        var options = new MentorOptions { AppName = "L6", ScanAssemblies = [] };
        options.AutoMentor.Enabled = true;
        options.AutoMentor.ObservePages = false;
        new MentorHostPageObserver(directory, Microsoft.Extensions.Options.Options.Create(options), new EmptyServices()).IsOn.Should().BeFalse();
        options.AutoMentor.ObservePages = true;
        new MentorHostPageObserver(directory, Microsoft.Extensions.Options.Options.Create(options), new EmptyServices()).IsOn.Should().BeTrue();
        options.AutoMentor.Enabled = false;
        new MentorHostPageObserver(directory, Microsoft.Extensions.Options.Options.Create(options), new EmptyServices()).IsOn.Should().BeFalse(
            "the AutoMentor is off");
        new MentorAutoMentorOptions().ObservePages.Should().BeTrue("on by default, with the AutoMentor, which is off by default");
    }

    [Fact]
    [Trait("Rule", "page.observe")]
    public async Task The_widgets_end_is_what_the_module_calls_and_a_failure_stops_the_observation_not_the_widget()
    {
        var method = typeof(MentorPageObservation).GetMethod(nameof(MentorPageObservation.OnPageObserved))!;
        method.GetCustomAttributes(typeof(JSInvokableAttribute), false).Should().ContainSingle("MentorPageObserver.js calls it by this name");
        method.GetParameters().Select(p => p.ParameterType).Should().Equal(typeof(string), typeof(List<PageObservedElement?>), typeof(string));
        File.ReadAllText(Path.Combine(RepoFiles.Root, "Novior.MentorAgent.Abstractions", "wwwroot", "js", "MentorPageObserver.js"))
            .Should().Contain("invokeMethodAsync('OnPageObserved', path, batch.map(b => b[1]), limit)");

        (await new MentorPageObservation(new Throwing(), NullLogger.Instance).OnPageObserved("/", [], null)).Should().BeFalse();
    }

    [Fact]
    [Trait("Rule", "page.observe")]
    public void The_scan_reads_with_the_widgets_own_reader()
    {
        var reader = Novior.MentorAgent.Scanner.MentorUiScanService.Reader();

        reader.Should().StartWith("(() => {").And.EndWith("return read({ observe: false });\n})()");
        reader.Should().NotContain("export function").And.Contain("function read(options)");
        reader.Should().Contain(File.ReadAllText(Path.Combine(RepoFiles.Root, "Novior.MentorAgent.Abstractions", "wwwroot", "js", "MentorPageReader.js"))
            .Replace("export function read(", "function read("), "one reader for the scan and the observation");
    }

    private sealed class Throwing : IMentorPageObserver
    {
        public bool IsOn => true;
        public Task<bool> ReportAsync(string? path, IReadOnlyList<PageObservedElement?>? elements, string? limit)
            => throw new InvalidOperationException("boom");
    }
}
