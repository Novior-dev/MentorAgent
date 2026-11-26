using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.JSInterop;
using Novior.MentorAgent.Abstractions.Core;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Xunit;

namespace Novior.MentorAgent.Tests.Pages;

/// <summary>
/// Story 1319, slice B — a page's grid registered with <c>RegisterGrid&lt;TRow&gt;</c> is the UI actions
/// <c>open_&lt;grid&gt;_row</c>, <c>sort_&lt;grid&gt;</c> and <c>filter_&lt;grid&gt;</c>: the key and the columns come
/// from the row, sensitive columns are not sorted by unless the application lets them in and are never the key, the
/// model's key, column and text are checked and become the steps of the recipes' runner, in the browser through the
/// page's <see cref="IJSRuntime"/>. The actions go through the gate and its events like any UI action, and are offered
/// only when the grid can do them. The browser itself is <c>GridBrowserTests</c>.
/// </summary>
[Trait("Rule", "grid.open")]
public sealed class GridActionTests
{
    public enum Priority { Low, Normal, High }

    public sealed class Ticket
    {
        public int Id { get; set; }
        public string? Title { get; set; }
        public Priority Priority { get; set; }
        public decimal Total { get; set; }
        public bool Urgent { get; set; }
        public DateOnly Due { get; set; }
        public string Summary => Title ?? "";                  // read only: a column all the same
        public List<string> Tags { get; set; } = [];           // not a simple type: no column
        public string? Pin { get; set; }
        public string? Iban { get; set; }
        public Guid Reference { get; set; }
        public string? AccessToken { get; set; }
    }

    private static MentorGrid Grid(Action<MentorGridOptions>? configure = null, Expression<Func<Ticket, object?>>? key = null)
    {
        var options = new MentorGridOptions();
        configure?.Invoke(options);
        return MentorGrid.Create(typeof(Ticket), "tickets", MentorGrid.KeyOf(key ?? (t => t.Id)), options);
    }

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    private static string Wire(IReadOnlyList<MentorGridStep> steps) => JsonSerializer.Serialize(steps, JsonSerializerOptions.Web);

    // -- The grid --------------------------------------------------------------------

    [Fact]
    public void The_actions_the_key_the_columns_and_the_selectors_come_from_the_row_and_the_grids_name()
    {
        var grid = Grid();

        (grid.OpenAction, grid.SortAction, grid.FilterAction).Should().Be(("open_tickets_row", "sort_tickets", "filter_tickets"));
        grid.Key.Name.Should().Be("id");
        grid.Key.Selector.Should().Be("[data-mentor-id=\"tickets.cell.id\"]");
        grid.RowSelector.Should().Be("[data-mentor-id=\"tickets.row\"]");
        grid.OpenSelector.Should().Be("[data-mentor-id=\"tickets.open\"]");
        grid.FilterSelector.Should().Be("[data-mentor-id=\"tickets.filter\"]");
        grid.Sortable.Select(c => c.Name).Should().Equal("id", "title", "priority", "total", "urgent", "due", "summary", "reference");
        grid.Sortable.Should().Contain(new MentorGridColumn("total", "[data-mentor-id=\"tickets.sort.total\"]"));
        grid.OpenHint.Should().Be("{ key: integer } - the id of the row to open, as the grid shows it");
        grid.SortHint.Should().Be("{ column: string (id|title|priority|total|urgent|due|summary|reference), descending: boolean? }");
    }

    [Fact]
    [Trait("Rule", "grid.sensitive")]
    public void A_sensitive_column_is_not_sorted_by_unless_the_application_lets_it_in()
    {
        Grid().Sortable.Select(c => c.Name).Should().NotContain(["pin", "iban", "accessToken"]);
        Grid(o => o.IncludeSensitive("Iban")).Sortable.Select(c => c.Name).Should().Contain("iban").And.NotContain("pin");
        Grid(o => o.Sortable("title", "iban").IncludeSensitive("iban")).Sortable.Select(c => c.Name).Should().Equal("title", "iban");
        FluentActions.Invoking(() => Grid(o => o.Sortable("title", "pin")))
            .Should().Throw<ArgumentException>().WithMessage("*'Pin' is a sensitive column*IncludeSensitive*");
    }

