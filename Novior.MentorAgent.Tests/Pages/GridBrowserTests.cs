using System.Collections.Concurrent;
using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.QuickGrid;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Tests.Harness;
using Xunit;

namespace Novior.MentorAgent.Tests.Pages;

/// <summary>
/// Story 1319, slice B — grids registered with <c>RegisterGrid&lt;TRow&gt;</c>, used by the model's calls in a real
/// browser (Edge or Chrome, headless) on an interactive Blazor Server page: a table written by hand with its
/// <c>data-mentor-id</c>s, which marks only the header it is sorted by (ARIA lets it), and a virtualized
/// <c>QuickGrid</c>, whose rows and headers take no attribute, given its selectors. The page sorts after a round trip
/// longer than a quiet while, as a server does.
/// </summary>
[Trait("Rule", "grid.open")]
public sealed class GridBrowserTests
{
    private static async Task<string> ReadAsync(RecipeBrowserTests.Site site, string script)
        => (await site.Browser.EvaluateAsync(script, CancellationToken.None)).ToString();

    private static Task<string> Ids(RecipeBrowserTests.Site site, string grid)
        => ReadAsync(site, $"Array.from(document.querySelectorAll('[data-mentor-id=\"{grid}.cell.id\"]')).map(c => c.textContent.trim()).join()");

