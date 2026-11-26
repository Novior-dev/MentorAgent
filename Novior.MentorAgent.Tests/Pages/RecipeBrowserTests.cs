using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.QuickGrid;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Scanner.Cdp;
using Novior.MentorAgent.Tests.App;
using Novior.MentorAgent.Tests.Docs;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Xunit;
using PageRoute = Microsoft.AspNetCore.Components.RouteAttribute;

namespace Novior.MentorAgent.Tests.Pages;

/// <summary>
/// Story 1638 (L4), slice A — recipes run in a real browser (Edge or Chrome, headless) on an interactive Blazor Server
/// page, through the circuit: an <c>EditForm</c> with <c>InputText</c>, <c>InputNumber</c>, <c>InputSelect</c> and
/// <c>InputCheckbox</c> is filled and saved by the model's call, and an element that is gone, hidden, disabled or
/// excluded makes the recipe "not done", turns it off and tells the observers. Slice B — a row of a virtualized
/// <c>QuickGrid</c> is found by its key, scrolling or with the page's filter, and an action clicked in it. Slice C — rows
/// inside <c>data-mentor-exclude</c> are never read, and a WebAssembly client runs the hub's recipe in its page.
/// </summary>
public sealed class RecipeBrowserTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private static MentorRecipe Order(params MentorRecipeStep[] extra) => new()
    {
        Name = "create_order",
        Description = "Creates an order for a customer",
        Route = "/recipes/{page}",
        Parameters =
        [
            new("customer", "The customer's name"),
            new("quantity", "How many", MentorRecipeParameterType.Number),
            new("country", "The country"),
            new("express", "Express shipping", MentorRecipeParameterType.Boolean),
            new("note", "A note"),
        ],
        Steps =
        [
            new(MentorRecipeStepKind.Set, "#customer", "{customer}"),
            new(MentorRecipeStepKind.Set, "#quantity", "{quantity}"),
            new(MentorRecipeStepKind.Choose, "#country", "{country}"),
            new(MentorRecipeStepKind.Check, "#express", "{express}"),
            new(MentorRecipeStepKind.Set, "#note", "{note}"),
            .. extra,
            new(MentorRecipeStepKind.Click, "#save"),
            new(MentorRecipeStepKind.Wait, "#saved"),
        ],
    };

    private static readonly object Anna = new { customer = "Anna Rossi", quantity = 3, country = "France", express = true, note = "Ring twice" };

    /// <summary>An interactive Blazor Server application with MentorAgent, served on a port of its own, and a browser on it.</summary>
    internal sealed class Site : IAsyncDisposable
    {
        internal required WebApplication App { get; init; }
        internal required CdpBrowser Browser { get; init; }
        internal required FakeChatClient Chat { get; init; }
        internal required RecordingActionObserver Observer { get; init; }
        internal required string Run { get; init; }
        internal required TestLogCollector Logs { get; init; }

        internal IServiceProvider Circuit => RecipeFormPage.Circuits[Run];

        public async ValueTask DisposeAsync()
        {
            RecipeFormPage.Circuits.TryRemove(Run, out _);
            RecipeFormPage.Saved.TryRemove(Run, out _);
            RecipeGridPage.Opened.TryRemove(Run, out _);
            RecipeGridPage.FilterDelay.TryRemove(Run, out _);
            RecipeGridPage.Intermediate.TryRemove(Run, out _);
            RecipeGridPage.FilterIgnored.TryRemove(Run, out _);
            await Browser.DisposeAsync();
            await App.DisposeAsync();
        }
    }

    internal static async Task<Site> OpenAsync(MentorRecipe? recipe, string page = "form", IMentorRecipeSource? source = null)
    {
        var chat = new FakeChatClient();
        var observer = new RecordingActionObserver();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var logs = new TestLogCollector();
        builder.Logging.AddProvider(new TestLoggerProvider(logs));
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        builder.Services.AddMentorActionObserver(observer);
        if (source is not null) builder.Services.AddSingleton(source);
        builder.Services.AddMentorAgent(o =>
        {
            o.AppName = "Recipes";
            o.ChatClient = chat;
            o.ScanAssemblies = [typeof(TestOrderService).Assembly];
            o.MemoryAutoCapture = false;
            o.CheckAnnouncedActions = false;
        });

        var app = builder.Build();
        app.UseAntiforgery();
        var blazor = Path.Combine(AppContext.BaseDirectory, "Interactive", "blazor.web.js");
        app.MapGet("/_framework/blazor.web.js", () => Results.File(blazor, "text/javascript"));
        var scripts = Path.Combine(RepoFiles.Root, "Novior.MentorAgent.Abstractions", "wwwroot", "js");
        app.MapGet("/_content/Novior.MentorAgent.Abstractions/js/{name}", (string name) => name == "MentorRecipeRunner.js"
            ? Results.File(Path.Combine(scripts, name), "text/javascript")
            : Results.NotFound());
        app.MapGet("/_content/Microsoft.AspNetCore.Components.QuickGrid/QuickGrid.razor.js",
            () => Results.File(Path.Combine(AppContext.BaseDirectory, "Interactive", "QuickGrid.razor.js"), "text/javascript"));
        app.MapRazorComponents<RecipeHostRoot>().AddInteractiveServerRenderMode();
        await app.StartAsync();

        if (recipe is not null) app.Services.GetRequiredService<MentorRecipeRegistry>().Add(recipe);
        var run = Guid.NewGuid().ToString("N");
        using (var http = new HttpClient())
        {
            // What the server answers, said in the failure, before a browser that would only say "failed".
            var answer = await http.GetAsync($"{app.Urls.First()}/recipes/{page}?run=probe");
            if (!answer.IsSuccessStatusCode)
                throw new InvalidOperationException($"The site answered {(int)answer.StatusCode}: {await answer.Content.ReadAsStringAsync()}");
        }
        var browser = await CdpBrowser.StartAsync(CdpBrowser.Find(null)!, CancellationToken.None);
        await browser.OpenAsync(new Uri($"{app.Urls.First()}/recipes/{page}?run={run}"), Wait, CancellationToken.None);

        var until = DateTime.UtcNow + Wait;
        while (!RecipeFormPage.Circuits.ContainsKey(run))
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("The page did not become interactive.");
            await Task.Delay(100);
        }

        return new Site { App = app, Browser = browser, Chat = chat, Observer = observer, Run = run, Logs = logs };
    }

    private static async Task<RecipeFormPage.Order> SavedAsync(Site site)
    {
        var until = DateTime.UtcNow + Wait;
        while (!RecipeFormPage.Saved.ContainsKey(site.Run))
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("The form was not saved.");
            await Task.Delay(100);
        }
        return RecipeFormPage.Saved[site.Run];
    }

    internal static async Task TurnAsync(Site site, string message)
    {
        await site.Circuit.GetRequiredService<IMentorOrchestrator>().SendMessageAsync(message);
        await site.App.Services.GetRequiredService<MentorActionDecisions>().FlushAsync();
    }

    [BrowserFact]
    [Trait("Rule", "recipe.run")]
    public async Task A_recipe_fills_an_EditForm_on_an_interactive_Blazor_Server_page_and_saves_it()
    {
        await using var site = await OpenAsync(Order());
        site.Chat.CallTool("create_order", Anna).Say("The order is saved.");

        await TurnAsync(site, "Order 3 for Anna Rossi to France, express, ring twice");

        var saved = await SavedAsync(site);
        saved.Should().BeEquivalentTo(new RecipeFormPage.Order
        {
            Customer = "Anna Rossi", Quantity = 3, Country = "FR", Express = true, Note = "Ring twice",
        }, "InputText and InputNumber listen to change, InputSelect and InputCheckbox too, @bind:event=\"oninput\" to input");
        site.Observer.Steps("create_order").Should().Equal(["Allowed/allowed", "Executed/completed"]);
        site.App.Services.GetRequiredService<MentorRecipeRegistry>().IsOff("create_order").Should().BeFalse();
    }

    /// <summary>Story 1639 — the recipe a console approved, as a bridge gives it: a definition of a source.</summary>
    private sealed class ApprovedRecipes : IMentorRecipeSource
    {
        public Task<MentorRecipeSourceResult> LoadAsync(MentorRecipeSourceContext context, CancellationToken ct = default)
        {
            var order = Order();
            MentorRecipeDefinition definition = new()
            {
                Name = order.Name,
                Description = order.Description,
                Route = order.Route,
                Parameters = [.. order.Parameters.Select(p => new MentorRecipeParameterDefinition
                    { Name = p.Name, Description = p.Description, Type = p.Type })],
                Steps = [.. order.Steps.Select(s => new MentorRecipeStepDefinition { Kind = s.Kind, Selector = s.Selector, Value = s.Value })],
            };
            return Task.FromResult(new MentorRecipeSourceResult([definition], []));
        }
    }

    [BrowserFact]
    [Trait("Rule", "recipe.from-source")]
    public async Task A_recipe_of_a_source_fills_the_form_of_its_page_in_the_browser()
    {
        await using var site = await OpenAsync(null, source: new ApprovedRecipes());
        site.Chat.CallTool("create_order", Anna).Say("The order is saved.");

        await TurnAsync(site, "Order 3 for Anna Rossi to France, express, ring twice");

        (await SavedAsync(site)).Should().BeEquivalentTo(new RecipeFormPage.Order
        {
            Customer = "Anna Rossi", Quantity = 3, Country = "FR", Express = true, Note = "Ring twice",
        });
        site.Observer.Steps("create_order").Should().Equal(["Allowed/allowed", "Executed/completed"]);
    }

    [BrowserFact]
    [Trait("Rule", "recipe.element-missing")]
    public async Task A_recipe_whose_element_is_gone_is_not_done_turns_off_and_the_form_is_not_saved()
    {
        await using var site = await OpenAsync(Order(new MentorRecipeStep(MentorRecipeStepKind.Click, "#coupon")));
        site.Chat.CallTool("create_order", Anna).Say("It did not work.");

        await TurnAsync(site, "Order 3 for Anna Rossi");

        RecipeFormPage.Saved.ContainsKey(site.Run).Should().BeFalse("the runner stopped before the save, and looked for no other element");
        site.Observer.Steps("create_order").Should().Equal(["Allowed/allowed", "Failed/element-missing"]);
        site.Observer.Seen.Single(d => d.Outcome == MentorActionOutcome.Failed).Detail.Should().Be("step 6: missing");
        site.App.Services.GetRequiredService<MentorRecipeRegistry>().IsOff("create_order").Should().BeTrue();
    }

    private static MentorRecipe OpenOrder(string? filter = null) => new()
    {
        Name = "open_order",
        Description = "Opens an order of the list",
        Route = "/recipes/{page}",
        Parameters = [new("order", "The order's number", MentorRecipeParameterType.Number)],
        Steps =
        [
            new(MentorRecipeStepKind.Row, "#orders tbody tr", "{order}") { Cell = "td:nth-child(1)", Target = "button.open", Filter = filter },
            new(MentorRecipeStepKind.Wait, "#opened"),
        ],
    };

    private static async Task<int> OpenedAsync(Site site)
    {
        var until = DateTime.UtcNow + Wait;
        while (!RecipeGridPage.Opened.ContainsKey(site.Run))
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("No order was opened.");
            await Task.Delay(100);
        }
        return RecipeGridPage.Opened[site.Run];
    }

    [BrowserFact]
    [Trait("Rule", "recipe.row")]
    public async Task A_row_of_a_virtualized_QuickGrid_is_reached_by_scrolling_and_its_action_clicked()
    {
        await using var site = await OpenAsync(OpenOrder(), "grid");
        (await site.Browser.EvaluateAsync("document.querySelectorAll('#orders tbody tr td:first-child').length", CancellationToken.None))
            .GetInt32().Should().BeLessThan(RecipeGridPage.Count / 2, "the grid renders only the rows in view");
        site.Chat.CallTool("open_order", new { order = 250 }).Say("Order 250 is open.");

        await TurnAsync(site, "Open order 250");

        (await OpenedAsync(site)).Should().Be(250);
        site.Observer.Steps("open_order").Should().Equal(["Allowed/allowed", "Executed/completed"]);
        (await site.Browser.EvaluateAsync("document.getElementById('filter').value", CancellationToken.None))
            .GetString().Should().BeEmpty("a recipe without a filter scrolls");
    }

    [BrowserFact]
    [Trait("Rule", "recipe.row")]
    public async Task A_row_step_waits_for_a_filter_slower_than_a_quiet_while_and_finds_the_row_without_scrolling()
    {
        await using var site = await OpenAsync(OpenOrder(filter: "#filter"), "grid");
        RecipeGridPage.FilterDelay[site.Run] = 1000;   // the server's round trip under load, far longer than 150 ms of quiet
        RecipeGridPage.Intermediate[site.Run] = true;  // and a render at once, the rows as they were (fixes 1639)
        await site.Browser.EvaluateAsync(
            "window.scrolls = 0; document.getElementById('orders').addEventListener('scroll', () => window.scrolls++); true",
            CancellationToken.None);
        var runner = site.Circuit.GetRequiredService<IMentorRecipeRunner>();

        (await runner.RunAsync(MentorRecipeRegistry.Steps(OpenOrder(filter: "#filter"), new Dictionary<string, object?> { ["order"] = 280 }),
            CancellationToken.None)).Should().Be(new MentorRecipeOutcome(true, 2, null));

        (await OpenedAsync(site)).Should().Be(280);
        (await site.Browser.EvaluateAsync("window.scrolls", CancellationToken.None))
            .GetInt32().Should().Be(0, "the runner waited for the filter's answer: the rows were never scrolled");
        (await site.Browser.EvaluateAsync("document.querySelector('#orders table').className", CancellationToken.None))
            .GetString().Should().Contain("renders-1", "the page rendered once before its filtered rows came");
    }

    [BrowserFact]
    [Trait("Rule", "recipe.row")]
    public async Task The_first_rows_of_a_grid_that_showed_none_are_not_the_filters_answer()
    {
        // 1639: the cause of the test above failing under load. The grid had not drawn its rows when the step began;
        // its rows came later than a quiet while, and were taken for the filter's answer: the row was then scrolled for.
        await using var site = await OpenAsync(OpenOrder(filter: "#filter"), "grid");
        RecipeGridPage.FilterDelay[site.Run] = 1000;
        await site.Browser.EvaluateAsync(
            "window.scrolls = 0; document.getElementById('orders').addEventListener('scroll', () => window.scrolls++); true",
            CancellationToken.None);

        (await RunWithRowsDrawnLateAsync(site, 280)).Should().Be(new MentorRecipeOutcome(true, 2, null));

        (await OpenedAsync(site)).Should().Be(280);
        (await site.Browser.EvaluateAsync("window.scrolls", CancellationToken.None))
            .GetInt32().Should().Be(0, "the grid's own rows, drawn after the step began, are not the filter's answer: the runner waited for it");
    }

    [BrowserFact]
    [Trait("Rule", "recipe.row")]
    public async Task After_a_grid_drew_its_rows_late_a_key_the_filter_does_not_find_ends_without_waiting_for_the_cap()
    {
        await using var site = await OpenAsync(OpenOrder(filter: "#filter"), "grid");
        RecipeGridPage.FilterDelay[site.Run] = 1000;   // the answer after the rows drawn late
        var clock = System.Diagnostics.Stopwatch.StartNew();

        (await RunWithRowsDrawnLateAsync(site, 999)).Should().Be(new MentorRecipeOutcome(false, 0, "no-row"));

        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(4),
            "the rows drawn late are those the filter's answer is compared with: an answer without the row ends the wait");
    }

    /// <summary>
    /// Runs the order's recipe on a grid that shows no rows when the step begins, and draws them only once the step has
    /// read the rows it had and written the filter: the order of a virtualized grid measuring itself under load, made
    /// certain. The filter's answer comes after the page's FilterDelay.
    /// </summary>
    private static async Task<MentorRecipeOutcome> RunWithRowsDrawnLateAsync(Site site, int order)
    {
        await site.Browser.EvaluateAsync("document.getElementById('redraw').click(); true", CancellationToken.None);
        await UntilAsync(site, "[...document.querySelectorAll('#orders tbody tr')].filter(r => r.textContent.trim()).length === 0",
            "The grid kept its rows.");

        var runner = site.Circuit.GetRequiredService<IMentorRecipeRunner>();
        var run = runner.RunAsync(MentorRecipeRegistry.Steps(OpenOrder(filter: "#filter"), new Dictionary<string, object?> { ["order"] = order }),
            CancellationToken.None);
        await UntilAsync(site, $"document.getElementById('filter').value === '{order}'", "The step did not write the filter.");
        await site.Browser.EvaluateAsync("document.getElementById('drawn').click(); true", CancellationToken.None);
        return await run;
    }

    private static async Task UntilAsync(Site site, string condition, string failure)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!(await site.Browser.EvaluateAsync(condition, CancellationToken.None)).GetBoolean())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException(failure);
            await Task.Delay(10);
        }
    }

    [BrowserFact]
    [Trait("Rule", "recipe.row")]
    public async Task A_row_already_shown_is_taken_without_writing_in_the_filter()
    {
        await using var site = await OpenAsync(OpenOrder(filter: "#filter"), "grid");
        var runner = site.Circuit.GetRequiredService<IMentorRecipeRunner>();

        (await runner.RunAsync(MentorRecipeRegistry.Steps(OpenOrder(filter: "#filter"), new Dictionary<string, object?> { ["order"] = 2 }),
            CancellationToken.None)).Should().Be(new MentorRecipeOutcome(true, 2, null));

        (await OpenedAsync(site)).Should().Be(2);
        (await site.Browser.EvaluateAsync("document.getElementById('filter').value", CancellationToken.None))
            .GetString().Should().BeEmpty("the row was in the page: nothing was written in the filter");
    }

    [BrowserFact]
    [Trait("Rule", "recipe.row")]
    public async Task A_filter_that_leaves_the_rows_as_they_were_ends_the_wait_at_the_cap_and_the_row_is_found_scrolling()
    {
        await using var site = await OpenAsync(OpenOrder(filter: "#filter"), "grid");
        RecipeGridPage.FilterIgnored[site.Run] = true;
        RecipeGridPage.Intermediate[site.Run] = true;
        var runner = site.Circuit.GetRequiredService<IMentorRecipeRunner>();
        var clock = System.Diagnostics.Stopwatch.StartNew();

        (await runner.RunAsync(MentorRecipeRegistry.Steps(OpenOrder(filter: "#filter"), new Dictionary<string, object?> { ["order"] = 280 }),
            CancellationToken.None)).Should().Be(new MentorRecipeOutcome(true, 2, null));

        (await OpenedAsync(site)).Should().Be(280);
        clock.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(4.5),
            "rows that never changed are no answer: the step waited for the 5 s cap, then scrolled");
    }

    [BrowserFact]
    [Trait("Rule", "recipe.row")]
    [Trait("Rule", "recipe.row-excluded")]
    public async Task An_excluded_row_that_changes_is_not_the_filters_answer()
    {
        var recipe = OpenOrder(filter: "#filter");
        recipe = recipe with
        {
            Steps = [recipe.Steps[0] with { Selector = "#orders tbody tr, #side tr" }, .. recipe.Steps.Skip(1)],
        };
        await using var site = await OpenAsync(recipe, "grid");
        // The step reads the excluded side table too: before the virtualized grid has drawn its rows, a round trip after
        // the page, the rows it finds are that table's alone, every one excluded, and it answers "excluded" at once
        // (recipe.row-excluded). Under the full suite's load it ran that early; this test is about the filter.
        await UntilAsync(site, "document.querySelectorAll('#orders tbody tr td').length > 0", "the grid did not draw its rows");
        RecipeGridPage.FilterDelay[site.Run] = 1000;
        await site.Browser.EvaluateAsync(
            "window.scrolls = 0; document.getElementById('orders').addEventListener('scroll', () => window.scrolls++);"
            + " document.getElementById('filter').addEventListener('input', () => setTimeout(() =>"
            + " document.getElementById('side-key').textContent = 'changed', 20)); true",
            CancellationToken.None);
        var runner = site.Circuit.GetRequiredService<IMentorRecipeRunner>();

        (await runner.RunAsync(MentorRecipeRegistry.Steps(recipe, new Dictionary<string, object?> { ["order"] = 280 }),
            CancellationToken.None)).Should().Be(new MentorRecipeOutcome(true, 2, null));

        (await OpenedAsync(site)).Should().Be(280);
        (await site.Browser.EvaluateAsync("window.scrolls", CancellationToken.None))
            .GetInt32().Should().Be(0, "a row that is never read changed: the step still waited for the filter's answer");
    }

    [BrowserFact]
    [Trait("Rule", "recipe.row-excluded")]
    public async Task A_step_without_a_filter_waits_for_a_grid_still_loading_and_does_not_take_it_for_an_area_all_excluded()
    {
        // BUG-252: begun before the virtualized grid had drawn a row, a step that reads the excluded side table too found
        // only its row, every one excluded, and answered "excluded" at once. Without a filter it now waits for a row it
        // may read, at most the step's while, before it answers.
        await using var site = await OpenAsync(Order(), "grid");
        await site.Browser.EvaluateAsync("document.getElementById('redraw').click(); true", CancellationToken.None);
        await UntilAsync(site, "[...document.querySelectorAll('#orders tbody tr')].filter(r => r.textContent.trim()).length === 0",
            "The grid kept its rows.");
        var runner = site.Circuit.GetRequiredService<IMentorRecipeRunner>();

        var run = runner.RunAsync([new("row", "#orders tbody tr, #side tr", "3", "td:nth-child(1)", "button.open")], CancellationToken.None);
        await Task.Delay(1000);
        run.IsCompleted.Should().BeFalse("the step waits for the grid's rows: the side table's alone are not every row");
        await site.Browser.EvaluateAsync("document.getElementById('drawn').click(); true", CancellationToken.None);

        (await run).Should().Be(new MentorRecipeOutcome(true, 1, null));
        (await OpenedAsync(site)).Should().Be(3);
    }

    [BrowserFact]
    [Trait("Rule", "recipe.row")]
    public async Task A_step_without_a_filter_waits_for_a_grid_that_shows_placeholder_rows_before_its_data()
    {
        // A grid that draws placeholder rows before its data: rows the step's selector finds, with an empty key cell.
        // They are no row the step may read, so the step waits for one with its key, as for a grid with no row yet.
        await using var site = await OpenAsync(Order());
        await site.Browser.EvaluateAsync("""
            (() => {
              const box = document.createElement('div');
              box.id = 'loading';
              box.style.cssText = 'height:200px;overflow-y:auto';
              const body = Array.from({ length: 10 }, () => '<tr class="placeholder"><td></td><td></td></tr>').join('');
              box.innerHTML = '<table><tbody>' + body + '</tbody></table>';
              document.body.appendChild(box);
              setTimeout(() => {
                box.querySelector('tbody').innerHTML = Array.from({ length: 60 }, (_, i) =>
                  `<tr><td>${i + 1}</td><td><button class="open" onclick="this.dataset.clicked='yes'">Open</button></td></tr>`).join('');
              }, 1000);
              return true;
            })()
            """, CancellationToken.None);
        var runner = site.Circuit.GetRequiredService<IMentorRecipeRunner>();

        (await runner.RunAsync([new("row", "#loading tbody tr", "45", "td:nth-child(1)", "button.open")], CancellationToken.None))
            .Should().Be(new MentorRecipeOutcome(true, 1, null), "the placeholders are no row the step may read: it waits for the data");
        (await site.Browser.EvaluateAsync("[...document.querySelectorAll('#loading button')].findIndex(b => b.dataset.clicked === 'yes') + 1",
            CancellationToken.None)).GetInt32().Should().Be(45);
    }

    [BrowserFact]
    [Trait("Rule", "recipe.row")]
    public async Task A_key_the_filter_does_not_find_ends_once_the_rows_changed_without_waiting_for_the_cap()
    {
        await using var site = await OpenAsync(OpenOrder(filter: "#filter"), "grid");
        var runner = site.Circuit.GetRequiredService<IMentorRecipeRunner>();
        var clock = System.Diagnostics.Stopwatch.StartNew();

        (await runner.RunAsync(MentorRecipeRegistry.Steps(OpenOrder(filter: "#filter"), new Dictionary<string, object?> { ["order"] = 999 }),
            CancellationToken.None)).Should().Be(new MentorRecipeOutcome(false, 0, "no-row"));

        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(4),
            "an answer of the page that holds no such row ends the wait, the 5 s cap is for a page that never answers");
        RecipeGridPage.Opened.ContainsKey(site.Run).Should().BeFalse();
    }

    [BrowserFact]
    [Trait("Rule", "recipe.row")]
    public async Task A_row_step_with_the_pages_filter_uses_it_before_scrolling()
    {
        await using var site = await OpenAsync(OpenOrder(filter: "#filter"), "grid");
        var runner = site.Circuit.GetRequiredService<IMentorRecipeRunner>();

        (await runner.RunAsync(MentorRecipeRegistry.Steps(OpenOrder(filter: "#filter"), new Dictionary<string, object?> { ["order"] = 280 }),
            CancellationToken.None)).Should().Be(new MentorRecipeOutcome(true, 2, null));

        (await OpenedAsync(site)).Should().Be(280);
        (await site.Browser.EvaluateAsync("document.getElementById('filter').value", CancellationToken.None)).GetString().Should().Be("280");
        (await site.Browser.EvaluateAsync("document.getElementById('orders').scrollTop", CancellationToken.None))
            .GetDouble().Should().Be(0, "the filter found the row: nothing was scrolled");
    }

    [BrowserFact]
    [Trait("Rule", "recipe.row")]
    [Trait("Rule", "recipe.element-missing")]
    public async Task A_row_that_is_not_there_after_the_last_row_is_not_done_and_the_recipe_turns_off()
    {
        await using var site = await OpenAsync(OpenOrder(), "grid");
        site.Chat.CallTool("open_order", new { order = 999 }).Say("There is no such order.");

        await TurnAsync(site, "Open order 999");

        RecipeGridPage.Opened.ContainsKey(site.Run).Should().BeFalse();
        site.Observer.Seen.Single(d => d.Outcome == MentorActionOutcome.Failed).Detail.Should().Be("step 1: no-row");
        site.App.Services.GetRequiredService<MentorRecipeRegistry>().IsOff("open_order").Should().BeTrue();
        (await site.Browser.EvaluateAsync("(() => { const o = document.getElementById('orders'); return o.scrollTop + o.clientHeight >= o.scrollHeight - 2; })()",
            CancellationToken.None)).GetBoolean().Should().BeTrue("it scrolled to the last row before giving up");
    }

    [BrowserFact]
    [Trait("Rule", "recipe.element-missing")]
    [Trait("Rule", "recipe.run")]
    [Trait("Rule", "form.sensitive")]
    public async Task The_runner_names_why_a_step_is_not_done_and_chooses_an_ARIA_option_by_its_label()
    {
        await using var site = await OpenAsync(Order());
        var runner = site.Circuit.GetRequiredService<IMentorRecipeRunner>();
        Task<MentorRecipeOutcome> Run(params MentorRecipeStepCall[] steps) => runner.RunAsync(steps, CancellationToken.None);
        static MentorRecipeStepCall S(string kind, string selector, string? value = null) => new(kind, selector, value);

        (await Run(S("set", "#hidden", "x"))).Should().Be(new MentorRecipeOutcome(false, 0, "hidden"));
        (await Run(S("set", "#customer", "Anna"), S("set", "#locked", "x"))).Should().Be(new MentorRecipeOutcome(false, 1, "disabled"));
        (await Run(S("click", "#secret"))).Should().Be(new MentorRecipeOutcome(false, 0, "excluded"));
        (await Run(S("click", "#nowhere"))).Should().Be(new MentorRecipeOutcome(false, 0, "missing"));
        (await Run(S("choose", "#country", "Spain"))).Should().Be(new MentorRecipeOutcome(false, 0, "no-option"));
        (await Run(S("check", "#customer", "true"))).Should().Be(new MentorRecipeOutcome(false, 0, "wrong-element"));
        // Story 1319 (form.sensitive): a password field, or one marked for a one-time code, is never written.
        (await Run(S("set", "#pin", "1234"))).Should().Be(new MentorRecipeOutcome(false, 0, "sensitive"));
        (await Run(S("set", "#code", "987654"))).Should().Be(new MentorRecipeOutcome(false, 0, "sensitive"));
        (await site.Browser.EvaluateAsync("document.getElementById('pin').value + document.getElementById('code').value",
            CancellationToken.None)).GetString().Should().BeEmpty();

        (await Run(S("click", "#size"), S("click", "#sizes li:last-child"))).Should().Be(new MentorRecipeOutcome(true, 2, null),
            "between two steps the page settles: the list the first click opens, from the circuit, is there for the second");
        (await Run(S("choose", "#size", "Large"), S("wait", "#size-chosen"))).Should().Be(new MentorRecipeOutcome(true, 2, null));
        (await Run(S("click", "#slow"), S("wait", "#slow-done"))).Should().Be(new MentorRecipeOutcome(true, 2, null),
            "a wait step waits for what the page shows a second later");
        (await Run(S("check", "#express", "true"), S("check", "#express", "true"))).Done.Should().BeTrue();
        (await site.Browser.EvaluateAsync("document.getElementById('express').checked", CancellationToken.None))
            .GetBoolean().Should().BeTrue("a box already checked is left as it is");
        (await site.Browser.EvaluateAsync("document.getElementById('size-chosen').textContent", CancellationToken.None))
            .GetString().Should().Be("Large");
    }

    [BrowserFact]
    [Trait("Rule", "recipe.row-excluded")]
    public async Task Rows_inside_data_mentor_exclude_are_never_read_whether_the_key_is_there_or_not()
    {
        await using var site = await OpenAsync(Order());
        var runner = site.Circuit.GetRequiredService<IMentorRecipeRunner>();
        Task<MentorRecipeOutcome> Row(string rows, string key, string? filter = null)
            => runner.RunAsync([new("row", rows, key, "td:nth-child(1)", "button.open", filter)], CancellationToken.None);
        async Task<string> Read(string script) => (await site.Browser.EvaluateAsync(script, CancellationToken.None)).ToString();
        Task<string> Filter(string id) => Read($"document.getElementById('{id}').value");
        Task<string> Clicked(string table) => Read($"Array.from(document.querySelectorAll('#{table} button')).map(b => b.dataset.clicked || '-').join()");

        // Every row excluded: "excluded" at once, the key there or not, and its filter not written.
        (await Row("#secrets tbody tr", "7", "#secret-filter")).Should().Be(new MentorRecipeOutcome(false, 0, "excluded"));
        (await Row("#secrets tbody tr", "8", "#secret-filter")).Should().Be(new MentorRecipeOutcome(false, 0, "excluded"),
            "the same answer whether the key is in the excluded rows or not: what they hold is never compared");
        (await Filter("secret-filter")).Should().BeEmpty("nothing is typed in the filter of an excluded table");
        (await Row("#secrets tbody tr", "7")).Should().Be(new MentorRecipeOutcome(false, 0, "excluded"), "without a filter too");

        // Excluded and other rows together: the excluded ones are no candidates, and a key that only they hold is no row.
        const string both = "#secrets tbody tr, #mixed tbody tr";
        (await Row(both, "7")).Should().Be(new MentorRecipeOutcome(false, 0, "no-row"), "7 is only in the excluded table");
        (await Row(both, "8")).Should().Be(new MentorRecipeOutcome(false, 0, "no-row"), "the same answer as for a key nowhere");
        (await Read("String(document.getElementById('secret-box').scrollTop)")).Should().Be("0", "an excluded area is not scrolled");
        (await Row("#mixed tbody tr", "2")).Should().Be(new MentorRecipeOutcome(false, 0, "no-row"), "an excluded row is skipped");
        (await Row("#mixed tbody tr", "3")).Should().Be(new MentorRecipeOutcome(false, 0, "no-row"),
            "an excluded key cell is not read, and is no match");

        // A filter: the other part's is written; an excluded one is not, and the step stops there.
        (await Row(both, "7", "#mixed-filter")).Should().Be(new MentorRecipeOutcome(false, 0, "no-row"));
        (await Filter("mixed-filter")).Should().Be("7", "the filter of the part not excluded gets the key");
        (await Row("#mixed tbody tr", "9", "#secret-filter")).Should().Be(new MentorRecipeOutcome(false, 0, "excluded"));
        (await Filter("secret-filter")).Should().BeEmpty();

        (await Row(both, "1")).Should().Be(new MentorRecipeOutcome(true, 1, null), "a row of the part not excluded is found");
        (await Clicked("secrets")).Should().Be("-,-,-");
        (await Clicked("mixed")).Should().Be("1,-,-");
    }

    /// <summary>
    /// The JavaScript runtime of a WebAssembly client, played by the browser of the site: the runner's module is imported
    /// and run in its page, with the steps as the hub sent them.
    /// </summary>
    private sealed class PageJs(CdpBrowser browser) : IJSRuntime, IJSObjectReference
    {
        private string _module = "";

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "import")
            {
                _module = (string)args![0]!;
                return (TValue)(object)this;
            }

            var steps = JsonSerializer.Serialize(args![0]);
            var answer = await browser.EvaluateAsync(
                $"import(new URL({JsonSerializer.Serialize(_module)}, document.baseURI).href).then(m => m.run({steps}))", cancellationToken);
            return answer.Deserialize<TValue>()!;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [BrowserFact]
    [Trait("Rule", "recipe.hub")]
    public async Task A_WebAssembly_client_runs_the_hubs_recipe_in_its_page_and_done_and_not_done_reach_the_model()
    {
        await using var site = await OpenAsync(Order());
        var observer = new RecordingActionObserver();
        await using var hub = await ServerAppFactory.CreateAsync(o => o.MemoryAutoCapture = false,
            configureServices: s => s.AddMentorActionObserver(observer));
        var registry = hub.Services.GetRequiredService<MentorRecipeRegistry>();
        registry.Add(Order());
        registry.Add(Order(new MentorRecipeStep(MentorRecipeStepKind.Click, "#nowhere")) with { Name = "create_order_again" });
        await using var client = RecipeHubTests.WasmClient(hub, new PageJs(site.Browser), "http://localhost/recipes/form");
        var orchestrator = client.GetRequiredService<IMentorOrchestrator>();
        hub.Chat.CallTool("create_order", Anna).Say("The order is saved.")
            .CallTool("create_order_again", Anna).Say("I could not do it.");

        await orchestrator.SendMessageAsync("Order 3 for Anna Rossi to France, express, ring twice").WaitAsync(Wait);
        var saved = await SavedAsync(site);
        saved.Customer.Should().Be("Anna Rossi");
        saved.Country.Should().Be("FR");
        saved.Note.Should().Be("Ring twice");

        await orchestrator.SendMessageAsync("Again").WaitAsync(Wait);
        await hub.Services.GetRequiredService<MentorActionDecisions>().FlushAsync();

        observer.Steps("create_order").Should().Equal(["Allowed/allowed", "Executed/completed"]);
        observer.Steps("create_order_again").Should().Equal(["Allowed/allowed", "Failed/element-missing"]);
        observer.Seen.Single(d => d.Outcome == MentorActionOutcome.Failed).Detail.Should().Be("step 6: missing");
        registry.IsOff("create_order_again").Should().BeTrue();
        registry.IsOff("create_order").Should().BeFalse();
    }
}