    [Fact]
    [Trait("Rule", "grid.sensitive")]
    public void A_sensitive_key_is_refused_and_so_is_a_key_the_page_shows_in_a_format_of_its_own()
    {
        FluentActions.Invoking(() => Grid(key: t => t.Pin)).Should().Throw<ArgumentException>()
            .WithMessage("*'Pin'*sensitive*Choose another key*");
        FluentActions.Invoking(() => Grid(o => o.IncludeSensitive("AccessToken"), t => t.AccessToken))
            .Should().Throw<ArgumentException>().WithMessage("*sensitive*", "a sensitive key is refused even when let in");
        FluentActions.Invoking(() => Grid(key: t => t.Due)).Should().Throw<ArgumentException>().WithMessage("*DateOnly*a text, a whole number, a Guid or an enum*");
        FluentActions.Invoking(() => Grid(key: t => t.Urgent)).Should().Throw<ArgumentException>().WithMessage("*Boolean*");
        FluentActions.Invoking(() => Grid(key: t => t.Id + 1)).Should().Throw<ArgumentException>().WithMessage("*a property of the row*");

        Grid(key: t => t.Title).Key.Name.Should().Be("title");
        Grid(key: t => t.Reference).OpenHint.Should().StartWith("{ key: string (GUID) }");
        Grid(key: t => t.Priority).OpenHint.Should().StartWith("{ key: string (Low|Normal|High) }");
    }

    [Fact]
    public void The_options_choose_the_columns_the_filter_and_the_selectors_and_a_wrong_name_throws_at_once()
    {
        Grid(o => o.Sortable("Total", "title")).Sortable.Select(c => c.Name).Should().Equal(["title", "total"], "in the row's order");
        Grid(o => o.Sortable()).Sortable.Should().BeEmpty("none: the grid sorts by nothing");
        Grid(o => o.Filter = false).FilterSelector.Should().BeNull();

        var grid = Grid(o => o
            .Selector("row", "#orders tbody tr").Selector("cell.Id", "span.key").Selector("open", "button.open")
            .Selector("filter", "#search").Selector("sort.total", "#orders th:nth-child(4) .col-title"));
        (grid.RowSelector, grid.Key.Selector, grid.OpenSelector, grid.FilterSelector)
            .Should().Be(("#orders tbody tr", "span.key", "button.open", "#search"));
        grid.Sortable.Single(c => c.Name == "total").Selector.Should().Be("#orders th:nth-child(4) .col-title");
        grid.Sortable.Single(c => c.Name == "title").Selector.Should().Be("[data-mentor-id=\"tickets.sort.title\"]");

        FluentActions.Invoking(() => Grid(o => o.Sortable("colour"))).Should().Throw<ArgumentException>().WithMessage("*'colour' is not a column*");
        FluentActions.Invoking(() => Grid(o => o.IncludeSensitive("colour"))).Should().Throw<ArgumentException>().WithMessage("*'colour'*");
        FluentActions.Invoking(() => Grid(o => o.Selector("header", "th"))).Should().Throw<ArgumentException>()
            .WithMessage("*'header' is no part*row, cell.id, open, filter, sort.id*");
        FluentActions.Invoking(() => Grid(o => o.Selector("cell.title", "td"))).Should().Throw<ArgumentException>()
            .WithMessage("*'cell.title' is no part*", "only the key's cell is read");
        FluentActions.Invoking(() => Grid(o => o.Sortable("title").Selector("sort.total", "th"))).Should().Throw<ArgumentException>()
            .WithMessage("*'sort.total' is no part*", "the grid does not sort by total");
        FluentActions.Invoking(() => Grid(o => { o.Filter = false; o.Selector("filter", "#search"); })).Should().Throw<ArgumentException>()
            .WithMessage("*'filter' is no part*");
        FluentActions.Invoking(() => MentorGrid.Create(typeof(Ticket), "tick ets", MentorGrid.KeyOf((Expression<Func<Ticket, object?>>)(t => t.Id)),
            new MentorGridOptions())).Should().Throw<ArgumentException>().WithMessage("*not a tool's name*");
    }

    // -- The steps -------------------------------------------------------------------