    private static async Task<int> OpenedAsync(RecipeBrowserTests.Site site)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!GridActionPage.Opened.ContainsKey(site.Run))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("No row was opened.");
            await Task.Delay(100);
        }
        return GridActionPage.Opened[site.Run];
    }

    [BrowserFact]
    [Trait("Rule", "grid.filter")]
    [Trait("Rule", "grid.sort")]
    public async Task The_model_sorts_filters_and_opens_a_row_of_a_table_written_by_hand()
    {
        await using var site = await RecipeBrowserTests.OpenAsync(null, "grids");
        site.Chat.CallTool("sort_tickets", new { parameter = new { column = "title", descending = true } })
            .CallTool("filter_tickets", new { parameter = "printer" })
            .CallTool("open_tickets_row", new { parameter = new { key = 5 } })
            .Say("Ticket 5 is open.");

        await RecipeBrowserTests.TurnAsync(site, "the printer tickets, by title from Z, then open 5");

        foreach (var action in new[] { "sort_tickets", "filter_tickets", "open_tickets_row" })
            site.Observer.Steps(action).Should().Equal(["Allowed/allowed", "Executed/completed"], action);
        GridActionPage.SortClicks[site.Run].Should().Be(2, "not sorted, then ascending, then descending: two clicks");
        (await ReadAsync(site, "document.querySelector('[data-mentor-id=\"tickets.sort.title\"]').getAttribute('aria-sort')"))
            .Should().Be("descending");
        (await OpenedAsync(site)).Should().Be(5,
            "5 was not among the printer tickets shown: its key was written in the filter, and its row opened");
        (await Ids(site, "tickets")).Should().Be("5");
    }

    [BrowserFact]
    [Trait("Rule", "grid.sort")]
    public async Task A_header_that_does_not_say_its_order_is_not_sorted_and_the_model_is_told_why()
    {
        await using var site = await RecipeBrowserTests.OpenAsync(null, "grids");
        site.Chat.CallTool("sort_tickets", new { parameter = new { column = "priority" } }).Say("Sorry.");

        await RecipeBrowserTests.TurnAsync(site, "by priority");

        site.Observer.Steps("sort_tickets").Should().Equal(["Allowed/allowed", "Failed/exception"]);
        GridActionPage.SortClicks[site.Run].Should().Be(1, "one click, after which the header still says nothing: no more");
    }

    [BrowserFact]
    [Trait("Rule", "grid.sort")]
    public async Task A_virtualized_QuickGrid_is_sorted_by_its_aria_sort_and_a_row_far_down_is_opened_by_scrolling()
    {
        await using var site = await RecipeBrowserTests.OpenAsync(null, "grids");
        site.Chat.CallTool("sort_orders", new { parameter = new { column = "id", descending = true } })
            .CallTool("open_orders_row", new { parameter = new { key = 120 } })
            .Say("Order 120 is open.");

        await RecipeBrowserTests.TurnAsync(site, "the last orders first, then open 120");

        site.Observer.Steps("sort_orders").Should().Equal(["Allowed/allowed", "Executed/completed"]);
        site.Observer.Steps("open_orders_row").Should().Equal(["Allowed/allowed", "Executed/completed"],
            "the model reads: " + string.Join(" | ", site.Chat.Calls.SelectMany(c => c.Messages).SelectMany(m => m.Contents)
                .OfType<Microsoft.Extensions.AI.FunctionResultContent>().Select(r => r.Result?.ToString())));
        (await ReadAsync(site, "document.querySelector('#orders th:nth-child(1)').getAttribute('aria-sort')")).Should().Be("descending");
        (await OpenedAsync(site)).Should().Be(120);
        (await ReadAsync(site, "String(document.querySelector('#orders').scrollTop > 0)")).Should().Be("true",
            "row 120 of the sorted grid is reached by scrolling");
    }

    [BrowserFact]
    [Trait("Rule", "recipe.row")]
    public async Task A_row_far_down_a_grid_whose_rows_come_later_than_a_quiet_while_is_still_reached_by_scrolling()
    {
        // BUG-252: under load the grid answered each scroll after the runner's quiet while, and the step scrolled past
        // the row: "No row of the grid 'orders' has the id '120'", two times in ten. Here every scroll is answered late.
        await using var site = await RecipeBrowserTests.OpenAsync(null, "grids");
        GridActionPage.RowsDelay[site.Run] = 600;
        site.Chat.CallTool("open_orders_row", new { parameter = new { key = 200 } }).Say("Order 200 is open.");

        await RecipeBrowserTests.TurnAsync(site, "open order 200");

        site.Observer.Steps("open_orders_row").Should().Equal(["Allowed/allowed", "Executed/completed"],
            "the model reads: " + string.Join(" | ", site.Chat.Calls.SelectMany(c => c.Messages).SelectMany(m => m.Contents)
                .OfType<Microsoft.Extensions.AI.FunctionResultContent>().Select(r => r.Result?.ToString())));
        (await OpenedAsync(site)).Should().Be(200);
    }

    [BrowserFact]
    [Trait("Rule", "grid.sort")]
    public async Task The_runners_sort_step_clicks_until_aria_sort_reads_the_order_and_names_why_it_could_not()
    {
        await using var site = await RecipeBrowserTests.OpenAsync(null, "grids");
        var runner = site.Circuit.GetRequiredService<IMentorRecipeRunner>();
        Task<MentorRecipeOutcome> Sort(string selector, string order)
            => runner.RunAsync([new("sort", selector, order)], CancellationToken.None);
        const string title = "[data-mentor-id=\"tickets.sort.title\"]";

        (await Sort("#nowhere", "ascending")).Should().Be(new MentorRecipeOutcome(false, 0, "missing"));
        (await Sort("#excluded-header", "ascending")).Should().Be(new MentorRecipeOutcome(false, 0, "excluded"));
        (await Sort(title, "ascending")).Should().Be(new MentorRecipeOutcome(true, 1, null));
        GridActionPage.SortClicks[site.Run].Should().Be(1);
        (await Sort(title, "ascending")).Should().Be(new MentorRecipeOutcome(true, 1, null));
        GridActionPage.SortClicks[site.Run].Should().Be(1, "already in the order asked: no click");
        (await Sort(title, "descending")).Should().Be(new MentorRecipeOutcome(true, 1, null));
        GridActionPage.SortClicks[site.Run].Should().Be(2);
        (await Ids(site, "tickets")).Should().Be("6,4,2,5,3,1", "the rows by title, from Z");

        // A header that never reaches the order asked: three clicks, then "not-sorted". It changes in its own click
        // handler, at once, which the step sees without waiting.
        (await Sort("#stuck", "ascending")).Should().Be(new MentorRecipeOutcome(false, 0, "not-sorted"));
        (await ReadAsync(site, "document.getElementById('stuck').dataset.clicks")).Should().Be("3");

        // A QuickGrid's header, by the button inside its th, which holds aria-sort.
        (await Sort("#orders th:nth-child(2) .col-title", "descending")).Should().Be(new MentorRecipeOutcome(true, 1, null));
        (await ReadAsync(site, "document.querySelector('#orders th:nth-child(2)').getAttribute('aria-sort')")).Should().Be("descending");
    }
}

/// <summary>
/// Two grids as an application writes them for <c>RegisterGrid</c>: tickets in a table written by hand, with its
/// <c>data-mentor-id</c>s, a filter over the title and the number, and headers that sort after a round trip, the sorted one
/// alone marked with <c>aria-sort</c> (the priority's never is); orders in a virtualized <c>QuickGrid</c>, in a container
/// that scrolls. It tells the test its circuit's services, the row opened and the clicks on the tickets' headers, under the
/// <c>run</c> of its address.
/// </summary>
public sealed class GridActionPage : ComponentBase, IDisposable
{
    internal static readonly ConcurrentDictionary<string, int> Opened = new();
    internal static readonly ConcurrentDictionary<string, int> SortClicks = new();

    /// <summary>BUG-252: how long the orders' grid takes to give its rows, in milliseconds, for a run: a busy server.</summary>
    internal static readonly ConcurrentDictionary<string, int> RowsDelay = new();