/// <summary>
/// Orders in a virtualized <c>QuickGrid</c> inside a container that scrolls, with a filter of its own: only the rows in
/// view are in the page. Its "Open" button tells the test the order opened.
/// </summary>
public sealed class RecipeGridPage : ComponentBase
{
    internal const int Count = 300;
    internal static readonly ConcurrentDictionary<string, int> Opened = new();

    /// <summary>How long the page takes to filter, in milliseconds, for a run: a server slower than a quiet while.</summary>
    internal static readonly ConcurrentDictionary<string, int> FilterDelay = new();

    /// <summary>
    /// Runs whose page renders at once when the filter is written, the rows as they were, before the filtered rows come:
    /// what an event's handler of Blazor Server does at its first await, here every time.
    /// </summary>
    internal static readonly ConcurrentDictionary<string, bool> Intermediate = new();

    /// <summary>Runs whose filter changes nothing: the rows stay as they were whatever is written in it.</summary>
    internal static readonly ConcurrentDictionary<string, bool> FilterIgnored = new();

    public sealed record Line(int Id, string Customer);

    private static readonly IQueryable<Line> All = Enumerable.Range(1, Count).Select(i => new Line(i, $"Customer {i}")).AsQueryable();

    [Inject] private IServiceProvider Services { get; set; } = null!;
    [Inject] private NavigationManager Navigation { get; set; } = null!;