    [Fact]
    public void Opening_a_row_is_the_runners_row_step_with_the_key_checked_and_written_as_the_page_shows_it()
    {
        var grid = Grid();

        Wire(grid.OpenSteps(Json(new { key = 42 }))).Should().Be(
            "[{\"kind\":\"row\",\"selector\":\"[data-mentor-id=\\u0022tickets.row\\u0022]\",\"value\":\"42\","
            + "\"cell\":\"[data-mentor-id=\\u0022tickets.cell.id\\u0022]\",\"target\":\"[data-mentor-id=\\u0022tickets.open\\u0022]\","
            + "\"filter\":\"[data-mentor-id=\\u0022tickets.filter\\u0022]\"}]");
        grid.OpenSteps(Json(42))[0].Value.Should().Be("42", "the key alone is taken too");
        grid.OpenSteps(Json(new { key = "42" }))[0].Value.Should().Be("42");
        grid.OpenSteps(Json(new { id = 42 }))[0].Value.Should().Be("42", "the key by its own name");
        grid.OpenSteps("{\"key\": 42}")[0].Value.Should().Be("42", "an object written as a text");
        Grid(o => o.Filter = false).OpenSteps(Json(new { key = 7 }))[0].Filter.Should().BeNull();
        Grid(key: t => t.Priority).OpenSteps(Json(new { key = "high" }))[0].Value.Should().Be("High");

        FluentActions.Invoking(() => grid.OpenSteps(Json(new { key = "forty-two" }))).Should().Throw<ArgumentException>().WithMessage("*'id'*integer*");
        FluentActions.Invoking(() => grid.OpenSteps(Json(new { key = 4.5 }))).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => grid.OpenSteps(Json(new { }))).Should().Throw<ArgumentException>().WithMessage("Give the row to open as { key: integer }*");
        FluentActions.Invoking(() => grid.OpenSteps(null)).Should().Throw<ArgumentException>().WithMessage("Give the row to open*");
        FluentActions.Invoking(() => Grid(key: t => t.Title).OpenSteps(Json(new { key = " " }))).Should().Throw<ArgumentException>();
    }

    [Fact]
    [Trait("Rule", "grid.sort")]
    public void Sorting_is_the_runners_sort_step_on_the_columns_header_with_the_order_asked()
    {
        var grid = Grid();

        Wire(grid.SortSteps(Json(new { column = "total", descending = true }))).Should().Be(
            "[{\"kind\":\"sort\",\"selector\":\"[data-mentor-id=\\u0022tickets.sort.total\\u0022]\",\"value\":\"descending\","
            + "\"cell\":null,\"target\":null,\"filter\":null}]");
        grid.SortSteps(Json(new { column = "Title" }))[0].Value.Should().Be("ascending");
        grid.SortSteps(Json(new { column = "title", descending = "true" }))[0].Value.Should().Be("descending");
        grid.SortSteps(Json(new { column = "title", descending = (bool?)null }))[0].Value.Should().Be("ascending");
        grid.SortSteps(Json("due"))[0].Selector.Should().Be("[data-mentor-id=\"tickets.sort.due\"]", "the column alone is taken too");

        FluentActions.Invoking(() => grid.SortSteps(Json(new { column = "colour" }))).Should().Throw<ArgumentException>()
            .WithMessage("*'colour' is not a column the grid 'tickets' sorts by*id, title*");
        FluentActions.Invoking(() => grid.SortSteps(Json(new { column = "pin" }))).Should().Throw<ArgumentException>()
            .WithMessage("*'pin' is not a column*").Which.Message.Should().NotContain("pin,");
        FluentActions.Invoking(() => grid.SortSteps(Json(new { column = "title", descending = "maybe" }))).Should().Throw<ArgumentException>()
            .WithMessage("*'descending' is true or false*");
        FluentActions.Invoking(() => grid.SortSteps(Json(new { descending = true }))).Should().Throw<ArgumentException>()
            .WithMessage("Give the column to sort by*");
    }

    [Fact]
    [Trait("Rule", "grid.filter")]
    public void Filtering_writes_the_text_in_the_grids_filter_and_an_empty_text_clears_it()
    {
        var grid = Grid();

        Wire(grid.FilterSteps(Json("printer"))).Should().Be(
            "[{\"kind\":\"set\",\"selector\":\"[data-mentor-id=\\u0022tickets.filter\\u0022]\",\"value\":\"printer\","
            + "\"cell\":null,\"target\":null,\"filter\":null}]");
        grid.FilterSteps(Json(""))[0].Value.Should().BeEmpty();
        grid.FilterSteps(null)[0].Value.Should().BeEmpty();
        grid.FilterSteps(Json(new { text = "jam" }))[0].Value.Should().Be("jam");
        grid.FilterSteps("printer")[0].Value.Should().Be("printer");
        grid.FilterSteps(Json(42))[0].Value.Should().Be("42");
        FluentActions.Invoking(() => grid.FilterSteps(Json(new { words = "jam" }))).Should().Throw<ArgumentException>().WithMessage("*one string*");
    }

    // -- Through the gate -----------------------------------------------------------------

    /// <summary>The page's JavaScript runtime: the runner's module, recording the steps it is given, answering as told.</summary>
    private sealed class RecordingJs : IJSRuntime, IJSObjectReference
    {
        internal List<string> Imports { get; } = [];
        internal List<string> Runs { get; } = [];
        internal Queue<object> Answers { get; } = new();
        internal int Disposed { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "import")
            {
                Imports.Add((string)args![0]!);
                return ValueTask.FromResult((TValue)(object)this);
            }
            identifier.Should().Be("run");
            // As the browser receives it: serialized with the web defaults of JS interop (camelCase).
            Runs.Add(JsonSerializer.Serialize(args![0], JsonSerializerOptions.Web));
            var answer = Answers.Count > 0 ? Answers.Dequeue() : new { done = true, step = 1, reason = (string?)null };
            return ValueTask.FromResult(JsonSerializer.Deserialize<TValue>(JsonSerializer.Serialize(answer), JsonSerializerOptions.Web)!);
        }

        public ValueTask DisposeAsync()
        {
            Disposed++;
            return ValueTask.CompletedTask;
        }
    }

    private static MentorHostFixture Host(RecordingActionObserver observer)
        => MentorHostFixture.Create(o => o.MemoryAutoCapture = false, s => s.AddMentorActionObserver(observer), withRecorder: true);

    [Fact]
    [Trait("Rule", "grid.filter")]
    [Trait("Rule", "grid.sort")]
    public async Task The_model_filters_sorts_and_opens_a_row_of_a_registered_grid_through_the_gate()
    {
        var observer = new RecordingActionObserver();
        await using var host = Host(observer);
        var js = new RecordingJs();
        host.Get<IMentorPageContext>().SetPageName("Tickets")
            .RegisterGrid<Ticket>("tickets", "The open tickets", js, t => t.Id)
            .SignalReady();
        host.Chat!.CallTool("filter_tickets", new { parameter = "printer" })
            .CallTool("sort_tickets", new { parameter = new { column = "total", descending = true } })
            .CallTool("open_tickets_row", new { parameter = new { key = 42 } })
            .Say("Ticket 42 is open.");

        (await host.RunTurnAsync("open the most expensive printer ticket")).Should().BeNull();
        await host.Get<MentorActionDecisions>().FlushAsync();

        js.Imports.Should().Equal(MentorForm.Module, MentorForm.Module, MentorForm.Module);
        js.Runs.Should().HaveCount(3);
        js.Runs[0].Should().Contain("\"kind\":\"set\"").And.Contain("\"value\":\"printer\"");
        js.Runs[1].Should().Contain("\"kind\":\"sort\"").And.Contain("tickets.sort.total").And.Contain("\"value\":\"descending\"");
        js.Runs[2].Should().Contain("\"kind\":\"row\"").And.Contain("\"value\":\"42\"");
        js.Disposed.Should().Be(3, "the module is let go after each action");
        foreach (var action in new[] { "filter_tickets", "sort_tickets", "open_tickets_row" })
            observer.Steps(action).Should().Equal(["Allowed/allowed", "Executed/completed"], action);
        observer.Seen.First(d => d.ToolName == "open_tickets_row").DisplayName.Should().StartWith("The open tickets");
        host.Recorder!.Sequence.Count(e => e == nameof(RecordingStateService.NotifyUIActionCompleted)).Should().Be(3);

        var call = host.Chat!.Calls[0];
        call.Tool("open_tickets_row")!.Description.Should().Contain("The open tickets").And.Contain("{ key: integer }");
        call.Tool("sort_tickets")!.Description.Should().Contain("column: string (id|title").And.NotContain("pin");
        call.Tool("filter_tickets")!.Description.Should().Contain("an empty text clears it");
    }

    [Fact]
    public async Task An_action_the_grid_cannot_do_is_not_offered()
    {
        var observer = new RecordingActionObserver();
        await using var host = Host(observer);
        host.Get<IMentorPageContext>().SetPageName("Tickets")
            .RegisterGrid<Ticket>("tickets", "The open tickets", new RecordingJs(), t => t.Id, o => { o.Filter = false; o.Sortable(); })
            .SignalReady();
        host.Chat!.Say("Here they are.");

        await host.RunTurnAsync("show me the tickets");

        var names = host.Chat!.Calls[0].Tools.Select(t => t.Name).ToList();
        names.Should().Contain("open_tickets_row").And.NotContain("filter_tickets").And.NotContain("sort_tickets");
    }

    [Theory]
    [InlineData("open_tickets_row", "{\"key\":42}", "no-row", "No row of the grid 'tickets' has the id '42' in the page, after its filter and scrolling")]
    [InlineData("open_tickets_row", "{\"key\":42}", "excluded", "The grid 'tickets' is not one the assistant reads")]
    [InlineData("open_tickets_row", "{\"key\":42}", "missing", "a part of the grid is missing in the page")]
    [InlineData("sort_tickets", "{\"column\":\"title\"}", "no-sort-state", "its header does not say how the rows are sorted (aria-sort)")]
    [InlineData("sort_tickets", "{\"column\":\"title\",\"descending\":true}", "not-sorted", "not sorted by 'title' descending: after three clicks")]
    [InlineData("sort_tickets", "{\"column\":\"title\"}", "hidden", "the header of 'title' is hidden in the page")]
    [InlineData("filter_tickets", "\"jam\"", "disabled", "its filter field is disabled in the page")]
    [Trait("Rule", "grid.sort")]
    public async Task An_action_not_done_in_the_page_fails_and_the_model_is_told_why(string action, string parameter, string reason, string told)
    {
        var observer = new RecordingActionObserver();
        await using var host = Host(observer);
        var js = new RecordingJs();
        js.Answers.Enqueue(new { done = false, step = 0, reason });
        host.Get<IMentorPageContext>().RegisterGrid<Ticket>("tickets", "The open tickets", js, t => t.Id).SignalReady();
        host.Chat!.CallTool(action, new { parameter = JsonDocument.Parse(parameter).RootElement }).Say("Sorry.");

        await host.RunTurnAsync("do it");
        await host.Get<MentorActionDecisions>().FlushAsync();

        observer.Steps(action).Should().Equal(["Allowed/allowed", "Failed/exception"]);
        Results(host.Chat!).Should().Contain(told);
    }

    [Fact]
    public async Task A_key_its_column_does_not_take_reaches_neither_the_page_nor_an_executed_decision()
    {
        var observer = new RecordingActionObserver();
        await using var host = Host(observer);
        var js = new RecordingJs();
        host.Get<IMentorPageContext>().RegisterGrid<Ticket>("tickets", "The open tickets", js, t => t.Id).SignalReady();
        host.Chat!.CallTool("open_tickets_row", new { parameter = new { key = "the printer one" } }).Say("Sorry.");

        await host.RunTurnAsync("open it");
        await host.Get<MentorActionDecisions>().FlushAsync();

        js.Imports.Should().BeEmpty("the key is checked before anything runs in the page");
        observer.Steps("open_tickets_row").Should().Equal(["Allowed/allowed", "Failed/exception"]);
        Results(host.Chat!).Should().Contain("'id'");
    }

    private static string Results(FakeChatClient chat) => string.Join("\n", chat.Calls
        .SelectMany(c => c.Messages).SelectMany(m => m.Contents).OfType<FunctionResultContent>()
        .Select(r => System.Text.RegularExpressions.Regex.Unescape(r.Result?.ToString() ?? "")));
}
