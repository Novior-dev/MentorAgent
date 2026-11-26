using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Scanner.Cdp;
using Novior.MentorAgent.Tests.Docs;
using Novior.MentorAgent.Tests.Harness;
using Xunit;

namespace Novior.MentorAgent.Tests.Pages;

/// <summary>
/// Story 1640 (L6) — the observation in a real browser (Edge or Chrome, headless): the widget's own modules,
/// <c>MentorPageReader.js</c> and <c>MentorPageObserver.js</c>, on pages served as they are, with the application's end
/// played by the page (it keeps every message in <c>window.__reports</c>). What the user opens is sent, what MentorAgent
/// never lets out and every value stay in the browser, and past a limit the observation stops.
/// </summary>
public sealed class ObservationBrowserTests
{
    private static readonly string Scripts = Path.Combine(RepoFiles.Root, "Novior.MentorAgent.Abstractions", "wwwroot", "js");
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    /// <summary>A page that starts the observation, answering <c>window.__answer</c>, with quick timings.</summary>
    private static string PageOf(string body, int reports = 0) => $$"""
        <!DOCTYPE html><html><head><title>Observed</title></head><body>
        {{body}}
        <script type="module">
          import { start } from '/js/MentorPageObserver.js';
          window.__reports = [];
          window.__answer = true;
          start({ invokeMethodAsync: (name, path, elements, limit) => {
            window.__reports.push({ name, path, elements, limit, bytes: new TextEncoder().encode(JSON.stringify(elements)).length });
            return Promise.resolve(window.__answer);
          } }, { quiet: 100, gap: 100, reports: {{reports}} });
        </script>
        </body></html>
        """;