    private string _filter = "";
    private int _renders;
    private int? _opened;

    private string Run => Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(Navigation.Uri).Query)
        .TryGetValue("run", out var run) ? run.ToString() : "";

    private bool _drawing;
    private TaskCompletionSource? _drawn;

    private IQueryable<Line> Items => _drawing ? Enumerable.Empty<Line>().AsQueryable()
        : string.IsNullOrWhiteSpace(_filter) || FilterIgnored.ContainsKey(Run)
        ? All
        : All.Where(l => l.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) == _filter.Trim());

    protected override void OnAfterRender(bool firstRender)
    {
        if (firstRender) RecipeFormPage.Circuits[Run] = Services;
    }

    private void Open(Line line)
    {
        _opened = line.Id;
        Opened[Run] = line.Id;
    }

    protected override void BuildRenderTree(RenderTreeBuilder b)
    {
        b.OpenElement(0, "input");
        b.AddAttribute(1, "id", "filter");
        b.AddAttribute(2, "value", _filter);
        b.AddAttribute(3, "oninput", EventCallback.Factory.Create<ChangeEventArgs>(this, async e =>
        {
            if (Intermediate.ContainsKey(Run))
            {
                _renders++;
                await Task.Yield();   // the render at the first await: the grid's class changes, its rows do not
            }
            if (FilterDelay.TryGetValue(Run, out var delay)) await Task.Delay(delay);
            _filter = e.Value?.ToString() ?? "";
        }));
        b.CloseElement();

        // A grid drawn again: no rows at first, its rows when #drawn is clicked, as a virtualized grid that draws its rows
        // only after it has measured itself, a round trip later.
        b.OpenElement(4, "button");
        b.AddAttribute(5, "id", "redraw");
        b.AddAttribute(6, "type", "button");
        b.AddAttribute(7, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, async () =>
        {
            _drawing = true;
            _drawn = new TaskCompletionSource();
            await _drawn.Task;   // the render at the first await: no rows
            _drawing = false;
        }));
        b.AddContent(8, "Redraw");
        b.CloseElement();
        b.OpenElement(21, "button");
        b.AddAttribute(22, "id", "drawn");
        b.AddAttribute(23, "type", "button");
        b.AddAttribute(24, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, () => _drawn?.TrySetResult()));
        b.AddContent(25, "Drawn");
        b.CloseElement();

        // Rows exactly as tall as ItemSize, as Virtualize needs: QuickGrid's own stylesheet is not served here.
        b.AddMarkupContent(9, "<style>#orders tr { height: 35px; } #orders td, #orders th { padding: 0 4px; border: 0; line-height: 35px; }</style>");
        b.OpenElement(10, "div");
        b.AddAttribute(11, "id", "orders");
        b.AddAttribute(12, "style", "height:300px;overflow-y:auto");
        b.OpenComponent<QuickGrid<Line>>(13);
        b.AddAttribute(14, "Items", Items);
        b.AddAttribute(18, "Class", "renders-" + _renders.ToString(System.Globalization.CultureInfo.InvariantCulture));
        b.AddAttribute(15, "Virtualize", true);
        b.AddAttribute(16, "ItemSize", 35f);
        b.AddAttribute(17, "ChildContent", (RenderFragment)(c =>
        {
            c.OpenComponent<PropertyColumn<Line, int>>(0);
            c.AddAttribute(1, "Property", (Expression<Func<Line, int>>)(l => l.Id));
            c.AddAttribute(2, "Title", "Order");
            c.CloseComponent();
            c.OpenComponent<PropertyColumn<Line, string>>(3);
            c.AddAttribute(4, "Property", (Expression<Func<Line, string>>)(l => l.Customer));
            c.CloseComponent();
            c.OpenComponent<TemplateColumn<Line>>(5);
            c.AddAttribute(6, "ChildContent", (RenderFragment<Line>)(line => r =>
            {
                r.OpenElement(0, "button");
                r.AddAttribute(1, "type", "button");
                r.AddAttribute(2, "class", "open");
                r.AddAttribute(3, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, () => Open(line)));
                r.AddContent(4, "Open");
                r.CloseElement();
            }));
            c.CloseComponent();
        }));
        b.CloseComponent();
        b.CloseElement();

        // A row the recipes may never read, among the rows of a step that names this table too.
        b.AddMarkupContent(19, "<table id=\"side\"><tbody><tr data-mentor-exclude><td id=\"side-key\">secret</td></tr></tbody></table>");
        if (_opened is { } opened) b.AddMarkupContent(20, $"<p id=\"opened\">Order {opened}</p>");
    }
}

