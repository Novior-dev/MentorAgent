using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Scanner;
using Novior.MentorAgent.Scanner.Cdp;
using Novior.MentorAgent.Tests.App;
using Novior.MentorAgent.Tests.Docs;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Xunit;
using PageRoute = Microsoft.AspNetCore.Components.RouteAttribute;

namespace Novior.MentorAgent.Tests.Scanner;

/// <summary>A page of the scanned site: a form with what the scan must read and what it must never let out.</summary>
/// <summary>1640 — a table of records: the scan reads it once per column, with the header, never a row's text.</summary>
[PageRoute("/scan/table")]
public sealed class ScanTablePage : ComponentBase
{
    protected override void BuildRenderTree(RenderTreeBuilder builder)
        => builder.AddMarkupContent(0, """
            <!DOCTYPE html><html><head><title>Table</title></head><body>
            <button type="button" id="add">Add</button>
            <table id="orders"><thead><tr><th>Customer</th><th>Actions</th></tr></thead><tbody>
              <tr id="o-1"><td><a href="/scan/empty">Ada Lovelace</a></td><td><button type="button">Delete</button></td></tr>
              <tr id="o-2"><td><a href="/scan/empty">Grace Hopper</a></td><td><button type="button">Delete</button></td></tr>
            </tbody></table>
            <ul><li><a href="/scan/empty" id="first">Orders</a></li><li><a href="/scan/empty" id="second">Invoices</a></li></ul>
            </body></html>
            """);
}

[PageRoute("/scan/form")]
public sealed class ScanFormPage : ComponentBase
{
    internal const string Typed = "secret text someone typed";
    internal const string Edited = "notes someone edited";
    internal const string Value = "Ada Lovelace";

    protected override void BuildRenderTree(RenderTreeBuilder builder)
        => builder.AddMarkupContent(0, $"""
            <!DOCTYPE html><html><head><meta charset="utf-8"><title>Form</title></head><body>
            <h1>New order</h1>
            <a href="/scan/empty">Orders</a>
            <form id="order" action="/scan/form" method="post">
              <label for="customer">Customer</label><input id="customer" name="customer" value="{Value}">
              <label for="secret">Password</label><input id="secret" type="password" name="secret">
              <input name="card" autocomplete="billing cc-number" aria-label="Card">
              <input type="hidden" name="token" value="t-123">
              <select id="state" aria-label="State"><option>Open</option><option>Closed</option></select>
              <textarea id="note" aria-label="Note">{Typed}</textarea>
              <div role="textbox" contenteditable="true" id="memo">{Edited}</div>
              <button type="button" disabled>Locked</button>
              <div data-mentor-exclude><button type="button" id="private">Private</button></div>
              <div hidden><button type="button" id="ghost">Ghost</button></div>
              <button type="submit">Save</button>
            </form>
            </body></html>
            """);
}

/// <summary>A page with nothing to act on.</summary>
[PageRoute("/scan/empty")]
public sealed class ScanEmptyPage : ComponentBase
{
    protected override void BuildRenderTree(RenderTreeBuilder builder)
        => builder.AddMarkupContent(0, "<!DOCTYPE html><html><head><title>Empty</title></head><body><h1>Nothing here</h1></body></html>");
}

/// <summary>
/// A page with a route parameter: with no value for it the scan does not open it and it stays to scan; slice C opens it
/// with the value of <c>Scanner:RouteParameters</c>, which the label shows.
/// </summary>
[PageRoute("/scan/orders/{id:int}")]
public sealed class ScanOrderPage : ComponentBase
{
    [Parameter] public int Id { get; set; }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
        => builder.AddMarkupContent(0,
            $"<!DOCTYPE html><html><body><button type=\"button\" aria-label=\"Print order {Id}\">Print</button></body></html>");
}

/// <summary>Slice C — a layout, inside another layout: both are part of the fingerprint of a page that uses it.</summary>
public sealed class ScanOuterLayout : LayoutComponentBase
{
    protected override void BuildRenderTree(RenderTreeBuilder builder) => builder.AddContent(0, Body);
}

[Layout(typeof(ScanOuterLayout))]
public sealed class ScanInnerLayout : LayoutComponentBase
{
    protected override void BuildRenderTree(RenderTreeBuilder builder) => builder.AddContent(0, Body);
}

/// <summary>Slice C — a component with a layout and a render fragment of its own; not a page.</summary>
[Layout(typeof(ScanInnerLayout))]
public sealed class ScanLayoutPage : ComponentBase
{
    protected override void BuildRenderTree(RenderTreeBuilder builder)
        => builder.AddContent(0, (RenderFragment)(inner => inner.AddMarkupContent(0, "<p>inside</p>")));
}

[Layout(typeof(ScanCycleB))]
public sealed class ScanCycleA : LayoutComponentBase
{
    protected override void BuildRenderTree(RenderTreeBuilder builder) => builder.AddContent(0, Body);
}

[Layout(typeof(ScanCycleA))]
public sealed class ScanCycleB : LayoutComponentBase
{
    protected override void BuildRenderTree(RenderTreeBuilder builder) => builder.AddContent(0, Body);
}

/// <summary>
/// Slice B — a page with what the scan may click and what it must never click. Each element tells the site when it is
/// clicked; the panel of the second tab shows only after its click.
/// </summary>
[PageRoute("/scan/clicks")]
public sealed class ScanClicksPage : ComponentBase
{
    internal static readonly string[] Clicked = ["tab-a", "tab-b", "tab-c", "expand", "summary", "leave"];
    internal static readonly string[] NeverClicked = ["confirmed", "skip", "submit-tab", "link-tab", "elimina", "form-tab", "plain"];

    protected override void BuildRenderTree(RenderTreeBuilder builder)
        => builder.AddMarkupContent(0, """
            <!DOCTYPE html><html><head><meta charset="utf-8"><title>Clicks</title>
            <script>function hit(what) { fetch('/scan/clicked/' + what, { keepalive: true }); }</script></head><body>
            <div role="tablist">
              <button type="button" role="tab" id="tab-a" onclick="hit('tab-a')">Overview</button>
              <button type="button" role="tab" id="tab-b" onclick="hit('tab-b'); document.getElementById('panel-b').hidden = false">Details</button>
              <button type="button" role="tab" id="tab-c" onclick="if (confirm('Sure?')) hit('confirmed'); hit('tab-c')">Notes</button>
              <button type="button" role="tab" id="tab-d" data-mentor-scan="skip" onclick="hit('skip')">Audit</button>
              <button type="submit" role="tab" id="tab-e" onclick="hit('submit-tab')">Totals</button>
              <a role="tab" id="tab-f" href="/scan/empty" onclick="hit('link-tab'); return false">Elsewhere</a>
            </div>
            <div id="panel-b" hidden><button type="button" id="export">Export</button></div>
            <button type="button" aria-haspopup="menu" id="actions" onclick="hit('elimina')">Elimina</button>
            <button type="button" aria-expanded="false" id="filters" onclick="hit('expand'); this.setAttribute('aria-expanded', 'true')">Filters</button>
            <details><summary id="more" onclick="hit('summary')">More</summary><p>Details text</p></details>
            <form action="/scan/clicks" method="post"><button type="button" role="tab" id="shipping" onclick="hit('form-tab')">Shipping</button></form>
            <button type="button" id="refresh" onclick="hit('plain')">Refresh</button>
            <button type="button" role="tab" id="tab-g" onclick="hit('leave'); location.href = '/scan/team'">Away</button>
            </body></html>
            """);
}

/// <summary>Slice B — a page only a Manager may open.</summary>
[PageRoute("/scan/staff")]
public sealed class ScanStaffPage : ComponentBase
{
    protected override void BuildRenderTree(RenderTreeBuilder builder)
        => builder.AddMarkupContent(0, "<!DOCTYPE html><html><body><button type=\"button\" id=\"credit\">Issue credit</button></body></html>");
}

/// <summary>Slice B — a page anyone may open, with one button for a Manager only.</summary>
[PageRoute("/scan/team")]
public sealed class ScanTeamPage : ComponentBase
{
    [Parameter] public bool Manager { get; set; }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
        => builder.AddMarkupContent(0, "<!DOCTYPE html><html><body><button type=\"button\" id=\"roster\">Roster</button>"
                                       + (Manager ? "<button type=\"button\" id=\"payroll\">Payroll</button>" : "") + "</body></html>");
}

/// <summary>
/// Story 1637 — two pages of a WebAssembly standalone app: the server knows nothing of them and serves the same
/// index.html for every address; the page draws itself in the browser. Their routes come from this assembly.
/// </summary>
[PageRoute("/wasm/home")]
public sealed class WasmHomePage : ComponentBase;

[PageRoute("/wasm/reports")]
public sealed class WasmReportsPage : ComponentBase;

/// <summary>
/// 1636 E — a page with MentorAgent's chat widget on it and elements CSS hides: neither is the page's. The widget's
/// button would tell the site if it were clicked.
/// </summary>
[PageRoute("/scan/widget")]
public sealed class ScanWidgetPage : ComponentBase
{
    protected override void BuildRenderTree(RenderTreeBuilder builder)
        => builder.AddMarkupContent(0, """
            <!DOCTYPE html><html><body>
            <button type="button" id="visible">Print</button>
            <div class="bm-root bm-pos-right">
              <button type="button" id="assistant" aria-label="Open the assistant" aria-expanded="false"
                      onclick="fetch('/scan/clicked/assistant')">AI</button>
              <input id="ask" aria-label="Write a message" />
              <button type="button" id="new-chat">New conversation</button>
            </div>
            <div id="blazor-error-ui" style="display:none"><a id="reload" href="">Reload</a></div>
            <div style="display:none"><span><button type="button" id="buried" aria-expanded="false"
                 onclick="fetch('/scan/clicked/buried')">Buried</button></span></div>
            </body></html>
            """);
}