    public enum Priority { Low, Normal, High }

    public sealed record Ticket(int Id, string Title, Priority Priority);

    public sealed record Line(int Id, string Customer);

    private static readonly Ticket[] Tickets =
    [
        new(1, "Monitor flickers", Priority.Normal), new(2, "Printer jam", Priority.High), new(3, "Mouse broken", Priority.Low),
        new(4, "Printer offline", Priority.Normal), new(5, "Paper tray", Priority.Low), new(6, "VPN down", Priority.High),
    ];

    private static readonly IQueryable<Line> Lines = Enumerable.Range(1, 300).Select(i => new Line(i, $"Customer {i:000}")).AsQueryable();

    [Inject] private IServiceProvider Services { get; set; } = null!;
    [Inject] private NavigationManager Navigation { get; set; } = null!;
    [Inject] private IMentorPageContext PageContext { get; set; } = null!;
    [Inject] private IJSRuntime JS { get; set; } = null!;

    private string _filter = "";
    private string? _sortBy;
    private bool _descending;

    private string Run => Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(Navigation.Uri).Query)
        .TryGetValue("run", out var run) ? run.ToString() : "";

    protected override void OnInitialized()
    {
        PageContext.SetPageName("Help desk")
            .RegisterGrid<Ticket>("tickets", "The tickets", JS, t => t.Id, o => o.Sortable("Title", "Priority"))
            .RegisterGrid<Line>("orders", "The orders", JS, l => l.Id, o =>
            {
                o.Filter = false;
                o.Selector("row", "#orders tbody tr")
                    .Selector("sort.id", "#orders th:nth-child(1) .col-title")
                    .Selector("sort.customer", "#orders th:nth-child(2) .col-title");
            });
        PageContext.SignalReady();
    }

    protected override void OnAfterRender(bool firstRender)
    {
        if (firstRender) RecipeFormPage.Circuits[Run] = Services;
    }

    public void Dispose()
    {
        PageContext.Clear("Help desk");
        Opened.TryRemove(Run, out _);
        SortClicks.TryRemove(Run, out _);
        RowsDelay.TryRemove(Run, out _);
    }

    private IEnumerable<Ticket> Shown
    {
        get
        {
            var rows = Tickets.Where(t => _filter.Length == 0 || t.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) == _filter.Trim()
                                          || t.Title.Contains(_filter.Trim(), StringComparison.OrdinalIgnoreCase));
            return _sortBy switch
            {
                "title" => _descending ? rows.OrderByDescending(t => t.Title) : rows.OrderBy(t => t.Title),
                "priority" => _descending ? rows.OrderByDescending(t => t.Priority) : rows.OrderBy(t => t.Priority),
                _ => rows,
            };
        }
    }

    /// <summary>A click on a header: sorted by it ascending, or the other way when it already is, after a round trip.</summary>
    private async Task SortAsync(string column)
    {
        SortClicks.AddOrUpdate(Run, 1, (_, n) => n + 1);
        await Task.Delay(400);   // longer than the runner's quiet while: the step must wait for aria-sort, not for quiet
        _descending = _sortBy == column && !_descending;
        _sortBy = column;
    }

    protected override void BuildRenderTree(RenderTreeBuilder b)
    {
        b.OpenElement(0, "input");
        b.AddAttribute(1, "data-mentor-id", "tickets.filter");
        b.AddAttribute(2, "value", _filter);
        b.AddAttribute(3, "oninput", EventCallback.Factory.Create<ChangeEventArgs>(this, e => _filter = e.Value?.ToString() ?? ""));
        b.CloseElement();

        b.OpenElement(4, "table");
        b.AddAttribute(5, "id", "tickets");
        b.OpenElement(6, "thead");
        b.OpenElement(7, "tr");
        b.AddMarkupContent(8, "<th>Number</th>");
        Header(b, 10, "title", "Title", ariaSort: true);
        Header(b, 20, "priority", "Priority", ariaSort: false);
        b.AddMarkupContent(30, "<th></th>");
        b.CloseElement();
        b.CloseElement();
        b.OpenElement(31, "tbody");
        foreach (var ticket in Shown)
        {
            b.OpenElement(32, "tr");
            b.SetKey(ticket.Id);
            b.AddAttribute(33, "data-mentor-id", "tickets.row");
            b.OpenElement(34, "td");
            b.AddAttribute(35, "data-mentor-id", "tickets.cell.id");
            b.AddContent(36, ticket.Id);
            b.CloseElement();
            b.OpenElement(37, "td");
            b.AddContent(38, ticket.Title);
            b.CloseElement();
            b.OpenElement(39, "td");
            b.AddContent(40, ticket.Priority.ToString());
            b.CloseElement();
            b.OpenElement(41, "td");
            b.OpenElement(42, "button");
            b.AddAttribute(43, "type", "button");
            b.AddAttribute(44, "data-mentor-id", "tickets.open");
            b.AddAttribute(45, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, () => Opened[Run] = ticket.Id));
            b.AddContent(46, "Open");
            b.CloseElement();
            b.CloseElement();
            b.CloseElement();
        }
        b.CloseElement();
        b.CloseElement();

        // A header whose aria-sort never reads an order: none, other, none... at once, in its own handler.
        b.AddMarkupContent(50, "<table><thead><tr><th id=\"stuck\" aria-sort=\"none\" data-clicks=\"0\" onclick=\""
            + "this.dataset.clicks = +this.dataset.clicks + 1; this.setAttribute('aria-sort', this.getAttribute('aria-sort') === 'none' ? 'other' : 'none')"
            + "\">Stuck</th></tr></thead></table>"
            + "<table data-mentor-exclude><thead><tr><th id=\"excluded-header\" aria-sort=\"none\">Secret</th></tr></thead></table>");

        // Rows exactly as tall as ItemSize, as Virtualize needs: QuickGrid's own stylesheet is not served here.
        b.AddMarkupContent(51, "<style>#orders tr { height: 35px; } #orders td, #orders th { padding: 0 4px; border: 0; line-height: 35px; }</style>");
        b.OpenElement(52, "div");
        b.AddAttribute(53, "id", "orders");
        b.AddAttribute(54, "style", "height:300px;overflow-y:auto");
        b.OpenComponent<QuickGrid<Line>>(55);
        b.AddAttribute(56, "ItemsProvider", (GridItemsProvider<Line>)ProvideAsync);
        b.AddAttribute(57, "Virtualize", true);
        b.AddAttribute(58, "ItemSize", 35f);
        b.AddAttribute(59, "ChildContent", (RenderFragment)(c =>
        {
            // The key's cell: a span with its data-mentor-id, inside a TemplateColumn sorted by the key.
            c.OpenComponent<TemplateColumn<Line>>(0);
            c.AddAttribute(1, "Title", "Order");
            c.AddAttribute(2, "SortBy", GridSort<Line>.ByAscending(l => l.Id));
            c.AddAttribute(3, "ChildContent", (RenderFragment<Line>)(line => r =>
            {
                r.OpenElement(0, "span");
                r.AddAttribute(1, "data-mentor-id", "orders.cell.id");
                r.AddContent(2, line.Id);
                r.CloseElement();
            }));
            c.CloseComponent();
            c.OpenComponent<PropertyColumn<Line, string>>(4);
            c.AddAttribute(5, "Property", (Expression<Func<Line, string>>)(l => l.Customer));
            c.AddAttribute(6, "Sortable", true);
            c.CloseComponent();
            c.OpenComponent<TemplateColumn<Line>>(7);
            c.AddAttribute(8, "ChildContent", (RenderFragment<Line>)(line => r =>
            {
                r.OpenElement(0, "button");
                r.AddAttribute(1, "type", "button");
                r.AddAttribute(2, "data-mentor-id", "orders.open");
                r.AddAttribute(3, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, () => Opened[Run] = line.Id));
                r.AddContent(4, "Open");
                r.CloseElement();
            }));
            c.CloseComponent();
        }));
        b.CloseComponent();
        b.CloseElement();
    }

    /// <summary>The orders, sorted as the grid asks, after the run's delay: a server that answers each scroll late.</summary>
    private async ValueTask<GridItemsProviderResult<Line>> ProvideAsync(GridItemsProviderRequest<Line> request)
    {
        if (RowsDelay.TryGetValue(Run, out var delay)) await Task.Delay(delay, request.CancellationToken);
        var rows = request.ApplySorting(Lines).Skip(request.StartIndex);
        if (request.Count is { } count) rows = rows.Take(count);
        return GridItemsProviderResult.From(rows.ToList(), Lines.Count());
    }

    private void Header(RenderTreeBuilder b, int seq, string column, string label, bool ariaSort)
    {
#pragma warning disable ASP0006 // a region of its own for each header: the sequence numbers inside it are literals
        b.OpenRegion(seq);
#pragma warning restore ASP0006
        b.OpenElement(0, "th");
        b.AddAttribute(1, "data-mentor-id", "tickets.sort." + column);
        if (ariaSort && _sortBy == column) b.AddAttribute(2, "aria-sort", _descending ? "descending" : "ascending");
        b.AddAttribute(3, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, () => SortAsync(column)));
        b.AddContent(4, label);
        b.CloseElement();
        b.CloseRegion();
    }
}
