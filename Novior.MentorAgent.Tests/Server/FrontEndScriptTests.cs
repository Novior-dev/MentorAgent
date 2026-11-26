using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Scanner.Cdp;
using Novior.MentorAgent.Server.Endpoints;
using Novior.MentorAgent.Server.Extensions;
using Novior.MentorAgent.Tests.App;
using Novior.MentorAgent.Tests.Docs;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Xunit;

namespace Novior.MentorAgent.Tests.Server;

/// <summary>
/// Story 1641, slice A — <c>/mentor/agent.js</c>, the recipes for a front end that is not Blazor: served by the server
/// package with the hub's CORS, loaded by a real React 19 page of another origin in Edge or Chrome, tied to the page's own
/// <c>@microsoft/signalr</c> connection. The React and SignalR files are test fixtures (<c>FrontEnd/SOURCE.txt</c>).
/// </summary>
public sealed class FrontEndScriptTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);
    private const string Policy = "Front";

    // ── The script over HTTP ─────────────────────────────────────────────────

    [Fact]
    [Trait("Rule", "recipe.front-end")]
    public async Task The_script_is_the_runner_and_the_front_ends_part_cached_for_a_year_with_its_version_only()
    {
        await using var app = await ServerAppFactory.CreateAsync();

        var versioned = await app.Client.GetAsync($"/mentor/agent.js?v={MentorClientScript.Version}");
        versioned.StatusCode.Should().Be(HttpStatusCode.OK);
        versioned.Content.Headers.ContentType!.MediaType.Should().Be("text/javascript", "a module runs only with a JavaScript type");
        versioned.Headers.CacheControl!.ToString().Should().Contain("immutable").And.Contain("max-age=31536000");
        var text = await versioned.Content.ReadAsStringAsync();
        text.Should().Contain("export async function run(steps, timing)", "the runner, the same file the Blazor widget imports")
            .And.Contain("export function attach(connection, options)").And.Contain("export function path()");
        text.Should().StartWith(File.ReadAllText(Path.Combine(RepoFiles.Root, "Novior.MentorAgent.Abstractions", "wwwroot", "js",
            "MentorRecipeRunner.js")).TrimStart('﻿'), "one source for the runner");
        MentorClientScript.Version.Should().NotContain("+", "the README's ?v= is the package's version, without the commit");

        var other = await app.Client.GetAsync("/mentor/agent.js?v=0.9.0");
        other.Headers.CacheControl!.NoCache.Should().BeTrue("another version is checked with the server each time");

        using var again = new HttpRequestMessage(HttpMethod.Get, "/mentor/agent.js");
        again.Headers.TryAddWithoutValidation("If-None-Match", versioned.Headers.ETag!.Tag);
        (await app.Client.SendAsync(again)).StatusCode.Should().Be(HttpStatusCode.NotModified);
    }

    [Fact]
    [Trait("Rule", "recipe.front-end-cors")]
    public async Task The_script_takes_the_cors_the_hub_requires_and_not_its_authorization()
    {
        await using var app = await ServerAppFactory.CreateAsync(
            configureServices: s => s.AddCors(o => o.AddPolicy(Policy, p => p.WithOrigins("http://front.test").AllowCredentials())),
            configureApp: a => a.UseCors(),
            configureTransports: e => e.RequireCors(Policy).RequireAuthorization("NoOne").WithMetadata(new HubOnly()),
            configureServicesFirst: s => s.AddAuthorization(o =>
            {
                o.AddPolicy("NoOne", p => p.RequireRole("nobody"));
                // A host that requires a user everywhere: the script is still served to anyone.
                o.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireRole("nobody").Build();
            }));

        using var script = new HttpRequestMessage(HttpMethod.Get, "/mentor/agent.js");
        script.Headers.Add("Origin", "http://front.test");
        var answer = await app.Client.SendAsync(script);
        answer.StatusCode.Should().Be(HttpStatusCode.OK, "the script is the library's code, loaded without the front end's token");
        answer.Headers.GetValues("Access-Control-Allow-Origin").Should().Equal(["http://front.test"]);

        using var other = new HttpRequestMessage(HttpMethod.Get, "/mentor/agent.js");
        other.Headers.Add("Origin", "http://elsewhere.test");
        (await app.Client.SendAsync(other)).Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse("the hub's policy, not any origin");

        (await app.Client.PostAsync("/mentor-hub/negotiate?negotiateVersion=1", null)).StatusCode
            .Should().BeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden], "the hub keeps its authorization");

        var endpoint = app.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>().Endpoints
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>().Single(e => e.RoutePattern.RawText == "/mentor/agent.js");
        endpoint.Metadata.OfType<Microsoft.AspNetCore.Cors.Infrastructure.ICorsMetadata>().Should().NotBeEmpty();
        endpoint.Metadata.OfType<HubOnly>().Should().BeEmpty("of the hub's conventions the script takes the CORS only");
        endpoint.Metadata.OfType<Microsoft.AspNetCore.Authorization.IAuthorizeData>().Should().BeEmpty();
    }

    /// <summary>A metadata of the hub's conventions that is not CORS.</summary>
    private sealed class HubOnly;

    // ── A React front end of another origin, in a browser ────────────────────

    private static MentorRecipe Note() => new()
    {
        Name = "write_note",
        Description = "Writes and saves a note",
        Route = "/front",
        Parameters = [new("note", "The note"), new("title", "Its title")],
        Steps =
        [
            new(MentorRecipeStepKind.Set, "#note", "{note}"),
            new(MentorRecipeStepKind.Set, "#title", "{title}"),
            new(MentorRecipeStepKind.Click, "#save"),
            new(MentorRecipeStepKind.Wait, "#saved"),
        ],
    };

    /// <summary>The application with MentorAgent's server, the front end on another port, and a browser on the front end.</summary>
    private sealed class Front : IAsyncDisposable
    {
        internal required WebApplication Backend { get; init; }
        internal required WebApplication Page { get; init; }
        internal required CdpBrowser Browser { get; init; }
        internal required FakeChatClient Chat { get; init; }
        internal required RecordingActionObserver Observer { get; init; }

        internal async Task<JsonElement> EvalAsync(string expression) => await Browser.EvaluateAsync(expression, CancellationToken.None);

        internal async Task<string?> TextAsync(string expression)
        {
            var value = await EvalAsync(expression);
            return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }

        public async ValueTask DisposeAsync()
        {
            await Browser.DisposeAsync();
            await Page.DisposeAsync();
            await Backend.DisposeAsync();
        }
    }

    private static readonly string[] Fixtures =
        ["react.production.js", "react-dom.production.js", "react-dom-client.production.js", "scheduler.production.js", "signalr.min.js",
         "angular.main.js"];

    private static async Task<Front> OpenAsync(bool cors = true, string routes = "", bool autoMentor = false, bool ready = true,
        bool angular = false, IMentorRecipeSource? source = null, FakeChatClient? classifier = null,
        Novior.MentorAgent.Abstractions.Interfaces.IMentorAssistantStatus? status = null)
    {
        // The front end first: the server's CORS names its origin.
        var page = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        page.WebHost.UseUrls("http://127.0.0.1:0");
        page.Logging.ClearProviders();
        var front = page.Build();
        string? backendUrl = null;
        var folder = Path.Combine(RepoFiles.Root, "Novior.MentorAgent.Tests", "FrontEnd");
        front.MapGet("/fixtures/{name}", (string name) => Fixtures.Contains(name)
            ? Results.File(Path.Combine(folder, name), "text/javascript")
            : Results.NotFound());
        front.MapFallback(() => Results.Content(angular ? AngularHtml(backendUrl!) : Html(backendUrl!, routes), "text/html"));
        await front.StartAsync();
        var frontUrl = front.Urls.First();

        var chat = new FakeChatClient();
        var observer = new RecordingActionObserver();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddCors(o => o.AddPolicy(Policy, p => p.WithOrigins(frontUrl).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
        builder.Services.AddMentorActionObserver(observer);
        if (source is not null) builder.Services.AddSingleton(source);
        if (status is not null) builder.Services.AddSingleton(status);
        builder.Services.AddMentorAgent(o =>
        {
            o.AppName = "Front";
            o.ChatClient = chat;
            o.ScanAssemblies = [typeof(TestOrderService).Assembly];
            o.MemoryAutoCapture = false;
            o.CheckAnnouncedActions = classifier is not null;
            if (classifier is not null) o.ClassifierChatClient = classifier;
            o.AutoMentor.Enabled = autoMentor;
        });
        builder.Services.AddMentorAgentServer();
        var backend = builder.Build();
        if (cors) backend.UseCors(Policy);
        backend.MapMentorAgentServer();
        await backend.StartAsync();
        backendUrl = backend.Urls.First();
        backend.Services.GetRequiredService<MentorRecipeRegistry>().Add(Note());
        if (angular) backend.Services.GetRequiredService<MentorRecipeRegistry>().Add(Order());

        var browser = await CdpBrowser.StartAsync(CdpBrowser.Find(null)!, CancellationToken.None);
        var site = new Front { Backend = backend, Page = front, Browser = browser, Chat = chat, Observer = observer };
        await browser.OpenAsync(new Uri($"{frontUrl}/front"), Wait, CancellationToken.None);
        if (ready)
        {
            var until = DateTime.UtcNow + Wait;
            while ((await site.EvalAsync("window.front && (window.front.ready || window.front.error || false)")).ValueKind != JsonValueKind.True)
            {
                if (await site.TextAsync("window.front && window.front.error") is { } error)
                    throw new InvalidOperationException("The front end failed: " + error);
                if (DateTime.UtcNow > until) throw new TimeoutException("The front end did not connect.");
                await Task.Delay(100);
            }
        }
        return site;
    }

    /// <summary>
    /// The front end: React 19 from its CommonJS files with a small require, a controlled field, a field Angular-like that
    /// reads its value when left (updateOn: 'blur'), links; then the script of the server, and the page's own connection.
    /// </summary>
    private static string Html(string backend, string routes) => $$"""
        <!doctype html>
        <html><head><meta charset="utf-8"><title>Front</title></head>
        <body>
        <nav><a href="/front/orders">Orders</a> <a href="/front/orders/42?tab=2#top">Order</a> <a href="https://elsewhere.test/x">Out</a>
        <span data-mentor-exclude><a href="/front/hidden">Hidden</a></span></nav>
        <div id="root"></div>
        <script src="/fixtures/signalr.min.js"></script>
        <script type="module">
        window.front = { ready: false };
        try {
          const files = { 'react': 'react.production.js', 'scheduler': 'scheduler.production.js',
            'react-dom': 'react-dom.production.js', 'react-dom/client': 'react-dom-client.production.js' };
          const texts = {};
          await Promise.all(Object.entries(files).map(async ([name, file]) => texts[name] = await (await fetch('/fixtures/' + file)).text()));
          const loaded = {};
          const require = name => {
            if (loaded[name]) return loaded[name].exports;
            const module = { exports: {} };
            loaded[name] = module;
            new Function('module', 'exports', 'require', 'process', texts[name])(module, module.exports, require, { env: { NODE_ENV: 'production' } });
            return module.exports;
          };
          const React = require('react');
          const { createRoot } = require('react-dom/client');
          const h = React.createElement;
          function Form() {
            const [note, setNote] = React.useState('');
            const [saved, setSaved] = React.useState(null);
            return h('form', { onSubmit: e => { e.preventDefault(); setSaved(note); } },
              h('input', { id: 'note', value: note, onChange: e => setNote(e.target.value) }),
              h('span', { id: 'state' }, note),
              h('input', { id: 'title', onBlur: e => { window.front.titleOnBlur = e.target.value; } }),
              h('button', { id: 'save', type: 'submit' }, 'Save'),
              saved === null ? null : h('p', { id: 'saved' }, saved));
          }
          createRoot(document.getElementById('root')).render(h(Form));

          const mentor = await import('{{backend}}/mentor/agent.js?v={{MentorClientScript.Version}}');
          window.front.loaded = true;
          const connection = new signalR.HubConnectionBuilder().withUrl('{{backend}}/mentor-hub', { withCredentials: true }).build();
          await connection.start();
          window.front.mentor = mentor.attach(connection{{routes}});
          await connection.invoke('UpdatePageContext', { pageName: 'Front', contextData: {}, uiActions: [], path: mentor.path() });
          window.front.send = text => connection.invoke('SendMessage', text, null);
          window.front.ready = true;
        } catch (e) {
          window.front.error = String((e && e.message) || e);
        }
        </script>
        </body></html>
        """;

    [BrowserFact]
    [Trait("Rule", "recipe.front-end")]
    public async Task A_recipe_fills_a_controlled_field_of_a_React_page_and_the_value_stays_in_Reacts_state()
    {
        await using var site = await OpenAsync();
        site.Chat.CallTool("write_note", new { note = "Ring twice", title = "Weekly" }).Say("The note is saved.");

        await site.EvalAsync("window.front.send('Write the note'); true");

        var until = DateTime.UtcNow + Wait;
        while (await site.TextAsync("document.getElementById('saved') && document.getElementById('saved').textContent") is null)
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("The form was not saved.");
            await Task.Delay(100);
        }
        (await site.TextAsync("document.getElementById('saved').textContent")).Should()
            .Be("Ring twice", "what React saved is its state, which a value written past React would not change");
        (await site.TextAsync("document.getElementById('state').textContent")).Should().Be("Ring twice");
        (await site.TextAsync("window.front.titleOnBlur")).Should().Be("Weekly", "a field that reads its value when left is left");

        // The answer reaches the server after the page saved: the call ends then.
        until = DateTime.UtcNow + Wait;
        do
        {
            await site.Backend.Services.GetRequiredService<MentorActionDecisions>().FlushAsync();
            if (site.Observer.Steps("write_note").Count >= 2 || DateTime.UtcNow > until) break;
            await Task.Delay(100);
        } while (true);
        site.Observer.Steps("write_note").Should().Equal(["Allowed/allowed", "Executed/completed"]);
    }

    // ── Story 1290: the assistant turned off or on ───────────────────────────

    [BrowserFact]
    public async Task The_page_reads_the_assistant_off_when_it_connects_and_hears_each_change_until_it_stops_listening()
    {
        var status = new TestAssistantSwitch(on: false);
        await using var site = await OpenAsync(status: status);

        async Task<string> Seen() => (await site.EvalAsync("JSON.stringify([window.front.mentor.assistantIsOn(), window.front.seen])")).GetString()!;
        async Task UntilSeen(string expected)
        {
            var until = DateTime.UtcNow + Wait;
            while (await Seen() != expected)
            {
                if (DateTime.UtcNow > until) throw new TimeoutException($"The page saw {await Seen()}, not {expected}.");
                await Task.Delay(50);
            }
        }

        var broadcaster = site.Backend.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>()
            .OfType<Novior.MentorAgent.Server.Hubs.MentorAssistantStatusBroadcaster>().Single();
        await site.EvalAsync("window.front.seen = []; true");
        await UntilSeen("[false,[]]");   // read with GetAssistantStatus when the connection opened
        await site.EvalAsync("""
            window.front.mentor.onAssistantStatus(() => { throw new Error('a listener that throws'); });
            window.front.stop = window.front.mentor.onAssistantStatus(on => window.front.seen.push(on));
            true
            """);

        status.Set(true);
        await broadcaster.Sends;
        await UntilSeen("[true,[true]]");

        status.Set(false);
        await broadcaster.Sends;
        await UntilSeen("[false,[true,false]]");

        await site.EvalAsync("window.front.stop(); true");
        status.Set(true);
        await broadcaster.Sends;
        await UntilSeen("[true,[true,false]]");   // the value still follows; the listener that stopped hears nothing
    }

    [BrowserFact]
    public async Task Without_a_status_on_the_server_the_page_has_the_assistant_on()
    {
        await using var site = await OpenAsync();
        await Task.Delay(300);

        (await site.EvalAsync("window.front.mentor.assistantIsOn()")).ValueKind.Should().Be(JsonValueKind.True);
    }

    // ── Story 1642: an Angular front end, its reactive form and its Routes ──────

    private static MentorRecipe Order() => new()
    {
        Name = "send_order",
        Description = "Fills and sends an order",
        Route = "/front",
        Parameters = [new("customer", "The customer"), new("product", "The product"), new("note", "A note")],
        Steps =
        [
            new(MentorRecipeStepKind.Set, "#customer", "{customer}"),
            new(MentorRecipeStepKind.Choose, "#product", "{product}"),
            new(MentorRecipeStepKind.Check, "#gift"),
            new(MentorRecipeStepKind.Set, "#note", "{note}"),
            new(MentorRecipeStepKind.Click, "#send"),
            new(MentorRecipeStepKind.Wait, "#sent"),
        ],
    };

    /// <summary>
    /// The front end: Angular 22 built ahead of time for production (<c>FrontEnd/angular.main.js</c>, from
    /// <c>FrontEnd/angular/main.ts</c>), zoneless, with a reactive form whose Send stays disabled until the form is valid;
    /// then the script of the server, tied to the page's own connection with the application's own Routes.
    /// </summary>
    private static string AngularHtml(string backend) => $$"""
        <!doctype html>
        <html><head><meta charset="utf-8"><title>Front</title></head>
        <body>
        <app-order></app-order>
        <script src="/fixtures/signalr.min.js"></script>
        <script type="module">
        window.front = { ready: false };
        try {
          const app = await import('/fixtures/angular.main.js');
          await window.angular.ready;
          const mentor = await import('{{backend}}/mentor/agent.js?v={{MentorClientScript.Version}}');
          window.front.loaded = true;
          const connection = new signalR.HubConnectionBuilder().withUrl('{{backend}}/mentor-hub', { withCredentials: true }).build();
          await connection.start();
          window.front.mentor = mentor.attach(connection, { routes: app.routes });
          await connection.invoke('UpdatePageContext', { pageName: 'Front', contextData: {}, uiActions: [], path: mentor.path() });
          window.front.send = text => connection.invoke('SendMessage', text, null);
          window.front.ready = true;
        } catch (e) {
          window.front.error = String((e && e.message) || e);
        }
        </script>
        </body></html>
        """;

    [BrowserFact]
    [Trait("Rule", "recipe.front-end")]
    [Trait("Rule", "page.front-end-routes")]
    public async Task A_recipe_fills_and_sends_an_Angular_reactive_form_and_its_Routes_reach_the_server()
    {
        await using var site = await OpenAsync(angular: true, autoMentor: true);
        site.Chat.CallTool("send_order", new { customer = "Ada", product = "Fountain pen", note = "Gift wrap" }).Say("The order is sent.");

        await site.EvalAsync("window.front.send('Send the order'); true");

        var until = DateTime.UtcNow + Wait;
        while (await site.TextAsync("document.getElementById('sent') && document.getElementById('sent').textContent") is null)
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("The form was not sent.");
            await Task.Delay(100);
        }
        using (var sent = JsonDocument.Parse((await site.TextAsync("document.getElementById('sent').textContent"))!))
        {
            var value = sent.RootElement.GetProperty("value");
            value.GetProperty("customer").GetString().Should().Be("Ada", "the form's value is Angular's, which a value written past it would not change");
            value.GetProperty("product").GetString().Should().Be("pen", "the option chosen by its label");
            value.GetProperty("gift").GetBoolean().Should().BeTrue();
            value.GetProperty("note").GetString().Should().Be("Gift wrap", "a control with updateOn: 'blur' takes its value when left");
            sent.RootElement.GetProperty("touched").GetBoolean().Should().BeTrue("a field written is left, as a user leaves it");
        }
        (await site.TextAsync("document.getElementById('state').textContent")).Should().Be("Ada");

        until = DateTime.UtcNow + Wait;
        do
        {
            await site.Backend.Services.GetRequiredService<MentorActionDecisions>().FlushAsync();
            if (site.Observer.Steps("send_order").Count >= 2 || DateTime.UtcNow > until) break;
            await Task.Delay(100);
        } while (true);
        site.Observer.Steps("send_order").Should().Equal(["Allowed/allowed", "Executed/completed"]);

        var pages = await ClientPagesAsync(site, 3);
        pages.Should().Equal([("/orders", "/orders"), ("/orders/{id}", "/orders/{id}"), ("/{*rest}", "/{*rest}")],
            "the application's own Routes: the redirect is not a page, the children are under 'orders', and the classes, "
            + "renamed by the production build, are named by their routes");
    }

    [BrowserFact]
    [Trait("Rule", "recipe.front-end")]
    public async Task A_recipe_of_a_source_is_offered_in_the_first_turn_of_a_front_ends_connection()
    {
        // 1642 B, the live proof: the first turn after the server started announced the order and called nothing. Here
        // the sample's order of calls: a new connection, attach, the page context with its path, the message at once.
        var source = new Pages.RecipeSourceTests.Source
        {
            Recipes =
            [
                new MentorRecipeDefinition
                {
                    Name = "send_note",
                    Description = "Writes and saves a note",
                    Route = "/front",
                    Parameters = [new MentorRecipeParameterDefinition { Name = "note", Description = "The note" }],
                    Steps =
                    [
                        new MentorRecipeStepDefinition { Kind = MentorRecipeStepKind.Set, Selector = "#note", Value = "{note}" },
                        new MentorRecipeStepDefinition { Kind = MentorRecipeStepKind.Click, Selector = "#save" },
                    ],
                },
            ],
        };
        await using var site = await OpenAsync(source: source);
        site.Chat.Say("Saved.");

        await site.EvalAsync("window.front.send('Save the note Ring twice'); true");

        var until = DateTime.UtcNow + Wait;
        while (site.Chat.Calls.Count == 0)
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("No request reached the model.");
            await Task.Delay(50);
        }
        site.Chat.Calls[0].ToolNames.Should().Contain("send_note",
            "the recipe of the page shown is offered from the first request of the first turn, as the page's own action");
    }

    [BrowserFact]
    [Trait("Rule", "recipe.front-end")]
    public async Task A_reply_that_says_a_recipe_was_done_without_calling_it_gets_one_more_request_that_runs_it()
    {
        // 1642 B, the live proof's first turn: "Order sent" with no call. The announced-action check covers a recipe too.
        var source = new Pages.RecipeSourceTests.Source
        {
            Recipes =
            [
                new MentorRecipeDefinition
                {
                    Name = "send_note",
                    Description = "Writes and saves a note",
                    Route = "/front",
                    Parameters = [new MentorRecipeParameterDefinition { Name = "note", Description = "The note" }],
                    Steps =
                    [
                        new MentorRecipeStepDefinition { Kind = MentorRecipeStepKind.Set, Selector = "#note", Value = "{note}" },
                        new MentorRecipeStepDefinition { Kind = MentorRecipeStepKind.Click, Selector = "#save" },
                        new MentorRecipeStepDefinition { Kind = MentorRecipeStepKind.Wait, Selector = "#saved" },
                    ],
                },
            ],
        };
        var classifier = new FakeChatClient().Say("CLAIMED send_note");
        await using var site = await OpenAsync(source: source, classifier: classifier);
        site.Chat.Say("The note is saved.").CallTool("send_note", new { note = "Ring twice" }).Say("The note is saved.");

        await site.EvalAsync("window.front.send('Save the note Ring twice'); true");

        var until = DateTime.UtcNow + Wait;
        while (await site.TextAsync("document.getElementById('saved') && document.getElementById('saved').textContent") is null)
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("The note was not saved.");
            await Task.Delay(100);
        }
        (await site.TextAsync("document.getElementById('saved').textContent")).Should().Be("Ring twice");
        classifier.Calls.Should().NotBeEmpty("the reply said it was done, and no tool ran: the check asks");
        site.Chat.Calls[1].Messages[^1].Text.Should().Contain("send_note", "the second request names the recipe to call");
    }

    [BrowserFact]
    [Trait("Rule", "recipe.front-end-cors")]
    public async Task A_front_end_of_another_origin_runs_the_script_with_the_servers_cors_and_not_without()
    {
        await using (var with = await OpenAsync(cors: true))
            (await with.EvalAsync("window.front.loaded === true")).GetBoolean().Should().BeTrue();

        await using var without = await OpenAsync(cors: false, ready: false);
        var until = DateTime.UtcNow + Wait;
        string? error;
        while ((error = await without.TextAsync("window.front && window.front.error")) is null)
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("The page neither failed nor loaded the script.");
            await Task.Delay(100);
        }
        error.Should().Contain("import", "the browser refuses a module of another origin without CORS");
        (await without.EvalAsync("window.front.loaded === true")).GetBoolean().Should().BeFalse();
    }

    [BrowserFact]
    [Trait("Rule", "page.front-end-routes")]
    public async Task The_routes_of_a_front_end_reach_the_server_from_its_configuration()
    {
        await using var site = await OpenAsync(routes: ", { routes: ['/front', '/front/orders/:id', 'front/about', { route: '/front/:rest?', component: 'Rest' }, '**', 'bad route'] }",
            autoMentor: true);

        var pages = await ClientPagesAsync(site, 5);
        pages.Should().Equal([
            ("/front", "/front"),
            ("/front/orders/{id}", "/front/orders/{id}"),
            ("/front/about", "/front/about"),
            ("/front/{rest?}", "Rest"),
            ("/{*rest}", "/{*rest}"),
        ], "':id' is a parameter, '**' a catch-all, a route with white space is not one");
    }

    [BrowserFact]
    [Trait("Rule", "page.front-end-routes")]
    public async Task The_routes_of_a_front_end_can_be_its_routers_own_with_their_children()
    {
        // Story 1642: Angular's Routes as the application gives them to provideRouter, and React Router's route objects.
        const string routes = """
            , { routes: [
                { path: '', component: 'Home' },
                { path: 'orders', component: class OrdersShell {}, children: [
                    { path: '', component: class OrderList {} },
                    { path: ':id', component: class OrderDetail {} } ] },
                { path: 'old', redirectTo: 'orders' },
                { path: 'admin', children: [ { path: 'users/:name?', component: 'Users' }, { matcher: () => null, component: 'Matched' } ] },
                { path: 'report', loadComponent: () => null },
                { path: 'shop', element: null, children: [ { index: true }, { path: '/absolute' } ] },
                { path: '**', component: class NotFound {} } ] }
            """;
        await using var site = await OpenAsync(routes: routes.ReplaceLineEndings(" "), autoMentor: true);

        var pages = await ClientPagesAsync(site, 8);
        pages.Should().Equal([
            ("/", "Home"),
            ("/orders", "/orders"),
            ("/orders/{id}", "/orders/{id}"),
            ("/admin/users/{name?}", "Users"),
            ("/report", "/report"),
            ("/shop", "/shop"),
            ("/absolute", "/absolute"),
            ("/{*rest}", "/{*rest}"),
        ], "a child's path is under its parent's, '' and index are the parent's own page, an entry with children is not a page "
           + "itself, a redirect and a matcher are not pages, and a class is named by its route");
    }

    [BrowserFact]
    [Trait("Rule", "page.front-end-routes")]
    public async Task Without_a_configuration_the_routes_are_the_links_of_the_page_of_its_origin_without_query_or_fragment()
    {
        await using var site = await OpenAsync(autoMentor: true);

        var pages = await ClientPagesAsync(site, 3);
        pages.Select(p => p.Route).Should().Equal(["/front", "/front/orders", "/front/orders/42"],
            "the page itself, then its links of its own origin; an excluded link and another origin's are not routes");
    }

    private static async Task<List<(string Route, string? Component)>> ClientPagesAsync(Front site, int count)
    {
        var directory = site.Backend.Services.GetRequiredService<MentorPageDirectory>();
        var until = DateTime.UtcNow + Wait;
        List<(string, string?)> pages;
        while ((pages = [.. directory.Pages().Where(p => p.Origin == MentorPageDirectory.PageOrigin.Client).Select(p => (p.Route, p.Component))])
               .Count < count)
        {
            if (DateTime.UtcNow > until) break;
            await Task.Delay(100);
        }
        return pages;
    }
}