/// <summary>
/// The root of the interactive test site: the page its address names (<c>/recipes/form</c>, <c>/recipes/grid</c>),
/// rendered interactive in the circuit, with no prerendering.
/// </summary>
public sealed class RecipeHostRoot : ComponentBase
{
    [Inject] private NavigationManager Navigation { get; set; } = null!;

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        var path = new Uri(Navigation.Uri).AbsolutePath;
        var page = path.EndsWith("/grid", StringComparison.Ordinal) ? typeof(RecipeGridPage)
            : path.EndsWith("/fill", StringComparison.Ordinal) ? typeof(FormFillPage)   // story 1319: a form registered
            : path.EndsWith("/grids", StringComparison.Ordinal) ? typeof(GridActionPage)   // story 1319 B: grids registered
            : typeof(RecipeFormPage);
        builder.OpenElement(0, "html");
        builder.AddMarkupContent(1, "<head><meta charset=\"utf-8\"><base href=\"/\"><title>Recipes</title></head>");
        builder.OpenElement(2, "body");
        builder.OpenComponent(3, page);
        builder.AddComponentRenderMode(new Microsoft.AspNetCore.Components.Web.InteractiveServerRenderMode(prerender: false));
        builder.CloseComponent();
        builder.AddMarkupContent(4, "<script src=\"_framework/blazor.web.js\"></script>");
        builder.CloseElement();
        builder.CloseElement();
    }
}