/// <summary>What the scanned site was clicked on.</summary>
public sealed class ScanClickLog
{
    public ConcurrentQueue<string> Hits { get; } = new();
}

/// <summary>
/// Story 1636, slice A — the scan of the pages: once the application has started, in <c>Development</c>, it opens each
/// page without route parameters in the browser installed and writes the manifest the AutoMentor reads.
/// </summary>
public sealed class ScannerTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "mentor-scan-tests-" + Guid.NewGuid().ToString("N"));

    private string ManifestPath => Path.Combine(_folder, "mentor-ui-manifest.json");

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    /// <summary>A Blazor site on Kestrel, on a port of the loopback: its pages rendered by Blazor, with the scan registered.</summary>
    private async Task<WebApplication> SiteAsync(string environment = "Development", Action<MentorUiScannerOptions>? scanner = null,
        Action<WebApplication>? map = null, Action<IServiceCollection>? services = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = environment,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddRazorComponents();
        // Sign-in sends to a page that renders: the scan must not read it as the page it asked for.
        builder.Services.AddAuthentication("Cookies").AddCookie("Cookies", o => o.LoginPath = "/scan/empty");
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<ScanClickLog>();
        builder.Services.AddScoped<TestOrderService>();
        builder.Services.AddScoped<TestWeatherService>();
        builder.Services.AddScoped<TestShippingAgent>();
        builder.Services.AddScoped<TestExpenseSkill>();
        builder.Services.AddScoped<TestAnalysisTools>();
        builder.Services.AddMentorAgent(o =>
        {
            o.AppName = "ScanSite";
            o.ScanAssemblies = [typeof(TestOrderService).Assembly];
            o.ChatClient = new FakeChatClient();
            o.MemoryAutoCapture = false;
            o.CheckAnnouncedActions = false;
            o.AutoMentor.Enabled = true;
            o.AutoMentor.PageAssemblies = [typeof(ScannerTests).Assembly];
        });
        builder.Services.AddMentorUiScanner(o =>
        {
            o.ManifestPath = ManifestPath;
            scanner?.Invoke(o);
        });

        services?.Invoke(builder.Services);

        var app = builder.Build();
        app.MapGet("/scan/form", () => new RazorComponentResult<ScanFormPage>());
        app.MapGet("/scan/widget", () => new RazorComponentResult<ScanWidgetPage>());
        app.MapGet("/scan/table", () => new RazorComponentResult<ScanTablePage>());
        app.MapGet("/scan/empty", () => new RazorComponentResult<ScanEmptyPage>());
        app.MapGet("/scan/orders/{id:int}", (int id) => new RazorComponentResult<ScanOrderPage>(new { Id = id }));
        app.MapGet("/scan/clicks", () => new RazorComponentResult<ScanClicksPage>());
        app.MapGet("/scan/staff", () => new RazorComponentResult<ScanStaffPage>()).RequireAuthorization(p => p.RequireRole("Manager"));
        app.MapGet("/scan/team", (HttpContext context)
            => new RazorComponentResult<ScanTeamPage>(new { Manager = context.User.IsInRole("Manager") }));
        app.MapGet("/scan/clicked/{what}", (string what, ScanClickLog log) =>
        {
            log.Hits.Enqueue(what);
            return Results.NoContent();
        });
        map?.Invoke(app);
        await app.StartAsync();
        return app;
    }

    private static Task<MentorUiScanResult?> ScanOf(WebApplication app)
        => app.Services.GetRequiredService<MentorUiScanService>().Completion.WaitAsync(TimeSpan.FromMinutes(3));

    [BrowserFact]
    [Trait("Rule", "page.scan")]
    public async Task The_scan_writes_the_manifest_with_every_page_that_has_no_parameter()
    {
        await using var app = await SiteAsync();

        var result = await ScanOf(app);

        result.Should().NotBeNull();
        result!.ManifestPath.Should().Be(ManifestPath);
        await using var file = File.OpenRead(ManifestPath);
        var manifest = MentorUiManifest.Parse(file);
        manifest.Problem.Should().BeNull();
        manifest.Dropped.Should().Be(0, "the scan never writes what the reader drops");
        manifest.Excluded.Should().Be(0, "the scan already left out what MentorAgent never lets out");

        var expected = app.Services.GetRequiredService<MentorPageDirectory>().Pages()
            .Where(p => p.Origin == MentorPageDirectory.PageOrigin.Host && !p.Route.Contains('{')).Select(p => p.Route);
        // The test assembly has the pages of other tests too, which this site does not serve: those are not read, and
        // are left out. Every page without a parameter is either in the manifest or among them.
        manifest.Pages.Select(p => p.Route).Concat(result.Failed).Should().BeEquivalentTo(expected);
        manifest.Pages.Select(p => p.Route).Should().Contain(["/scan/form", "/scan/empty"])
            .And.NotContain("/scan/orders/{id:int}", "a page with a route parameter has no value to open it with");
        result.Waiting.Should().Contain("/scan/orders/{id:int}", "it stays to scan");
        result.Failed.Should().OnlyContain(r => r.StartsWith("/l1/") || r.StartsWith("/wasm/") || r == "/scan/staff",
            "only the pages this site does not serve, and the one a scan with no user may not open");
        result.Failed.Should().Contain("/scan/staff", "sent to sign in, the scan does not read the sign-in page in its place");
        manifest.Pages.Should().OnlyContain(p => p.ScannedAt != null);
    }

    [BrowserFact]
    [Trait("Rule", "page.scan")]
    [Trait("Rule", "page.manifest-excluded")]
    public async Task The_scan_reads_the_structure_and_never_a_value_nor_what_is_left_out()
    {
        await using var app = await SiteAsync();

        var result = await ScanOf(app);

        result!.Excluded.Should().BeGreaterThanOrEqualTo(6,
            "a password, card data, a hidden field, an element in a hidden container, a disabled and an excluded element");
        var text = await File.ReadAllTextAsync(ManifestPath);
        text.Should().NotContain(ScanFormPage.Typed).And.NotContain(ScanFormPage.Edited).And.NotContain(ScanFormPage.Value)
            .And.NotContain("t-123", "a value is never read");
        text.Should().NotContain("Password").And.NotContain("Card").And.NotContain("Locked").And.NotContain("Private")
            .And.NotContain("Ghost");

        await using var file = File.OpenRead(ManifestPath);
        var form = MentorUiManifest.Parse(file).Pages.Single(p => p.Route == "/scan/form").Elements;
        form.Should().ContainSingle(e => e.Tag == "input" && e.Label == "Customer" && e.Type == "text" && e.Form == "order"
                                         && e.Selector == "#customer");
        form.Should().ContainSingle(e => e.Tag == "select" && e.Label == "State").Which.Options.Should().Equal("Open", "Closed");
        form.Should().ContainSingle(e => e.Tag == "button" && e.Label == "Save" && e.Type == "submit");
        form.Should().ContainSingle(e => e.Tag == "a" && e.Label == "Orders");
        form.Should().ContainSingle(e => e.Tag == "textarea" && e.Label == "Note");
        form.Should().ContainSingle(e => e.Tag == "div" && e.Role == "textbox" && e.Label == null,
            "the text of an editable element is what someone typed, never its label");
        form.Should().OnlyContain(e => e.Roles.Count == 0, "slice A scans with no user");
    }

    [Fact]
    [Trait("Rule", "page.scan-development")]
    public async Task Outside_Development_the_scan_does_nothing()
    {
        await using var app = await SiteAsync(environment: "Production");

        (await ScanOf(app)).Should().BeNull();
        File.Exists(ManifestPath).Should().BeFalse();
    }

    [Fact]
    [Trait("Rule", "page.scan-browser")]
    public async Task Without_a_browser_the_scan_does_not_start_and_downloads_nothing()
    {
        await using var app = await SiteAsync(scanner: o => o.BrowserPath = Path.Combine(_folder, "no-browser.exe"));

        (await ScanOf(app)).Should().BeNull();
        File.Exists(ManifestPath).Should().BeFalse();
    }

    [Fact]
    [Trait("Rule", "page.manifest-excluded")]
    public void An_element_the_scan_reads_is_kept_in_the_formats_shape()
    {
        var excluded = MentorUiManifest.FromScan("INPUT", null, "Card", "text", null, null, "#c", [], "cc-number", false, false, false, out var left);
        excluded.Should().BeNull();
        left.Should().BeTrue();

        var long_ = new string('x', MentorUiManifest.MaxTextLength + 40);
        var kept = MentorUiManifest.FromScan("BUTTON", null, "Save\u0007 now " + long_, "submit",
            Enumerable.Range(0, MentorUiManifest.MaxOptions + 5).Select(i => "o" + i), "order​", "#save", ["Manager", "Manager"],
            null, false, false, false, out left);
        left.Should().BeFalse();
        kept!.Tag.Should().Be("button");
        kept.Label!.Length.Should().BeLessThanOrEqualTo(MentorUiManifest.MaxTextLength);
        kept.Label.Should().StartWith("Save  now", "a control character becomes a space");
        kept.Options.Should().HaveCount(MentorUiManifest.MaxOptions);
        kept.Form.Should().Be("order", "a format character becomes a space, and the text is trimmed");
        kept.Roles.Should().Equal("Manager");

        MentorUiManifest.FromScan("my tag", null, null, null, null, null, "#x", [], null, false, false, false, out _)
            .Should().BeNull("a tag of another shape");
        MentorUiManifest.FromScan("a", null, null, null, null, null, "", [], null, false, false, false, out _)
            .Should().BeNull("an element without a selector");
    }

    [Fact]
    [Trait("Rule", "page.scan")]
    public void What_the_scan_writes_the_reader_reads_back_the_same()
    {
        var pages = new[]
        {
            new MentorUiManifest.Page("/orders", new DateTimeOffset(2026, 11, 17, 10, 0, 0, TimeSpan.Zero),
            [
                new MentorUiManifest.Element("select", null, "State", null, ["Open", "Closed"], "order", "#state", []),
                new MentorUiManifest.Element("button", "tab", "Details", "button", [], null, "#details", ["Manager"]),
            ]),
            new MentorUiManifest.Page("/empty", null, []),
        };

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(MentorUiManifest.Serialize(pages)));
        var read = MentorUiManifest.Parse(stream);

        read.Problem.Should().BeNull();
        read.Pages.Should().BeEquivalentTo(pages, o => o.WithStrictOrdering());
    }

    /// <summary>
    /// As for the core's targets (BUG-120): the one build file of the package names the manifest's path, in a property
    /// the project can set and an assembly attribute, and holds nothing that runs: no target, no task, no error.
    /// </summary>
    [Fact]
    [Trait("Rule", "page.scan")]
    public void The_scanner_package_ships_only_the_build_file_that_names_the_manifest_and_it_cannot_stop_a_build()
    {
        var folder = Path.Combine(RepoFiles.Root, RepoFiles.Scanner);
        var packed = XDocument.Load(Path.Combine(folder, RepoFiles.Scanner + ".csproj")).Descendants("None")
            .Where(n => (string?)n.Attribute("Pack") == "true")
            .Where(n => ((string?)n.Attribute("PackagePath") ?? "").StartsWith("build", StringComparison.OrdinalIgnoreCase))
            .Select(n => ((string?)n.Attribute("Include"), (string?)n.Attribute("PackagePath")));
        packed.Should().Equal([(@"build\Novior.MentorAgent.Scanner.targets", @"build\")]);
        Directory.Exists(Path.Combine(folder, "buildTransitive")).Should().BeFalse("the application references the scanner itself");
        Directory.EnumerateFileSystemEntries(Path.Combine(folder, "build")).Select(Path.GetFileName)
            .Should().Equal("Novior.MentorAgent.Scanner.targets");

        var project = XDocument.Load(Path.Combine(folder, "build", "Novior.MentorAgent.Scanner.targets")).Root!;
        project.Name.LocalName.Should().Be("Project");
        project.Attributes().Should().BeEmpty("no Sdk, no InitialTargets, no DefaultTargets");
        project.Elements().Select(e => e.Name.LocalName).Should().Equal(["PropertyGroup", "ItemGroup"], "nothing that runs");

        var property = project.Element("PropertyGroup")!.Elements().Should().ContainSingle().Subject;
        property.Name.LocalName.Should().Be("MentorUiManifest");
        property.Attributes().Select(a => (a.Name.LocalName, a.Value))
            .Should().Equal([("Condition", "'$(MentorUiManifest)' == ''")], "the project can set another path");
        property.Value.Should().Be(@"$(MSBuildProjectDirectory)\mentor-ui-manifest.json", "the same default as the core's targets");

        var items = project.Element("ItemGroup")!.Elements().ToList();
        items.Should().OnlyContain(i => i.Name.LocalName == "AssemblyMetadata" && !i.HasElements);
        items.Select(i => string.Join(" | ", i.Attributes().Select(a => a.Name.LocalName + "=" + a.Value))).Should().Equal(
            "Include=" + MentorUiScanService.ManifestPathMetadata + " | Value=$(MentorUiManifest)",
            // Slice C: the fingerprints of the last scan, in the project's obj folder (page.scan-changed).
            "Include=" + MentorUiScanService.StatePathMetadata
            + " | Value=$([MSBuild]::NormalizePath('$(MSBuildProjectDirectory)', '$(BaseIntermediateOutputPath)', 'mentor-ui-scan.state.json'))");
    }

    [Fact]
    [Trait("Rule", "page.scan")]
    public void The_reader_of_the_page_keeps_at_most_the_formats_elements()
    {
        var many = JsonSerializer.SerializeToElement(Enumerable.Range(0, MentorUiManifest.MaxElementsPerPage + 10)
            .Select(i => new { element = "button", label = "B" + i, selector = "#b" + i }));

        MentorUiScanService.Read(many, [], out var excluded).Should().HaveCount(MentorUiManifest.MaxElementsPerPage);
        excluded.Should().Be(0);
    }

    // ---- Slice B: the scan's user and its clicks ----

    private static MentorUiManifest.Result ManifestOf(string path)
    {
        using var file = File.OpenRead(path);
        return MentorUiManifest.Parse(file);
    }

    [BrowserFact]
    [Trait("Rule", "page.scan-clicks")]
    public async Task The_scan_clicks_only_what_opens_a_tab_a_menu_or_a_section_and_never_a_delete()
    {
        await using var app = await SiteAsync();

        var result = await ScanOf(app);

        result.Should().NotBeNull();
        var hits = app.Services.GetRequiredService<ScanClickLog>().Hits.ToArray();
        hits.Should().BeEquivalentTo(ScanClicksPage.Clicked,
            "tabs, a collapsed section and a summary, each once; a confirm() inside a click answers no");
        hits.Should().NotContain(ScanClicksPage.NeverClicked,
            "never a delete, a submit, a link to another page, a button of a form that posts, a skipped or a plain button");
        result!.Clicks.Should().Be(ScanClicksPage.Clicked.Length);

        var page = ManifestOf(ManifestPath).Pages.Single(p => p.Route == "/scan/clicks").Elements;
        page.Should().ContainSingle(e => e.Selector == "#export" && e.Label == "Export",
            "the button the second tab shows is read after its click");
        page.Should().Contain(e => e.Selector == "#actions", "what the scan does not click it still reads");
        page.Should().NotContain(e => e.Selector == "#roster", "a click that leaves the page ends the clicks: the next page is not read as this one");
    }

    [BrowserFact]
    [Trait("Rule", "page.scan-user")]
    public async Task The_scan_reads_with_no_user_then_as_its_own_user_with_each_role()
    {
        await using var app = await SiteAsync(scanner: o => o.Roles = ["Manager", " ", "Manager"]);

        var result = await ScanOf(app);

        result.Should().NotBeNull();
        result!.Failed.Should().NotContain("/scan/staff", "the scan's Manager opens the page only a Manager may open");
        var gate = app.Services.GetRequiredService<MentorUiScanGate>();
        gate.Sessions.Should().Be(1, "one pass with a user: blank and repeated roles are not passes");
        gate.Active.Should().BeFalse("the scan's user ends with the scan");

        var pages = ManifestOf(ManifestPath).Pages;
        pages.Single(p => p.Route == "/scan/staff").Elements.Should().ContainSingle(e => e.Selector == "#credit")
            .Which.Roles.Should().Equal("Manager");
        var team = pages.Single(p => p.Route == "/scan/team").Elements;
        team.Single(e => e.Selector == "#roster").Roles.Should().BeEmpty("seen with no user: it needs no role");
        team.Single(e => e.Selector == "#payroll").Roles.Should().Equal("Manager");
        pages.Single(p => p.Route == "/scan/form").Elements.Should().OnlyContain(e => e.Roles.Count == 0);
    }

    [BrowserFact]
    [Trait("Rule", "page.scan-user")]
    public async Task The_scan_user_stops_working_when_the_scan_ends_early()
    {
        var app = await SiteAsync(scanner: o => o.Roles = ["Manager"]);
        var gate = app.Services.GetRequiredService<MentorUiScanGate>();
        try
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
            while (gate.Sessions == 0 && DateTime.UtcNow < deadline) await Task.Delay(20);
            gate.Sessions.Should().Be(1, "the pass as Manager has started");
            gate.Active.Should().BeTrue();

            await app.StopAsync();   // the scan stops in the middle of the pass

            (await ScanOf(app)).Should().BeNull();
            gate.Active.Should().BeFalse("the cookie stops working however the scan ends");
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    [Fact]
    [Trait("Rule", "page.scan-user")]
    public async Task Outside_Development_the_door_of_the_scan_user_is_not_mapped()
    {
        static void Fallback(WebApplication app) => app.MapFallback(() => "the application");
        await using var production = await SiteAsync(environment: "Production", map: Fallback);
        await using var development = await SiteAsync(scanner: o => o.BrowserPath = Path.Combine(_folder, "no-browser.exe"), map: Fallback);
        using var client = new HttpClient();

        async Task<(HttpStatusCode, string)> StartAsync(WebApplication app)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, app.Urls.First() + MentorUiScanGate.StartPath);
            request.Headers.Add(MentorUiScanGate.TokenHeader, new string('A', 64));
            using var response = await client.SendAsync(request);
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        (await StartAsync(production)).Should().Be((HttpStatusCode.OK, "the application"),
            "outside Development the request reaches the application: there is no door");
        (await StartAsync(development)).Should().Be((HttpStatusCode.NotFound, ""), "in Development a token never issued opens nothing");
    }

    private static DefaultHttpContext Request(string path, IPAddress from, string? token = null, string? cookie = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = path;
        context.Connection.RemoteIpAddress = from;
        if (token is not null) context.Request.Headers[MentorUiScanGate.TokenHeader] = token;
        if (cookie is not null) context.Request.Headers.Cookie = MentorUiScanGate.CookieName + "=" + cookie;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string? SessionOf(HttpContext context)
        => context.Response.Headers.SetCookie.ToString() is { Length: > 0 } header
            ? header.Split(';')[0].Split('=', 2)[1]
            : null;

    [Fact]
    [Trait("Rule", "page.scan-user")]
    public async Task The_token_opens_the_door_once_and_from_this_machine_only()
    {
        var gate = new MentorUiScanGate();
        var middleware = new MentorUiScanMiddleware(gate);
        var reached = 0;
        RequestDelegate next = _ => { reached++; return Task.CompletedTask; };
        var token = gate.Issue(["Manager"]);

        var remote = Request(MentorUiScanGate.StartPath, IPAddress.Parse("10.0.0.5"), token);
        await middleware.InvokeAsync(remote, next);
        remote.Response.StatusCode.Should().Be(404, "from another machine");
        SessionOf(remote).Should().BeNull();

        var first = Request(MentorUiScanGate.StartPath, IPAddress.Parse("::ffff:127.0.0.1"), token);
        await middleware.InvokeAsync(first, next);
        first.Response.StatusCode.Should().Be(200);
        first.Response.Headers.SetCookie.ToString().Should().Contain("httponly").And.Contain("samesite=strict").And.Contain("path=/");
        SessionOf(first).Should().NotBeNullOrEmpty().And.NotBe(token);

        var again = Request(MentorUiScanGate.StartPath, IPAddress.Loopback, token);
        await middleware.InvokeAsync(again, next);
        again.Response.StatusCode.Should().Be(404, "the token does not work twice");
        SessionOf(again).Should().BeNull();

        var wrong = Request(MentorUiScanGate.StartPath, IPAddress.Loopback, gate.Issue(["Manager"]).ToLowerInvariant() + "0");
        await middleware.InvokeAsync(wrong, next);
        wrong.Response.StatusCode.Should().Be(404, "a token that is not the one issued");
        reached.Should().Be(0, "the door never reaches the application");
        gate.Sessions.Should().Be(1);
    }

    [Fact]
    [Trait("Rule", "page.scan-user")]
    public async Task The_cookie_is_the_scan_user_from_this_machine_until_the_scan_ends()
    {
        var gate = new MentorUiScanGate();
        var middleware = new MentorUiScanMiddleware(gate);
        var token = gate.Issue(["Manager", "Clerk"]);
        var start = Request(MentorUiScanGate.StartPath, IPAddress.Loopback, token);
        await middleware.InvokeAsync(start, _ => Task.CompletedTask);
        var session = SessionOf(start)!;

        async Task<System.Security.Claims.ClaimsPrincipal> UserAsync(IPAddress from, string? cookie)
        {
            var context = Request("/orders", from, cookie: cookie);
            System.Security.Claims.ClaimsPrincipal? seen = null;
            await middleware.InvokeAsync(context, c => { seen = c.User; return Task.CompletedTask; });
            return seen!;
        }

        var user = await UserAsync(IPAddress.IPv6Loopback, session);
        user.Identity!.IsAuthenticated.Should().BeTrue();
        user.Identity.Name.Should().Be(MentorUiScanGate.UserName);
        user.IsInRole("Manager").Should().BeTrue();
        user.IsInRole("Clerk").Should().BeTrue();

        (await UserAsync(IPAddress.Parse("192.168.1.20"), session)).Identity!.IsAuthenticated.Should().BeFalse("from another machine");
        (await UserAsync(IPAddress.Loopback, token)).Identity!.IsAuthenticated.Should().BeFalse("the token is not the cookie");
        (await UserAsync(IPAddress.Loopback, null)).Identity!.IsAuthenticated.Should().BeFalse();

        gate.End();
        (await UserAsync(IPAddress.Loopback, session)).Identity!.IsAuthenticated.Should().BeFalse("the scan has ended");
        gate.Active.Should().BeFalse();
    }

    [BrowserFact]
    [Trait("Rule", "page.scan-browser")]
    public async Task The_scan_browser_is_noted_in_its_profile_and_on_Windows_closes_with_its_job_when_the_application_ends()
    {
        await using var browser = await CdpBrowser.StartAsync(CdpBrowser.Find(null)!, CancellationToken.None);
        var note = File.ReadAllLines(Path.Combine(browser.Profile!, ScanLeftovers.NoteName));
        note.Should().HaveCount(4);
        (note[0], note[2]).Should().Be((Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            browser.ProcessId!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)), "the next scan knows whose browser it is");

        if (!OperatingSystem.IsWindows()) return;   // no job objects elsewhere: the next scan closes it (below)
        browser.Job.Should().NotBeNull("the browser runs in a job of its own");
        using var process = Process.GetProcessById(browser.ProcessId!.Value);
        // The wait is on a handle of this process, opened while it runs. A Process taken by id opens it only when waited
        // on: once the job has closed the browser (in a few milliseconds) it looks the id up again among all the machine's
        // processes, which took up to six seconds with the machine busy, and could find another process given the same id.
        _ = process.SafeHandle;

        // What Windows does when the application ends in any way, also killed: it closes the application's handles.
        browser.Job!.Dispose();

        process.WaitForExit(10_000).Should().BeTrue("the browser, and its DevTools port, close with the job");
    }

    [BrowserFact]
    [Trait("Rule", "page.scan-browser")]
    public async Task Disposed_without_stopping_the_application_still_waits_for_the_scan_to_close_its_browser()
    {
        var app = await SiteAsync(scanner: o => o.Roles = ["Manager"]);
        var scan = app.Services.GetRequiredService<MentorUiScanService>();
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        while (scan.BrowserProcessId is null && DateTime.UtcNow < deadline) await Task.Delay(10);
        using var process = Process.GetProcessById(scan.BrowserProcessId!.Value);
        _ = process.SafeHandle;   // HasExited reads this process, not whatever has its id later (see the test above)

        await app.DisposeAsync();   // no StopAsync: the scan is in the middle of the pages

        process.HasExited.Should().BeTrue("the scan closed its browser before the application was disposed");
        scan.Completion.IsCompleted.Should().BeTrue();
        (await scan.Completion).Should().BeNull("the scan was stopped");
    }

    [Fact]
    [Trait("Rule", "page.scan-browser")]
    public void The_next_scan_closes_the_browser_a_scan_left_when_its_application_died()
    {
        Directory.CreateDirectory(_folder);
        using var self = Process.GetCurrentProcess();
        var selfStart = self.StartTime.ToUniversalTime().Ticks;
        var gone = selfStart - TimeSpan.TicksPerHour;   // this id, another start: the application that had it has ended
        using var left = Sleeper();
        using var reused = Sleeper();
        using var running = Sleeper();
        try
        {
            string Profile(string name, Action<string>? note = null)
            {
                var path = Path.Combine(_folder, ScanLeftovers.Prefix + name);
                Directory.CreateDirectory(path);
                note?.Invoke(path);
                return path;
            }
            static long Start(Process p) => p.StartTime.ToUniversalTime().Ticks;

            var leftover = Profile("left", p => ScanLeftovers.Note(p, self.Id, gone, left.Id, Start(left)));
            var idReused = Profile("reused", p => ScanLeftovers.Note(p, self.Id, gone, reused.Id, Start(reused) - TimeSpan.TicksPerHour));
            var inUse = Profile("in-use", p => ScanLeftovers.Note(p, self.Id, selfStart, running.Id, Start(running)));
            var starting = Profile("starting");
            var old = Profile("old");
            Directory.SetLastWriteTimeUtc(old, DateTime.UtcNow - ScanLeftovers.Grace - TimeSpan.FromMinutes(1));
            var other = Path.Combine(_folder, "another-folder");
            Directory.CreateDirectory(other);

            ScanLeftovers.CleanUp(_folder).Should().Be((1, 3));

            left.WaitForExit(10_000).Should().BeTrue("the browser of an application that is gone is closed");
            Directory.Exists(leftover).Should().BeFalse();
            reused.HasExited.Should().BeFalse("a process that took the id of the browser is someone else's");
            Directory.Exists(idReused).Should().BeFalse();
            running.HasExited.Should().BeFalse("its application still runs: that scan is not over");
            Directory.Exists(inUse).Should().BeTrue();
            Directory.Exists(starting).Should().BeTrue("a browser may be starting there now");
            Directory.Exists(old).Should().BeFalse();
            Directory.Exists(other).Should().BeTrue("only the scan's profiles");
        }
        finally
        {
            foreach (var p in new[] { left, reused, running })
                if (!p.HasExited) p.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    [Trait("Rule", "page.scan-parameters")]
    public void A_route_parameter_takes_its_value_escaped_and_never_out_of_the_pages_path()
    {
        MentorUiScanRoutes.Key("/orders/{id:int}").Should().Be("/orders/{id}", "a configuration key cannot hold a colon");
        MentorUiScanRoutes.Key("/files/{*path}").Should().Be("/files/{path}");
        MentorUiScanRoutes.Key("/report/{year:int?}").Should().Be("/report/{year}");

        static Dictionary<string, string> Values(string name, string value) => new() { [name] = value };
        MentorUiScanRoutes.Fill("/scan/form", null).Should().Be("/scan/form");
        MentorUiScanRoutes.Fill("/orders/{id:int}", Values("id", "7")).Should().Be("/orders/7");
        MentorUiScanRoutes.Fill("/orders/{id:int}", null).Should().BeNull("no value: the page stays to scan");
        MentorUiScanRoutes.Fill("/orders/{id:int}", Values("other", "7")).Should().BeNull();
        MentorUiScanRoutes.Fill("/orders/{id}", Values("id", "../admin")).Should().Be("/orders/..%2Fadmin", "escaped, it stays one segment");
        MentorUiScanRoutes.Fill("/orders/{id}", Values("id", "a b?c#d")).Should().Be("/orders/a%20b%3Fc%23d");
        MentorUiScanRoutes.Fill("/orders/{id}", Values("id", "..")).Should().BeNull("the browser would read it as a step up");
        MentorUiScanRoutes.Fill("/orders/{id}", Values("id", ".")).Should().BeNull();
        MentorUiScanRoutes.Fill("/orders/{id}", Values("id", "a\nb")).Should().BeNull();
        MentorUiScanRoutes.Fill("/orders/{id}", Values("id", new string('1', MentorUiScanRoutes.MaxValueLength + 1))).Should().BeNull();
        MentorUiScanRoutes.Fill("/orders/{id}", Values("id", new string('1', MentorUiScanRoutes.MaxValueLength)))
            .Should().Be("/orders/" + new string('1', MentorUiScanRoutes.MaxValueLength));
        MentorUiScanRoutes.Fill("/report/{year:int?}", null).Should().Be("/report", "an optional parameter may have no value");
        MentorUiScanRoutes.Fill("/report/{year:int?}/totals", Values("year", "2026")).Should().Be("/report/2026/totals");
        MentorUiScanRoutes.Fill("/list/{page:int=1}", null).Should().Be("/list/1", "the default");
        MentorUiScanRoutes.Fill("/files/{*path}", Values("path", "a/b c")).Should().Be("/files/a/b%20c");
        MentorUiScanRoutes.Fill("/files/{*path}", Values("path", "a/../b")).Should().Be("/files", "a step up is not used, not even in a catch-all");
    }

    [Fact]
    [Trait("Rule", "page.scan-changed")]
    public void The_fingerprint_of_a_page_is_the_code_that_renders_it_and_its_layouts()
    {
        MentorUiScanState.Rendered(typeof(ScanLayoutPage)).Should().Equal(typeof(ScanLayoutPage), typeof(ScanInnerLayout), typeof(ScanOuterLayout));
        MentorUiScanState.Rendered(typeof(ScanCycleA)).Should().Equal([typeof(ScanCycleA), typeof(ScanCycleB)], "a layout that comes back is read once");
        MentorUiScanState.RenderingMethods(typeof(ScanLayoutPage)).Select(m => m.Name).Should().Contain("BuildRenderTree")
            .And.Contain(n => n.StartsWith("<BuildRenderTree>", StringComparison.Ordinal), "the code of its render fragment too");

        static string? Print(Type? page = null, string route = "/r/{id}", string path = "/r/1", string[]? roles = null, string[]? scripts = null)
            => MentorUiScanState.Fingerprint(page ?? typeof(ScanLayoutPage), route, path, roles ?? [], scripts ?? ["reader"]);
        var print = Print();
        print.Should().MatchRegex("^[0-9a-f]{64}$").And.Be(Print(), "the same code, the same fingerprint");
        new[] { Print(typeof(ScanInnerLayout)), Print(route: "/q/{id}"), Print(path: "/r/2"), Print(roles: ["Manager"]), Print(scripts: ["reader 2"]) }
            .Should().OnlyHaveUniqueItems().And.NotContain(print);
        MentorUiScanState.Fingerprint(null, "/r", "/r", [], []).Should().BeNull("a page whose code is not found is read every time");
    }

    [BrowserFact]
    [Trait("Rule", "page.scan-parameters")]
    public async Task A_page_with_route_parameters_is_read_with_the_values_of_RouteParameters()
    {
        await using var app = await SiteAsync(scanner: o => o.RouteParameters["/scan/orders/{ID}"] = new Dictionary<string, string> { ["Id"] = "7" });
        var result = (await ScanOf(app))!;

        result.Waiting.Should().NotContain("/scan/orders/{id:int}");
        var page = ManifestOf(ManifestPath).Pages.Should().ContainSingle(p => p.Route == "/scan/orders/{id:int}", "the route as the router has it").Subject;
        page.Elements.Select(e => e.Label).Should().Contain("Print order 7", "the page was opened with the value");
    }

    [BrowserFact]
    [Trait("Rule", "page.scan-changed")]
    public async Task The_scan_reads_again_only_the_pages_that_changed_and_writes_the_manifest_only_when_it_says_something_new()
    {
        async Task<MentorUiScanResult> ScanAsync(Action<MentorUiScannerOptions>? scanner = null)
        {
            await using var app = await SiteAsync(scanner: scanner);
            return (await ScanOf(app))!;
        }

        var first = await ScanAsync();
        first.Written.Should().BeTrue();
        first.Kept.Should().Be(0);
        var statePath = Path.Combine(_folder, "obj", MentorUiScanState.FileName);
        var state = MentorUiScanState.Read(statePath);
        state.Keys.Should().BeEquivalentTo(ManifestOf(ManifestPath).Pages.Select(p => p.Route), "a fingerprint for each page in the manifest");
        var bytes = File.ReadAllBytes(ManifestPath);
        var then = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(ManifestPath, then);

        var second = await ScanAsync();
        second.Kept.Should().Be(first.Pages, "nothing changed: what the manifest says stays");
        second.Read.Should().Be(first.Failed.Count, "only the pages it could not read are tried again");
        second.Written.Should().BeFalse();

        // The code of one page changed.
        File.WriteAllText(statePath, MentorUiScanState.Serialize(state.Select(p => p.Key == "/scan/form" ? KeyValuePair.Create(p.Key, "changed") : p)));
        var third = await ScanAsync();
        third.Read.Should().Be(first.Failed.Count + 1);
        third.Written.Should().BeFalse("read again, the page says the same: it keeps the time it was read");
        MentorUiScanState.Read(statePath)["/scan/form"].Should().Be(state["/scan/form"], "the fingerprint of its code now");

        var full = await ScanAsync(o => o.Full = true);
        full.Read.Should().Be(first.Pages + first.Failed.Count, "Full reads every page");
        full.Kept.Should().Be(0);
        full.Written.Should().BeFalse();

        File.ReadAllBytes(ManifestPath).Should().Equal(bytes);
        File.GetLastWriteTimeUtc(ManifestPath).Should().Be(then, "a manifest that says the same is not written: nothing builds again");

        // A page the manifest no longer has is read again, whatever the state says.
        var pages = ManifestOf(ManifestPath).Pages;
        File.WriteAllText(ManifestPath, MentorUiManifest.Serialize(pages.Where(p => p.Route != "/scan/form")));
        var fourth = await ScanAsync();
        fourth.Read.Should().Be(first.Failed.Count + 1);
        fourth.Written.Should().BeTrue();
        ManifestOf(ManifestPath).Pages.Select(p => p.Route).Should().BeEquivalentTo(pages.Select(p => p.Route), "the page is back");
    }

    [BrowserFact]
    [Trait("Rule", "page.scan-changed")]
    public async Task With_every_page_read_and_nothing_changed_the_next_scan_starts_no_browser()
    {
        // Every page opens on this site: those of other tests too, and the Manager's with that role.
        static void Everything(WebApplication app) => app.MapFallback(() => new RazorComponentResult<ScanEmptyPage>());
        async Task<(MentorUiScanResult Result, int? Browser)> ScanAsync()
        {
            await using var app = await SiteAsync(scanner: o => o.Roles = ["Manager"], map: Everything);
            var result = (await ScanOf(app))!;
            return (result, app.Services.GetRequiredService<MentorUiScanService>().BrowserProcessId);
        }

        var (first, firstBrowser) = await ScanAsync();
        first.Failed.Should().BeEmpty("every page opens");
        firstBrowser.Should().NotBeNull();

        var (second, browser) = await ScanAsync();
        second.Read.Should().Be(0);
        second.Kept.Should().Be(first.Pages);
        second.Written.Should().BeFalse();
        browser.Should().BeNull("with nothing to read, no browser starts");
    }

    [Fact]
    [Trait("Rule", "page.scan-webview")]
    public void In_a_MAUI_app_the_scan_uses_the_WebViews_port_already_named_or_adds_a_free_one()
    {
        var asked = 0;
        int Free() { asked++; return 4567; }

        MentorUiScanService.WebViewArguments(null, Free).Should().Be(("--remote-debugging-port=4567", (int?)4567));
        MentorUiScanService.WebViewArguments("--disable-gpu ", Free).Should().Be(("--disable-gpu --remote-debugging-port=4567", (int?)4567),
            "what was there stays");
        asked.Should().Be(2);

        MentorUiScanService.WebViewArguments("--remote-debugging-port=9222 --disable-gpu", Free)
            .Should().Be(("--remote-debugging-port=9222 --disable-gpu", (int?)9222), "the port already named, never a second flag");
        MentorUiScanService.WebViewArguments("--remote-debugging-port=abc", Free).Port.Should().BeNull("not a port: no scan, and no second flag");
        MentorUiScanService.WebViewArguments("--remote-debugging-port", Free).Port.Should().BeNull();
        MentorUiScanService.WebViewArguments("--remote-debugging-port=70000", Free).Port.Should().BeNull();
        asked.Should().Be(2, "no free port is taken when one is named");
    }

    [Fact]
    [Trait("Rule", "page.scan-webview")]
    public async Task In_a_MAUI_app_the_scan_starts_in_a_Debug_build_only_and_never_in_a_web_host()
    {
        MentorUiScanService.IsDebugBuild(Built(debug: true)).Should().BeTrue();
        MentorUiScanService.IsDebugBuild(Built(debug: false)).Should().BeFalse("a Release build never scans, even with the call left in");
        MentorUiScanService.IsDebugBuild(null).Should().BeFalse();

        var before = Environment.GetEnvironmentVariable(MentorUiScanService.WebViewArgumentsVariable);
        var maui = new MentorUiScanService(Microsoft.Extensions.Options.Options.Create(new Novior.MentorAgent.Options.MentorOptions { AppName = "ScanSite", ScanAssemblies = [] }),
            Microsoft.Extensions.Options.Options.Create(new MentorUiScannerOptions()), new MentorUiScanGate(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MentorUiScanService>.Instance);
        maui.StartWebView(Built(debug: false)).Should().BeFalse();
        (await maui.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeNull();

        await using var web = await SiteAsync(environment: "Production");
        web.Services.GetRequiredService<MentorUiScanService>().StartWebView(Built(debug: true)).Should().BeFalse("a web host starts its scan by itself");
        web.Services.StartMentorUiScan();
        Environment.GetEnvironmentVariable(MentorUiScanService.WebViewArgumentsVariable).Should().Be(before, "no port was opened");
    }

    [BrowserFact]
    [Trait("Rule", "page.scan-webview")]
    public async Task In_a_WebView_the_scan_reads_the_pages_as_the_apps_user_and_returns_to_the_page_it_found()
    {
        // A headless Edge with a DevTools port stands for the app's WebView2: the scan attaches to it, as it does to the
        // WebView, and must neither close it nor leave it elsewhere.
        await using var app = await SiteAsync(environment: "Production");   // no scan of its own
        var start = app.Urls.First() + "/scan/form";
        var port = FreeLoopbackPort();
        var profile = Path.Combine(Path.GetTempPath(), "mentor-scan-webview-" + Guid.NewGuid().ToString("N"));
        var run = new ProcessStartInfo(CdpBrowser.Find(null)!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "--headless=new", $"--remote-debugging-port={port}", "--user-data-dir=" + profile, "--no-first-run",
                     "--no-default-browser-check", "--disable-sync", "about:blank" })
            run.ArgumentList.Add(argument);
        using var webView = Process.Start(run)!;
        using var job = BrowserJob.TryContain(webView);   // closes with this test process, whatever happens
        try
        {
            (await CdpBrowser.AttachAsync(port, TimeSpan.FromSeconds(3), CancellationToken.None))
                .Should().BeNull("about:blank, or a page of the browser's own, is not a page of the app");
            using (var http = new HttpClient())
                (await http.PutAsync($"http://127.0.0.1:{port}/json/new?{start}", null)).EnsureSuccessStatusCode();   // the app shows its page

            var result = await app.Services.GetRequiredService<MentorUiScanService>().ScanWebViewAsync(port, CancellationToken.None);

            result.Should().NotBeNull();
            var pages = ManifestOf(ManifestPath).Pages;
            pages.Single(p => p.Route == "/scan/clicks").Elements.Should().Contain(e => e.Selector == "#export", "it clicks as in a browser it starts");
            pages.SelectMany(p => p.Elements).Should().OnlyContain(e => e.Roles.Count == 0, "the app's own user: no roles");
            webView.HasExited.Should().BeFalse("the scan does not close a browser it did not start");
            (await PageAsync(port, start)).Should().Be(start, "back to the page it found");
        }
        finally
        {
            if (!webView.HasExited) webView.Kill(entireProcessTree: true);
            webView.WaitForExit(5000);
            try
            {
                Directory.Delete(profile, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>The address the browser behind <paramref name="port"/> shows, waiting a few seconds for <paramref name="expected"/>.</summary>
    private static async Task<string?> PageAsync(int port, string expected)
    {
        using var http = new HttpClient();
        string? url = null;
        for (var attempt = 0; attempt < 50 && url != expected; attempt++)
        {
            using var list = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json/list"));
            url = list.RootElement.EnumerateArray().Where(t => t.GetProperty("type").GetString() == "page")
                .Select(t => t.GetProperty("url").GetString())
                .FirstOrDefault(u => u?.StartsWith("http", StringComparison.Ordinal) == true);   // the browser's own pages aside
            if (url != expected) await Task.Delay(100);
        }
        return url;
    }

    private static int FreeLoopbackPort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>An assembly built as Debug (the JIT optimizer off) or as Release.</summary>
    private static System.Reflection.Assembly Built(bool debug)
    {
        var assembly = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(
            new System.Reflection.AssemblyName("ScanBuilt" + (debug ? "Debug" : "Release")), System.Reflection.Emit.AssemblyBuilderAccess.Run);
        var modes = debug
            ? DebuggableAttribute.DebuggingModes.Default | DebuggableAttribute.DebuggingModes.DisableOptimizations
            : DebuggableAttribute.DebuggingModes.IgnoreSymbolStoreSequencePoints;
        assembly.SetCustomAttribute(new System.Reflection.Emit.CustomAttributeBuilder(
            typeof(DebuggableAttribute).GetConstructor([typeof(DebuggableAttribute.DebuggingModes)])!, [modes]));
        return assembly;
    }

    [Fact]
    [Trait("Rule", "page.scan-tool")]
    public void The_command_line_of_novior_mentor_scan_is_checked_before_anything_runs()
    {
        static string? Problem(params string[] args)
        {
            MentorUiScanCommand.Parse(args, out var problem).Should().BeNull();
            return problem;
        }

        Problem().Should().Be("Usage:");
        Problem("--help").Should().Be("Usage:");
        Problem("build").Should().Contain("Unknown command 'build'");
        Problem("scan", "--project", "app.csproj").Should().Contain("--url");
        Problem("scan", "--url", "ftp://example.com", "--project", "app.csproj").Should().Contain("http or https");
        Problem("scan", "--url", "/relative", "--project", "app.csproj").Should().Contain("--url");
        Problem("scan", "--url", "https://test.example.com").Should().Contain("--project or --assembly");
        Problem("scan", "--url", "https://test.example.com", "--assembly", "app.dll").Should().Contain("--manifest is needed");
        Problem("scan", "--url", "https://test.example.com", "--project").Should().Contain("--project needs a value");
        Problem("scan", "--url", "https://test.example.com", "--project", "--full").Should().Contain("--project needs a value");
        Problem("scan", "--url", "https://test.example.com", "--project", "app.csproj", "--token", "x").Should().Contain("Unknown option '--token'");

        var request = MentorUiScanCommand.Parse(["scan", "--url", "https://test.example.com:8443/shop/orders?x=1", "--project", "app.csproj", "--full",
            "--parameters", "p.json", "--manifest", "m.json", "--browser", "edge.exe"], out _)!;
        request.Url.Should().Be(new Uri("https://test.example.com:8443/"), "the app's origin: the pages' routes are absolute");
        (request.Project, request.Manifest, request.Parameters, request.Browser, request.Full).Should().Be(("app.csproj", "m.json", "p.json", "edge.exe", true));
    }

    /// <summary>An address on this machine where nothing listens: a command that goes too far reaches no one.</summary>
    private const string Nowhere = "http://127.0.0.1:9";

    [Fact]
    [Trait("Rule", "page.scan-tool")]
    public async Task Without_its_app_built_or_with_a_wrong_command_line_novior_mentor_scan_stops_with_an_exit_code()
    {
        var error = new StringWriter();
        (await MentorUiScanCommand.RunAsync(["scan", "--url", Nowhere], NullLoggerFactory.Instance, error, CancellationToken.None))
            .Should().Be(MentorUiScanCommand.Usage);
        error.ToString().Should().Contain("--project or --assembly").And.Contain("novior-mentor scan --url");

        var project = Path.Combine(_folder, "Shop");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "Shop.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk.BlazorWebAssembly\" />");
        (await MentorUiScanCommand.RunAsync(["scan", "--url", Nowhere, "--project", project], NullLoggerFactory.Instance, error,
            CancellationToken.None)).Should().Be(MentorUiScanCommand.Failed, "not built: no dll to read the pages from");

        var parameters = Path.Combine(_folder, "parameters.json");
        File.WriteAllText(parameters, "[1, 2]");
        (await MentorUiScanCommand.RunAsync(["scan", "--url", Nowhere, "--assembly", typeof(ScannerTests).Assembly.Location,
            "--manifest", ManifestPath, "--parameters", parameters], NullLoggerFactory.Instance, error, CancellationToken.None))
            .Should().Be(MentorUiScanCommand.Failed, "the values of the route parameters are not an object of routes");
        File.Exists(ManifestPath).Should().BeFalse();
    }

    [Fact]
    [Trait("Rule", "page.scan-tool")]
    public void Novior_mentor_scan_reads_the_pages_from_the_newest_build_of_the_project()
    {
        var project = Path.Combine(_folder, "Shop");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "Shop.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk.BlazorWebAssembly\"><PropertyGroup><AssemblyName>Shop.Client</AssemblyName></PropertyGroup></Project>");
        string Dll(string folder, int minutesAgo)
        {
            var path = Path.Combine(project, folder, "Shop.Client.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-minutesAgo));
            return path;
        }

        Dll(Path.Combine("bin", "Debug", "net10.0"), 30);
        var newest = Dll(Path.Combine("bin", "Release", "net10.0"), 10);
        Dll(Path.Combine("bin", "Release", "net10.0", "ref"), 1);   // a reference assembly has no code
        Dll(Path.Combine("obj", "Release", "net10.0"), 0);
        File.WriteAllText(Path.Combine(project, "bin", "Release", "net10.0", "Shop.dll"), "");

        MentorUiScanCommand.Built(project).Should().Be(newest, "the assembly the project names, the newest build in bin");
        MentorUiScanCommand.Built(Path.Combine(project, "Shop.csproj")).Should().Be(newest);
        MentorUiScanCommand.Built(Path.Combine(_folder, "nothing")).Should().BeNull();
    }

    [BrowserFact]
    [Trait("Rule", "page.scan-tool")]
    public async Task Novior_mentor_scan_writes_the_same_manifest_as_the_scanner_of_the_app()
    {
        await using var app = await SiteAsync();
        (await ScanOf(app)).Should().NotBeNull();
        var byScanner = ManifestOf(ManifestPath).Pages;

        var byTool = Path.Combine(_folder, "tool", MentorUiScanService.ManifestFileName);
        (await MentorUiScanCommand.RunAsync(["scan", "--url", app.Urls.First(), "--assembly", typeof(ScannerTests).Assembly.Location,
            "--manifest", byTool], NullLoggerFactory.Instance, TextWriter.Null, CancellationToken.None)).Should().Be(MentorUiScanCommand.Done);

        static string Content(IEnumerable<MentorUiManifest.Page> pages)
            => MentorUiManifest.Serialize(pages.Select(p => p with { ScannedAt = null }));
        var pages = ManifestOf(byTool).Pages;
        pages.Should().NotBeEmpty();
        Content(pages).Should().Be(Content(byScanner), "the same pages, the same elements: only the time each was read differs");
        File.Exists(Path.Combine(_folder, "tool", "obj", MentorUiScanState.FileName)).Should().BeTrue("the state next to the manifest");
    }

    [Fact]
    [Trait("Rule", "page.scan-ci")]
    public void An_account_of_the_team_is_a_role_and_the_name_of_a_variable_never_a_cookie()
    {
        var request = MentorUiScanCommand.Parse(["scan", "--url", "https://test.example.com", "--project", "app.csproj",
            "--account", "Manager=SCAN_MANAGER", "--account", "Clerk=SCAN_CLERK", "--fail-on-change"], out _)!;
        request.Accounts.Should().Equal([("Manager", "SCAN_MANAGER"), ("Clerk", "SCAN_CLERK")]);
        request.FailOnChange.Should().BeTrue();

        foreach (var wrong in new[] { "Manager", "=SCAN", "Manager=", "Manager=1SCAN", "Manager=.AspNetCore.Cookies=CfDJ8abc", " =SCAN" })
        {
            MentorUiScanCommand.Parse(["scan", "--url", "https://test.example.com", "--project", "app.csproj", "--account", wrong], out var problem)
                .Should().BeNull(wrong);
            problem.Should().Contain("--account needs").And.NotContain("CfDJ8abc", "the command line never echoes what looks like a cookie");
        }
        MentorUiScanCommand.Parse(["scan", "--url", "https://test.example.com", "--project", "app.csproj",
            "--account", "Manager=A", "--account", "Manager=B"], out var twice).Should().BeNull();
        twice.Should().Contain("twice");

        MentorUiScanCommand.Cookies(".AspNetCore.Cookies=CfDJ8abc").Should().Equal([(".AspNetCore.Cookies", "CfDJ8abc")]);
        MentorUiScanCommand.Cookies(" a=1; b=2 ;").Should().Equal([("a", "1"), ("b", "2")]);
        foreach (var wrong in new[] { null, "", "  ", "a", "=1", "a=", "a=1; b", "a=1\u0001b" })
            MentorUiScanCommand.Cookies(wrong).Should().BeNull(wrong);
    }

    [Fact]
    [Trait("Rule", "page.scan-ci")]
    public async Task Without_the_accounts_cookie_novior_mentor_scan_stops_and_never_scans_with_no_user_in_its_place()
    {
        var logs = new ScanLogs();
        using var loggers = LoggerFactory.Create(b => b.AddProvider(logs));
        foreach (var held in new[] { null, "", "not a cookie" })
        {
            (await MentorUiScanCommand.RunAsync(["scan", "--url", Nowhere, "--assembly", typeof(ScannerTests).Assembly.Location,
                "--manifest", ManifestPath, "--account", "Manager=SCAN_MANAGER"], loggers, TextWriter.Null, CancellationToken.None,
                _ => held)).Should().Be(MentorUiScanCommand.Failed, held);
        }
        File.Exists(ManifestPath).Should().BeFalse("nothing was scanned");
        logs.Lines.Should().Contain(l => l.Contains("SCAN_MANAGER") && l.Contains("Manager"), "the error names the variable and the role")
            .And.NotContain(l => l.Contains("not a cookie"), "never what the variable holds");
    }

    [BrowserFact]
    [Trait("Rule", "page.scan-ci")]
    public async Task Novior_mentor_scan_reads_as_each_account_with_its_cookie_set_for_the_app_only_and_never_logged()
    {
        static void Login(WebApplication app) => app.MapGet("/scan/test-login", async (HttpContext context) =>
        {
            var identity = new System.Security.Claims.ClaimsIdentity(
                [new(System.Security.Claims.ClaimTypes.Name, "ci"), new(System.Security.Claims.ClaimTypes.Role, "Manager")], "Cookies");
            await Microsoft.AspNetCore.Authentication.AuthenticationHttpContextExtensions.SignInAsync(context, "Cookies",
                new System.Security.Claims.ClaimsPrincipal(identity));
            return Results.NoContent();
        });
        await using var app = await SiteAsync(environment: "Production", map: Login);   // no scan of its own, no door: a test environment
        using var http = new HttpClient(new HttpClientHandler { UseCookies = false });
        using var login = await http.GetAsync(app.Urls.First() + "/scan/test-login");
        var cookie = login.Headers.GetValues("Set-Cookie").Single().Split(';')[0];   // the team's account, signed in once
        var secret = cookie.Split('=', 2)[1];

        var logs = new ScanLogs();
        using var loggers = LoggerFactory.Create(b => b.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
        (await MentorUiScanCommand.RunAsync(["scan", "--url", app.Urls.First(), "--assembly", typeof(ScannerTests).Assembly.Location,
            "--manifest", ManifestPath, "--account", "Manager=SCAN_MANAGER"], loggers, TextWriter.Null, CancellationToken.None,
            name => name == "SCAN_MANAGER" ? cookie : null)).Should().Be(MentorUiScanCommand.Done);

        var pages = ManifestOf(ManifestPath).Pages;
        pages.Single(p => p.Route == "/scan/staff").Elements.Single(e => e.Selector == "#credit").Roles.Should().Equal("Manager");
        var team = pages.Single(p => p.Route == "/scan/team").Elements;
        team.Single(e => e.Selector == "#roster").Roles.Should().BeEmpty("seen with no user too");
        team.Single(e => e.Selector == "#payroll").Roles.Should().Equal("Manager");
        logs.Lines.Should().NotBeEmpty().And.NotContain(l => l.Contains(secret), "the cookie never reaches the log");
    }

    [BrowserFact]
    [Trait("Rule", "page.scan-tool")]
    [Trait("Rule", "page.scan-ci")]
    public async Task On_a_WebAssembly_standalone_app_with_fail_on_change_a_new_element_fails_the_pipeline_and_writes_nothing()
    {
        var extra = false;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production", ContentRootPath = AppContext.BaseDirectory });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        await using var standalone = builder.Build();
        // The development server of a standalone app: one index.html for every address, and the page draws itself.
        standalone.MapFallback(() => Results.Content($$"""
            <!DOCTYPE html><html><body><div id="app">Loading...</div><script>
            const extra = {{(extra ? "true" : "false")}};
            setTimeout(() => {
              const app = document.getElementById('app');
              if (location.pathname === '/wasm/home')
                app.innerHTML = '<button type="button" id="start">Start</button>' + (extra ? '<button type="button" id="export">Export</button>' : '');
              else if (location.pathname === '/wasm/reports')
                app.innerHTML = '<a id="back" href="/wasm/home">Home</a>';
              else
                app.innerHTML = '<p>Not found</p>';
            }, 300);
            </script></body></html>
            """, "text/html"));
        await standalone.StartAsync();

        string[] Args(params string[] more) =>
            ["scan", "--url", standalone.Urls.First(), "--assembly", typeof(ScannerTests).Assembly.Location, "--manifest", ManifestPath, .. more];
        var logs = new ScanLogs();
        using var loggers = LoggerFactory.Create(b => b.AddProvider(logs));

        (await MentorUiScanCommand.RunAsync(Args(), loggers, TextWriter.Null, CancellationToken.None)).Should().Be(MentorUiScanCommand.Done);
        var pages = ManifestOf(ManifestPath).Pages;
        pages.Single(p => p.Route == "/wasm/home").Elements.Select(e => e.Selector).Should().Equal("#start");
        pages.Single(p => p.Route == "/wasm/reports").Elements.Select(e => e.Selector).Should().Equal("#back");
        var bytes = File.ReadAllBytes(ManifestPath);
        var statePath = Path.Combine(_folder, "obj", MentorUiScanState.FileName);
        var state = File.ReadAllBytes(statePath);

        (await MentorUiScanCommand.RunAsync(Args("--fail-on-change"), loggers, TextWriter.Null, CancellationToken.None))
            .Should().Be(MentorUiScanCommand.Done, "nothing changed");

        extra = true;   // a new element on one page
        (await MentorUiScanCommand.RunAsync(Args("--fail-on-change"), loggers, TextWriter.Null, CancellationToken.None))
            .Should().Be(MentorUiScanCommand.Changed);
        logs.Lines.Should().Contain(l => l.Contains("would change") && l.Contains("~ /wasm/home"));
        File.ReadAllBytes(ManifestPath).Should().Equal(bytes, "--fail-on-change writes nothing");
        File.ReadAllBytes(statePath).Should().Equal(state);
    }

    [BrowserFact]
    [Trait("Rule", "page.observe-repeated")]
    public async Task The_scan_reads_a_table_once_per_column_with_its_header_and_keeps_the_labels_of_a_list()
    {
        await using var app = await SiteAsync();
        (await ScanOf(app)).Should().NotBeNull();

        var elements = ManifestOf(ManifestPath).Pages.Single(p => p.Route == "/scan/table").Elements;
        elements.Select(e => e.Label).Should().Equal(["Add", "Customer", "Actions", "Orders", "Invoices"],
            "the rows are data: one element per column, with its header; out of a table the scan keeps its labels");
        elements.Where(e => e.Label is "Customer" or "Actions").Should().OnlyContain(e => e.Selector.StartsWith("body > table > tbody > tr > td:nth-child("),
            "a selector for every row, with no record's id");
        File.ReadAllText(ManifestPath).Should().NotContainAny(["Lovelace", "Hopper", "o-1"]);
    }

    [BrowserFact]
    [Trait("Rule", "page.scan")]
    [Trait("Rule", "page.scan-clicks")]
    public async Task MentorAgents_widget_and_what_CSS_hides_are_not_the_pages_and_are_never_clicked()
    {
        await using var app = await SiteAsync();
        (await ScanOf(app)).Should().NotBeNull();

        ManifestOf(ManifestPath).Pages.Single(p => p.Route == "/scan/widget").Elements.Select(e => e.Selector)
            .Should().Equal(["#visible"], "the assistant's own commands are not the page's, and an element with no box is hidden");
        app.Services.GetRequiredService<ScanClickLog>().Hits.Should().NotContain(["assistant", "buried"]);
    }

    [BrowserFact]
    [Trait("Rule", "page.scan-ci")]
    public async Task With_two_accounts_the_second_pass_never_reads_with_the_first_ones_session()
    {
        // Two sign-ins with cookies of their own names: the site takes the Manager's when it has it. If the scan kept
        // the first account's cookie, the Staff pass would read the pages as the Manager.
        static void Schemes(IServiceCollection services)
        {
            services.AddAuthentication().AddCookie("Staff", o =>
                {
                    o.Cookie.Name = ".Scan.Staff";
                    o.LoginPath = "/scan/empty";
                })
                .AddPolicyScheme("Either", null, o => o.ForwardDefaultSelector = context =>
                    context.Request.Cookies.ContainsKey(".AspNetCore.Cookies") ? "Cookies" : "Staff");
            services.Configure<Microsoft.AspNetCore.Authentication.AuthenticationOptions>(o =>
            {
                o.DefaultScheme = "Either";
                o.DefaultChallengeScheme = "Cookies";
            });
        }
        static void Login(WebApplication app) => app.MapGet("/scan/test-login/{scheme}/{role}", async (string scheme, string role, HttpContext context) =>
        {
            var identity = new System.Security.Claims.ClaimsIdentity(
                [new(System.Security.Claims.ClaimTypes.Name, "ci-" + role), new(System.Security.Claims.ClaimTypes.Role, role)], scheme);
            await Microsoft.AspNetCore.Authentication.AuthenticationHttpContextExtensions.SignInAsync(context, scheme,
                new System.Security.Claims.ClaimsPrincipal(identity));
            return Results.NoContent();
        });
        await using var app = await SiteAsync(environment: "Production", map: Login, services: Schemes);
        using var http = new HttpClient(new HttpClientHandler { UseCookies = false });
        async Task<string> CookieAsync(string scheme, string role)
        {
            using var login = await http.GetAsync($"{app.Urls.First()}/scan/test-login/{scheme}/{role}");
            return login.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        }
        var manager = await CookieAsync("Cookies", "Manager");
        var staff = await CookieAsync("Staff", "Staff");

        (await MentorUiScanCommand.RunAsync(["scan", "--url", app.Urls.First(), "--assembly", typeof(ScannerTests).Assembly.Location,
            "--manifest", ManifestPath, "--account", "Manager=SCAN_MANAGER", "--account", "Staff=SCAN_STAFF"], NullLoggerFactory.Instance,
            TextWriter.Null, CancellationToken.None, name => name switch { "SCAN_MANAGER" => manager, "SCAN_STAFF" => staff, _ => null }))
            .Should().Be(MentorUiScanCommand.Done);

        var pages = ManifestOf(ManifestPath).Pages;
        pages.Single(p => p.Route == "/scan/staff").Elements.Single(e => e.Selector == "#credit").Roles
            .Should().Equal(["Manager"], "the Staff pass starts with no cookie of the Manager's");
        pages.Single(p => p.Route == "/scan/team").Elements.Single(e => e.Selector == "#payroll").Roles.Should().Equal("Manager");
    }

    // ── Story 1641, slice B: a front end that this host does not serve ─────────

    /// <summary>
    /// A single-page front end on a port of its own, as React's or Angular's development server: one document for every
    /// path, its page drawn by its script from the path, as a router draws it.
    /// </summary>
    private static async Task<WebApplication> FrontEndAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.MapFallback(() => Results.Content(FrontEndPage, "text/html"));
        await app.StartAsync();
        return app;
    }

    private const string FrontEndPage = """
        <!DOCTYPE html><html><body><div id="app"></div>
        <script>
        const views = {
          '/': '<h1>Home</h1><nav><a href="/orders">Orders</a> <a href="/about?tab=2#top">About</a> <a href="/orders/7">Order 7</a>'
             + ' <a href="https://elsewhere.test/x">Out</a> <span data-mentor-exclude><a href="/secret">Secret</a></span>'
             + ' <a href="/old">Old</a></nav>',
          '/orders': '<h1>Orders</h1><input id="customer" aria-label="Customer"><button type="button" id="save">Save</button><a href="/">Home</a>',
          '/about': '<h1>About</h1><a href="/orders">Orders</a>',
          '/orders/7': '<h1>Order 7</h1><button type="button" id="print-order">Print</button>',
          '/secret': '<h1>Secret</h1><button type="button" id="secret">Secret</button>',
        };
        if (location.pathname === '/old') location.replace('/about');   // a route the router sends elsewhere
        document.getElementById('app').innerHTML = views[location.pathname] || '<h1>Not found</h1>';
        </script></body></html>
        """;

    [BrowserFact]
    [Trait("Rule", "page.scan-front-end")]
    public async Task The_scan_reads_the_pages_of_a_front_end_that_its_links_reach()
    {
        await using var front = await FrontEndAsync();
        await using var app = await SiteAsync(scanner: o => o.FrontEndUrl = new Uri(front.Urls.First()));

        var result = await ScanOf(app);

        result.Should().NotBeNull();
        result!.Failed.Should().BeEmpty();
        var manifest = ManifestOf(ManifestPath);
        manifest.Pages.Select(p => p.Route).Should().BeEquivalentTo(["/", "/orders", "/about", "/orders/7"],
            "the front end's pages its links reach, without query or fragment; not the host's own pages, not another origin's, "
            + "not a link inside data-mentor-exclude, not one the front end sends elsewhere");
        manifest.Pages.Single(p => p.Route == "/orders").Elements.Should().Contain(e => e.Selector == "#customer")
            .And.Contain(e => e.Selector == "#save");
        manifest.Pages.Single(p => p.Route == "/orders/7").Elements.Should().Contain(e => e.Selector == "#print-order");
    }

    [BrowserFact]
    [Trait("Rule", "page.scan-front-end")]
    public async Task The_scan_reads_the_routes_of_the_front_end_it_is_given_with_no_user()
    {
        await using var front = await FrontEndAsync();
        var logs = new ScanLogs();
        await using var app = await SiteAsync(scanner: o =>
        {
            o.FrontEndUrl = new Uri(front.Urls.First());
            o.FrontEndRoutes = ["/orders", "about", "/orders/:id", "**", "bad route"];
            o.RouteParameters["/orders/{id}"] = new Dictionary<string, string> { ["id"] = "7" };
            o.Roles = ["Manager"];
        }, services: s => s.AddSingleton<ILoggerProvider>(logs));

        var result = await ScanOf(app);

        result.Should().NotBeNull();
        var manifest = ManifestOf(ManifestPath);
        manifest.Pages.Select(p => p.Route).Should().BeEquivalentTo(["/orders", "/about", "/orders/{id}"],
            "':id' is a parameter, opened with its value; a catch-all is never opened, and a route with white space is not one");
        manifest.Pages.Single(p => p.Route == "/orders/{id}").Elements.Should().Contain(e => e.Selector == "#print-order");
        manifest.Pages.SelectMany(p => p.Elements).Should().OnlyContain(e => e.Roles.Count == 0, "no pass signs in on a front end");
        logs.Lines.Should().Contain(l => l.Contains("MentorAgent:Scanner:Roles is not used") && l.Contains("page.scan-front-end"))
            .And.Contain(l => l.Contains("FrontEndRoutes has 1 route(s)"));
    }

    [BrowserFact]
    [Trait("Rule", "page.scan-front-end")]
    public async Task A_front_end_address_that_is_not_http_or_has_a_query_starts_no_scan()
    {
        var logs = new ScanLogs();
        await using var app = await SiteAsync(scanner: o => o.FrontEndUrl = new Uri("http://127.0.0.1:1/?page=2"),
            services: s => s.AddSingleton<ILoggerProvider>(logs));

        (await ScanOf(app)).Should().BeNull();
        File.Exists(ManifestPath).Should().BeFalse();
        logs.Lines.Should().Contain(l => l.Contains("FrontEndUrl is not an http or https address"));
    }

    /// <summary>What a command wrote to its log, each line formatted.</summary>
    private sealed class ScanLogs : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _lines = new();

        internal IReadOnlyList<string> Lines => [.. _lines];

        public ILogger CreateLogger(string categoryName) => new Logger(_lines);

        public void Dispose()
        {
        }

        private sealed class Logger(ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => lines.Enqueue(formatter(state, exception) + (exception is null ? "" : " " + exception));
        }
    }

    [Fact]
    [Trait("Rule", "page.scan-browser")]
    public void A_leftover_browser_is_checked_and_closed_on_one_process_found_once_by_its_id()
    {
        Directory.CreateDirectory(_folder);
        using var self = Process.GetCurrentProcess();
        var gone = self.StartTime.ToUniversalTime().Ticks - TimeSpan.TicksPerHour;   // the application that had this id ended
        using var left = Sleeper();
        var path = Path.Combine(_folder, ScanLeftovers.Prefix + "once");
        Directory.CreateDirectory(path);
        ScanLeftovers.Note(path, self.Id, gone, left.Id, left.StartTime.ToUniversalTime().Ticks);
        var found = new List<Process>();
        Process ById(int id)
        {
            var process = Process.GetProcessById(id);
            if (id == left.Id) found.Add(process);
            return process;
        }

        try
        {
            ScanLeftovers.CleanUp(_folder, ById).Should().Be((1, 1));

            found.Should().ContainSingle("the id is looked up once: a second look-up could find another process by then");
            left.WaitForExit(10_000).Should().BeTrue("the process checked is the process closed");
        }
        finally
        {
            if (!left.HasExited) left.Kill();
        }
    }

    /// <summary>A process that waits two minutes and does nothing: a stand-in for a browser.</summary>
    private static Process Sleeper()
    {
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("ping", "-n 120 127.0.0.1")
            : new ProcessStartInfo("sleep", "120");
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardOutput = true;
        return Process.Start(start)!;
    }
}