    private static async Task<WebApplication> SiteAsync(Dictionary<string, string> pages)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.MapGet("/js/{name}", (string name) => name is "MentorPageReader.js" or "MentorPageObserver.js"
            ? Results.Text(File.ReadAllText(Path.Combine(Scripts, name)), "text/javascript")
            : Results.NotFound());
        app.MapGet("/obs/{name}", (string name) => pages.TryGetValue(name, out var html) ? Results.Text(html, "text/html") : Results.NotFound());
        await app.StartAsync();
        return app;
    }

    private static async Task<(WebApplication App, CdpBrowser Browser)> OpenAsync(string name, string html)
    {
        var app = await SiteAsync(new() { [name] = html });
        var browser = await CdpBrowser.StartAsync(CdpBrowser.Find(null)!, CancellationToken.None);
        await browser.OpenAsync(new Uri(app.Urls.First() + "/obs/" + name), TimeSpan.FromSeconds(30), CancellationToken.None);
        return (app, browser);
    }

    private static async Task<JsonElement> Reports(CdpBrowser browser)
        => await browser.EvaluateAsync("JSON.stringify(window.__reports || [])", CancellationToken.None) is { ValueKind: JsonValueKind.String } s
            ? JsonDocument.Parse(s.GetString()!).RootElement
            : default;

    private static async Task<JsonElement> UntilAsync(CdpBrowser browser, Func<JsonElement, bool> done)
    {
        var until = DateTime.UtcNow + Wait;
        while (true)
        {
            var reports = await Reports(browser);
            if (reports.ValueKind == JsonValueKind.Array && done(reports)) return reports;
            if (DateTime.UtcNow > until) throw new TimeoutException("The page did not send what was waited for: " + reports);
            await Task.Delay(100);
        }
    }

    private static IEnumerable<JsonElement> Elements(JsonElement reports) => reports.EnumerateArray().SelectMany(r => r.GetProperty("elements").EnumerateArray());

    private static IEnumerable<string> Selectors(JsonElement reports) => Elements(reports).Select(e => e.GetProperty("selector").GetString()!);

    /// <summary>
    /// BUG-256 — the id of the one element each selector finds in the page, by the selector: an observed selector
    /// carries no id, so a test names the elements by what the selector finds. Null for a selector that finds none or many.
    /// </summary>
    private static async Task<Dictionary<string, string?>> IdsAsync(CdpBrowser browser, JsonElement reports)
    {
        var selectors = Selectors(reports).Distinct().ToList();
        var found = await browser.EvaluateAsync(
            "JSON.stringify(" + JsonSerializer.Serialize(selectors) + ".map(s => { const all = document.querySelectorAll(s); return all.length === 1 ? all[0].id || '' : null; }))",
            CancellationToken.None);
        var ids = JsonSerializer.Deserialize<string?[]>(found.GetString()!)!;
        return selectors.Zip(ids).ToDictionary(p => p.First, p => p.Second);
    }

    /// <summary>The reports once a selector the page sent finds the element with this id.</summary>
    private static async Task<JsonElement> UntilIdAsync(CdpBrowser browser, string id)
    {
        var until = DateTime.UtcNow + Wait;
        while (true)
        {
            var reports = await Reports(browser);
            if (reports.ValueKind == JsonValueKind.Array && (await IdsAsync(browser, reports)).ContainsValue(id)) return reports;
            if (DateTime.UtcNow > until) throw new TimeoutException($"The page did not send #{id}: " + reports);
            await Task.Delay(100);
        }
    }

    /// <summary>The selector the page sent for the element with this id.</summary>
    private static async Task<string> SelectorOfAsync(CdpBrowser browser, JsonElement reports, string id)
        => (await IdsAsync(browser, reports)).Single(p => p.Value == id).Key;

    private static PageObservedElement[] Sent(JsonElement reports)
        => Elements(reports).Select(e => JsonSerializer.Deserialize<PageObservedElement>(e.GetRawText(), JsonSerializerOptions.Web)!).ToArray();

    [BrowserFact]
    [Trait("Rule", "page.observe")]
    public async Task Opening_a_dialog_sends_its_elements_and_two_users_make_them_the_pages_as_observed()
    {
        var (app, browser) = await OpenAsync("dialog", PageOf("""
            <button type="button" id="open" onclick="document.getElementById('pay').showModal()">New payment</button>
            <dialog id="pay"><label>Amount <input id="amount" type="number" value="1234"></label>
              <button type="button" id="confirm">Confirm</button></dialog>
            """));
        await using var _ = app;
        await using var __ = browser;

        var first = await UntilIdAsync(browser, "open");
        (await IdsAsync(browser, first)).Values.Should().NotContain(["amount", "confirm"], "a closed dialog has no box: hidden");

        await browser.EvaluateAsync("document.getElementById('open').click()", CancellationToken.None);
        var all = await UntilIdAsync(browser, "confirm");
        var ids = await IdsAsync(browser, all);
        all.EnumerateArray().Last().GetProperty("elements").EnumerateArray().Select(e => ids[e.GetProperty("selector").GetString()!])
            .Should().BeEquivalentTo(["amount", "confirm"], "only what the page did not send yet");
        all.EnumerateArray().Should().OnlyContain(r => r.GetProperty("path").GetString() == "/obs/dialog");
        var amount = await SelectorOfAsync(browser, all, "amount");
        var confirm = await SelectorOfAsync(browser, all, "confirm");

        // The application's end: two users who saw the same dialog make its elements the page's, as observed.
        var options = new MentorOptions { AppName = "L6", ScanAssemblies = [] };
        options.AutoMentor.Enabled = true;
        options.AutoMentor.PageAssemblies = [typeof(ObservationBrowserTests).Assembly];
        var directory = new MentorPageDirectory(Microsoft.Extensions.Options.Options.Create(options), NullLogger<MentorPageDirectory>.Instance);
        directory.Observe("/l1/home", Sent(all), 1, 1);
        directory.Observe("/l1/home", Sent(all), 2, 1);
        directory.Pages().Single(p => p.Route == "/l1/home").Observed.Select(e => (e.Selector, e.Label))
            .Should().Contain([(amount, "Amount"), (confirm, "Confirm")]);
        var catalog = new MentorActionCatalog(null!, Microsoft.Extensions.Options.Options.Create(options), new EmptyServices(), pageDirectory: directory);
        catalog.GetPages().Single(p => p.Route == "/l1/home").Elements.Where(e => e.Selector == amount || e.Selector == confirm)
            .Should().HaveCount(2).And.OnlyContain(e => e.Coverage == MentorPageCoverage.Observed && e.Label == null);
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    [BrowserFact]
    [Trait("Rule", "page.observe-values")]
    [Trait("Rule", "page.observe-repeated")]
    public async Task No_value_and_no_text_of_a_row_or_a_list_ever_leaves_the_browser()
    {
        var (app, browser) = await OpenAsync("values", PageOf("""
            <form name="pay">
              <label>Name <input id="name" value="SECRET-NAME"></label>
              <label>Notes <textarea id="notes">SECRET-NOTES</textarea></label>
              <div contenteditable="true" id="editor" aria-label="Editor">SECRET-EDITOR</div>
              <label>Customer <select id="customer"><option>SECRET-OPT-A</option><option selected>SECRET-OPT-B</option></select></label>
              <input type="password" id="pwd" aria-label="Password" value="SECRET-PWD">
              <input id="card" autocomplete="cc-number" aria-label="Card" value="SECRET-CARD">
            </form>
            <table id="orders"><thead><tr><th>Customer</th><th>Total</th><th>Actions</th></tr></thead><tbody>
              <tr id="order-901"><td><a href="/c/901">SECRET-ROW Mario Rossi</a></td><td><input aria-label="SECRET-ROW total" value="SECRET-ROW-12"></td><td><button type="button">SECRET-ROW Delete</button></td></tr>
              <tr id="order-902"><td><a href="/c/902">SECRET-ROW Luigi Bianchi</a></td><td><input value="SECRET-ROW-13"></td><td><button type="button">SECRET-ROW Delete</button></td></tr>
            </tbody></table>
            <ul id="people"><li><a href="/p/1" id="person-1">SECRET-LIST Anna</a></li><li><a href="/p/2" id="person-2">SECRET-LIST Bruno</a></li></ul>
            <section><div class="card" id="card-77"><button type="button">SECRET-CARD Open Anna</button></div><div class="card" id="card-78"><button type="button">SECRET-CARD Open Bruno</button></div></section>
            <div role="feed"><article><a href="/n/1">SECRET-FEED one</a></article><article><a href="/n/2">SECRET-FEED two</a></article></div>
            <div class="bm-root"><button type="button">SECRET-WIDGET</button></div>
            <div data-mentor-exclude><button type="button">SECRET-EXCLUDED</button></div>
            """));
        await using var _ = app;
        await using var __ = browser;

        await UntilIdAsync(browser, "name");
        await Task.Delay(500);
        var reports = await Reports(browser);

        reports.GetRawText().Should().NotContain("SECRET", "no value, no option, no text of a row, a list, a card or a feed, nothing excluded");
        reports.GetRawText().Should().NotContainAny(["order-901", "person-1", "card-77", "/c/901"], "a record's id is not structure");
        var elements = Elements(reports).ToList();
        elements.Select(e => e.GetProperty("label").GetString()).Should().Contain(["Name", "Notes", "Editor", "Customer", "Total", "Actions"]);
        elements.Where(e => e.GetProperty("selector").GetString()!.Contains("tbody")).Select(e => e.GetProperty("label").GetString())
            .Should().BeEquivalentTo(["Customer", "Total", "Actions"], "a table is read once per column, with the column's header");
        elements.Where(e => e.GetProperty("selector").GetString()!.Contains("li > a")).Should().ContainSingle()
            .Which.GetProperty("label").ValueKind.Should().Be(JsonValueKind.Null, "a list repeats: no label of its own");
        (await IdsAsync(browser, reports)).Values.Should().NotContain(["pwd", "card"]);
    }

    [BrowserFact]
    [Trait("Rule", "page.observe-repeated")]
    [Trait("Rule", "page.observe-shared")]
    public async Task A_field_with_a_tied_label_keeps_it_in_a_repeated_wrapper_and_a_records_label_stays_with_its_one_user()
    {
        var (app, browser) = await OpenAsync("form", PageOf("""
            <form name="profile">
              <div class="mb-3"><label for="name" class="form-label">Name</label><input id="name" class="form-control" value="SECRET-NAME"></div>
              <div class="mb-3"><label for="email" class="form-label">Email</label><input id="email" type="email" class="form-control" value="SECRET-EMAIL"></div>
            </form>
            <ul id="pick"><li><input type="checkbox" id="c-1"><label for="c-1">RECORD Mario Rossi</label></li>
              <li><input type="checkbox" id="c-2"><label for="c-2">RECORD Luigi Bianchi</label></li></ul>
            <button type="button" aria-label="Help for Anna">?</button>
            """));
        await using var _ = app;
        await using var __ = browser;

        var reports = await UntilIdAsync(browser, "email");
        var ids = await IdsAsync(browser, reports);
        var sent = Elements(reports).Where(e => ids[e.GetProperty("selector").GetString()!] is { Length: > 0 })
            .ToDictionary(e => ids[e.GetProperty("selector").GetString()!]!, e => e.GetProperty("label").GetString());
        sent.Should().Contain("name", "Name").And.Contain("email", "Email", "a tied label is the form's, not a record's");
        reports.GetRawText().Should().NotContain("SECRET");
        Selectors(reports).Should().NotContain(s => s.Contains("aria-label"), "in observation a selector never carries a label");

        // A label tied to a record leaves the browser, but one user's never reaches the pages.
        var options = new MentorOptions { AppName = "L6", ScanAssemblies = [] };
        options.AutoMentor.Enabled = true;
        options.AutoMentor.PageAssemblies = [typeof(ObservationBrowserTests).Assembly];
        var directory = new MentorPageDirectory(Microsoft.Extensions.Options.Options.Create(options), NullLogger<MentorPageDirectory>.Instance);
        directory.Observe("/l1/home", Sent(reports), 1, 1);
        directory.Observe("/l1/home", Sent(reports).Where(e => !(e.Label ?? "").StartsWith("RECORD")).ToArray(), 2, 1);
        directory.Pages().Single(p => p.Route == "/l1/home").Observed.Select(e => e.Label).Should().BeEquivalentTo(["Name", "Email", "Help for Anna"],
            "the other user saw the form and the button, not the same record");
    }

    [BrowserFact]
    [Trait("Rule", "page.observe-private")]
    public async Task An_observed_selector_carries_no_id_name_test_id_class_or_label_of_the_page_and_still_finds_its_element()
    {
        // BUG-256: up to the fix the selector was the element's id (#customer-4711), its name, its test id, or a path from
        // the nearest ancestor with an id (#order-1002 > div > button): a record's, which two users on the same record
        // see the same, and which reached the catalogue's pages and whoever reads them.
        var (app, browser) = await OpenAsync("record", PageOf("""
            <main id="order-1002" class="order-1002">
              <div class="row-4711"><button type="button" id="customer-4711" name="MarioRossi" data-testid="save-4711" class="btn-4711">Save</button></div>
              <div><input id="email-4711" name="mario.rossi@example.com" data-testid="email-4711" aria-label="Mario Rossi's email"></div>
              <section data-mentor-id="ticket"><button type="button" id="in-ticket-4711" title="Mario Rossi">Open</button></section>
              <input id="title-4711" data-mentor-id="ticket.title" aria-label="Title">
            </main>
            """ + string.Concat(Enumerable.Repeat("<div>", 16)) + "<button type=\"button\" id=\"deep\">Deep</button>"
                + string.Concat(Enumerable.Repeat("</div>", 16))));
        await using var _ = app;
        await using var __ = browser;

        var reports = await UntilIdAsync(browser, "title-4711");
        await UntilIdAsync(browser, "in-ticket-4711");
        reports = await Reports(browser);

        var selectors = Selectors(reports).ToList();
        selectors.Should().HaveCount(4);
        string.Join("\n", selectors).Should().NotContainAny(["1002", "4711", "Mario", "mario", "#"],
            "nothing of the record: no id, no name, no test id, no class, no title, no label");
        selectors.Should().OnlyContain(s => MentorUiManifest.IsObservedSelector(s), "the server keeps what the browser sends");
        (await IdsAsync(browser, reports)).Values.Should().BeEquivalentTo(["customer-4711", "email-4711", "in-ticket-4711", "title-4711"],
            "each selector still finds its one element, and one deeper than 16 steps is not sent");
        selectors.Should().Contain("input[data-mentor-id=\"ticket\\.title\"]", "the developer's own name is kept")
            .And.Contain(s => s.StartsWith("section[data-mentor-id=\"ticket\"] > ", StringComparison.Ordinal),
                "a path starts at the nearest ancestor with a data-mentor-id");
    }

    [BrowserFact]
    [Trait("Rule", "page.observe-limit")]
    public async Task Past_its_elements_the_observation_sends_what_fits_in_small_messages_then_stops_and_says_so()
    {
        var buttons = string.Concat(Enumerable.Range(1, 600).Select(i => $"<x-b{i}><button type=\"button\" id=\"b{i}\">Button number {i}</button></x-b{i}>"));
        var (app, browser) = await OpenAsync("many", PageOf(buttons));
        await using var _ = app;
        await using var __ = browser;

        var reports = await UntilAsync(browser, r => r.EnumerateArray().Any(x => x.GetProperty("limit").ValueKind == JsonValueKind.String));
        reports.EnumerateArray().Sum(r => r.GetProperty("elements").GetArrayLength()).Should().Be(500, "a page sends at most 500 elements");
        reports.EnumerateArray().Should().HaveCountGreaterThan(1).And.OnlyContain(r => r.GetProperty("bytes").GetInt32() <= 24 * 1024,
            "a message stays well below SignalR's 32 KB");
        reports.EnumerateArray().Last().GetProperty("limit").GetString().Should().Be("elements");

        var count = reports.GetArrayLength();
        await browser.EvaluateAsync("document.body.insertAdjacentHTML('beforeend', '<x-late><button id=\"late\">Late</button></x-late>')", CancellationToken.None);
        await Task.Delay(1000);
        (await Reports(browser)).GetArrayLength().Should().Be(count, "stopped: nothing more is sent");
    }

    [BrowserFact]
    [Trait("Rule", "page.observe-limit")]
    public async Task Past_its_messages_or_when_the_application_says_no_the_observation_stops()
    {
        var (app, browser) = await OpenAsync("messages", PageOf("<button type=\"button\" id=\"first\">First</button>", reports: 3));
        await using var _ = app;
        await using var __ = browser;

        for (var i = 1; i <= 4; i++)
        {
            await UntilAsync(browser, r => r.GetArrayLength() >= Math.Min(i, 3));
            await browser.EvaluateAsync($"document.body.insertAdjacentHTML('beforeend', '<x-n{i}><button id=\"n{i}\">Next {i}</button></x-n{i}>')", CancellationToken.None);
            await Task.Delay(400);
        }
        var reports = await Reports(browser);
        reports.GetArrayLength().Should().Be(3);
        reports[2].GetProperty("limit").GetString().Should().Be("reports");

        // The application answers no (off, or past its own limit): the browser stops at once.
        await browser.OpenAsync(new Uri(app.Urls.First() + "/obs/messages"), TimeSpan.FromSeconds(30), CancellationToken.None);
        await UntilAsync(browser, r => r.GetArrayLength() == 1);
        await browser.EvaluateAsync("window.__answer = false; document.body.insertAdjacentHTML('beforeend', '<x-a><button id=\"a\">A</button></x-a>')", CancellationToken.None);
        await UntilAsync(browser, r => r.GetArrayLength() == 2);
        await browser.EvaluateAsync("document.body.insertAdjacentHTML('beforeend', '<x-c><button id=\"c\">C</button></x-c>')", CancellationToken.None);
        await Task.Delay(1000);
        (await Reports(browser)).GetArrayLength().Should().Be(2, "the application said no");
    }
}