/// <summary>The pages of the interactive test site: the root renders the form whatever the page.</summary>
[PageRoute("/recipes/{page}")]
public sealed class RecipeHostPage : ComponentBase
{
    [Parameter] public string? Page { get; set; }
}

/// <summary>
/// An order form as an application writes it, interactive in the circuit. It tells the test its circuit's services and
/// what it saved, under the <c>run</c> of its address.
/// </summary>
public sealed class RecipeFormPage : ComponentBase
{
    internal static readonly ConcurrentDictionary<string, IServiceProvider> Circuits = new();
    internal static readonly ConcurrentDictionary<string, Order> Saved = new();

    public sealed class Order
    {
        public string? Customer { get; set; }
        public int Quantity { get; set; }
        public string? Country { get; set; } = "IT";
        public bool Express { get; set; }
        public string? Note { get; set; }
    }

    [Inject] private IServiceProvider Services { get; set; } = null!;
    [Inject] private NavigationManager Navigation { get; set; } = null!;

    private readonly Order _order = new();
    private bool _saved;
    private bool _sizeOpen;
    private string? _size;
    private bool _slow;

    private string Run => Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(Navigation.Uri).Query)
        .TryGetValue("run", out var run) ? run.ToString() : "";

    protected override void OnAfterRender(bool firstRender)
    {
        if (firstRender) Circuits[Run] = Services;
    }

    private void Save()
    {
        Saved[Run] = new Order
        {
            Customer = _order.Customer, Quantity = _order.Quantity, Country = _order.Country, Express = _order.Express, Note = _order.Note,
        };
        _saved = true;
    }

    protected override void BuildRenderTree(RenderTreeBuilder b)
    {
        b.OpenComponent<EditForm>(0);
        b.AddAttribute(1, "Model", _order);
        b.AddAttribute(2, "OnValidSubmit", EventCallback.Factory.Create<EditContext>(this, Save));
        b.AddAttribute(3, "ChildContent", (RenderFragment<EditContext>)(_ => f =>
        {
            Input<InputText, string?>(f, 0, "customer", _order.Customer, v => _order.Customer = v, () => _order.Customer);
            Input<InputNumber<int>, int>(f, 10, "quantity", _order.Quantity, v => _order.Quantity = v, () => _order.Quantity);

            f.OpenComponent<InputSelect<string?>>(20);
            f.AddAttribute(21, "id", "country");
            f.AddAttribute(22, "Value", _order.Country);
            f.AddAttribute(23, "ValueChanged", EventCallback.Factory.Create<string?>(this, v => _order.Country = v));
            f.AddAttribute(24, "ValueExpression", (Expression<Func<string?>>)(() => _order.Country));
            f.AddAttribute(25, "ChildContent", (RenderFragment)(o => o.AddMarkupContent(0,
                "<option value=\"IT\">Italy</option><option value=\"FR\">France</option><option value=\"DE\">Germany</option>")));
            f.CloseComponent();

            Input<InputCheckbox, bool>(f, 30, "express", _order.Express, v => _order.Express = v, () => _order.Express);

            // A field bound on every key, as @bind:event="oninput" writes it.
            f.OpenElement(40, "input");
            f.AddAttribute(41, "id", "note");
            f.AddAttribute(42, "value", BindConverter.FormatValue(_order.Note));
            f.AddAttribute(43, "oninput", EventCallback.Factory.CreateBinder(this, v => _order.Note = v, _order.Note ?? string.Empty));
            f.CloseElement();

            // What the runner must never use: hidden, disabled, excluded.
            f.AddMarkupContent(50, "<input id=\"hidden\" style=\"display:none\"><input id=\"locked\" disabled>"
                + "<input id=\"pin\" type=\"password\"><input id=\"code\" autocomplete=\"one-time-code\">"
                + "<div data-mentor-exclude><button type=\"button\" id=\"secret\">Secret</button></div>");

            // What the runner must never read: a table with its filter inside an excluded area; next to it, a table with an
            // excluded row and an excluded key cell among others, and its own filter.
            const string open = "<button type=\"button\" class=\"open\" onclick=\"this.dataset.clicked='1'\">Open</button>";
            f.AddMarkupContent(55, "<div id=\"secret-box\" data-mentor-exclude style=\"height:40px;overflow-y:auto\"><input id=\"secret-filter\">"
                + $"<table id=\"secrets\"><tbody><tr><td>7</td><td>{open}</td></tr><tr><td>70</td><td>{open}</td></tr><tr><td>71</td><td>{open}</td></tr></tbody></table></div>"
                + "<input id=\"mixed-filter\"><table id=\"mixed\"><tbody>"
                + $"<tr><td>1</td><td>{open}</td></tr>"
                + $"<tr data-mentor-exclude><td>2</td><td>{open}</td></tr>"
                + $"<tr><td data-mentor-exclude>3</td><td>{open}</td></tr></tbody></table>");

            f.AddMarkupContent(60, "<button type=\"submit\" id=\"save\">Save</button>");
        }));
        b.CloseComponent();

        if (_saved) b.AddMarkupContent(70, "<p id=\"saved\">Saved</p>");

        // An ARIA combobox, as component libraries write it: the options appear once it is opened.
        b.OpenElement(80, "button");
        b.AddAttribute(81, "type", "button");
        b.AddAttribute(82, "id", "size");
        b.AddAttribute(83, "role", "combobox");
        b.AddAttribute(84, "aria-controls", "sizes");
        b.AddAttribute(85, "aria-expanded", _sizeOpen ? "true" : "false");
        b.AddAttribute(86, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, () => _sizeOpen = !_sizeOpen));
        b.AddContent(87, _size ?? "Size");
        b.CloseElement();
        if (_sizeOpen)
        {
            b.OpenElement(90, "ul");
            b.AddAttribute(91, "id", "sizes");
            b.AddAttribute(92, "role", "listbox");
            foreach (var size in new[] { "Small", "Large" })
            {
                b.OpenElement(93, "li");
                b.SetKey(size);
                b.AddAttribute(94, "role", "option");
                b.AddAttribute(95, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, () => { _size = size; _sizeOpen = false; }));
                b.AddContent(96, size);
                b.CloseElement();
            }
            b.CloseElement();
        }
        // A button whose result shows up a second later, as after a call to a server.
        b.OpenElement(110, "button");
        b.AddAttribute(111, "type", "button");
        b.AddAttribute(112, "id", "slow");
        b.AddAttribute(113, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, async () => { await Task.Delay(1000); _slow = true; }));
        b.AddContent(114, "Slow");
        b.CloseElement();
        if (_slow) b.AddMarkupContent(115, "<p id=\"slow-done\">Done</p>");

        if (_size is not null)
        {
            b.OpenElement(100, "span");
            b.AddAttribute(101, "id", "size-chosen");
            b.AddContent(102, _size);
            b.CloseElement();
        }
    }

    private void Input<TInput, TValue>(RenderTreeBuilder f, int seq, string id, TValue value, Action<TValue> set, Expression<Func<TValue>> expression)
        where TInput : InputBase<TValue>
    {
#pragma warning disable ASP0006 // a region of its own for each field: the sequence numbers inside it are literals
        f.OpenRegion(seq);
#pragma warning restore ASP0006
        f.OpenComponent<TInput>(0);
        f.AddAttribute(1, "id", id);
        f.AddAttribute(2, "Value", value);
        f.AddAttribute(3, "ValueChanged", EventCallback.Factory.Create<TValue>(this, set));
        f.AddAttribute(4, "ValueExpression", expression);
        f.CloseComponent();
        f.CloseRegion();
    }
}
