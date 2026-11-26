using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Blazor.Extensions;
using Novior.MentorAgent.Blazor.Models;
using Novior.MentorAgent.Blazor.Services;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Server.Hubs;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Xunit;

namespace Novior.MentorAgent.Tests.Pages;

/// <summary>
/// Story 1638 (L4), slice C — a recipe on a WebAssembly client: the client sends the path of its page with the page
/// context, the hub offers that page's recipes, asks the client to run one (<c>RecipeRequested</c>), and the model waits
/// for the answer the client sends with <c>POST /mentor/recipe-result</c>. The real client of Novior.MentorAgent.Blazor
/// on the in-memory hub; its JavaScript runtime is played here, and by the browser in <see cref="RecipeBrowserTests"/>.
/// </summary>
public sealed class RecipeHubTests
{
    private static MentorRecipe CreateOrder() => new()
    {
        Name = "create_order",
        Description = "Creates an order for a customer",
        Route = "/recipes/{page}",
        Parameters = [new("customer", "The customer's name")],
        Steps =
        [
            new(MentorRecipeStepKind.Set, "#customer", "{customer}"),
            new(MentorRecipeStepKind.Click, "#save"),
        ],
    };

    /// <summary>A WebAssembly client's JavaScript runtime that answers the runner's module as told, and records the steps.</summary>
    private sealed class FakeJs : IJSRuntime, IJSObjectReference
    {
        internal string Answer { get; set; } = """{"done":true,"step":2,"reason":null}""";
        internal TaskCompletionSource? Hold { get; set; }
        internal List<string> Imports { get; } = [];
        internal List<string> Runs { get; } = [];

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "import")
            {
                Imports.Add((string)args![0]!);
                return (TValue)(object)this;
            }

            Runs.Add(JsonSerializer.Serialize(args![0]));
            if (Hold is { } hold) await hold.Task;
            return JsonSerializer.Deserialize<TValue>(Answer)!;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>The page a WebAssembly client shows.</summary>
    private sealed class Page : NavigationManager
    {
        public Page(string uri) => Initialize("http://localhost/", uri);
        protected override void NavigateToCore(string uri, bool forceLoad) => Uri = ToAbsoluteUri(uri).ToString();
    }

    /// <summary>
    /// The services of a WebAssembly client of <paramref name="app"/>'s hub, as <c>AddMentorAgentBlazor()</c> registers
    /// them, with <paramref name="js"/> for the browser's runtime and the page at <paramref name="uri"/>, if any.
    /// </summary>
    internal static ServiceProvider WasmClient(ServerAppFactory app, IJSRuntime js, string? uri, HttpMessageHandler? handler = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMentorAgentBlazor(o =>
        {
            o.HubUrl = new Uri(app.BaseAddress, "mentor-hub").ToString();
            o.ConfigureConnection = c => c.HttpMessageHandlerFactory = _ => app.CreateHandler();
        });
        services.AddSingleton(js);
        if (uri is not null) services.AddSingleton<NavigationManager>(new Page(uri));
        services.AddSingleton(new HttpClient(handler ?? app.CreateHandler()) { BaseAddress = app.BaseAddress });
        return services.BuildServiceProvider();
    }

    private static async Task<(ServerAppFactory App, RecordingActionObserver Observer)> ServerAsync()
    {
        var observer = new RecordingActionObserver();
        var app = await ServerAppFactory.CreateAsync(o => o.MemoryAutoCapture = false,
            configureServices: s => s.AddMentorActionObserver(observer));
        app.Services.GetRequiredService<MentorRecipeRegistry>().Add(CreateOrder());
        return (app, observer);
    }

    /// <summary>How long a turn of these tests may take: one that waits for an answer never sent fails, never hangs.</summary>
    private static readonly TimeSpan Turn = TimeSpan.FromSeconds(30);

    private static Task FlushAsync(ServerAppFactory app) => app.Services.GetRequiredService<MentorActionDecisions>().FlushAsync();

    /// <summary>The requests a WebAssembly client sends over HTTP.</summary>
    private sealed class Sent(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        internal List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return base.SendAsync(request, cancellationToken);
        }
    }

    private static HubRecipeRunner RunnerOf(ServerAppFactory app, string connectionId)
        => app.Services.GetRequiredService<MentorHubScopes>().GetOrCreate(connectionId).GetRequiredService<HubRecipeRunner>();

    private static async Task Until(Func<bool> condition, int ms = 10_000)
    {
        for (var waited = 0; !condition() && waited < ms; waited += 50)
            await Task.Delay(50);
        condition().Should().BeTrue($"within {ms / 1000} s");
    }

    private static async Task<int> PostAsync(ServerAppFactory app, string query)
        => (int)(await app.Client.PostAsync($"/mentor/recipe-result?{query}", null)).StatusCode;

    /// <summary>What the model read back from the recipe's call.</summary>
    private static string? ToolResult(ServerAppFactory app)
        => app.Chat.Calls.Skip(1).SelectMany(c => c.Messages).SelectMany(m => m.Contents).OfType<FunctionResultContent>()
            .LastOrDefault()?.Result?.ToString();

    [Fact]
    [Trait("Rule", "recipe.hub")]
    public async Task A_WebAssembly_client_runs_the_recipe_the_hub_asks_for_and_the_model_reads_it_done()
    {
        var (app, observer) = await ServerAsync();
        await using var _app = app;
        var js = new FakeJs();
        await using var client = WasmClient(app, js, "http://localhost/recipes/form?customer=Anna#top");
        app.Chat.CallTool("create_order", new { customer = "Anna" }).Say("The order is saved.");

        await client.GetRequiredService<IMentorOrchestrator>().SendMessageAsync("An order for Anna").WaitAsync(Turn);
        await FlushAsync(app);

        js.Imports.Should().Equal([WasmRecipeClient.Module]);
        js.Runs.Should().ContainSingle().Which.Should()
            .Contain("\"kind\":\"set\"").And.Contain("\"selector\":\"#customer\"").And.Contain("\"value\":\"Anna\"")
            .And.Contain("\"selector\":\"#save\"");
        observer.Steps("create_order").Should().Equal(["Allowed/allowed", "Executed/completed"]);
        ToolResult(app).Should().NotContain("rror");

        var connection = client.GetRequiredService<Novior.MentorAgent.Blazor.Hub.MentorHubClient>().ConnectionId!;
        app.Services.GetRequiredService<HubPageContextStore>().Get(connection)!.Path.Should().Be("/recipes/form",
            "the path only: a query or a fragment can carry what the user typed");
    }

    [Fact]
    [Trait("Rule", "recipe.hub")]
    [Trait("Rule", "recipe.element-missing")]
    public async Task Not_done_in_a_WebAssembly_client_reaches_the_model_and_turns_the_recipe_off()
    {
        var (app, observer) = await ServerAsync();
        await using var _app = app;
        var js = new FakeJs { Answer = """{"done":false,"step":1,"reason":"excluded"}""" };
        await using var client = WasmClient(app, js, "http://localhost/recipes/form");
        app.Chat.CallTool("create_order", new { customer = "Anna" }).Say("I could not save it.");

        await client.GetRequiredService<IMentorOrchestrator>().SendMessageAsync("An order for Anna").WaitAsync(Turn);
        await FlushAsync(app);

        observer.Steps("create_order").Should().Equal(["Allowed/allowed", "Failed/element-missing"]);
        observer.Seen.Single(d => d.Outcome == MentorActionOutcome.Failed).Detail.Should().Be("step 2: excluded");
        ToolResult(app).Should().Contain("could not be done").And.Contain("now off");
        app.Services.GetRequiredService<MentorRecipeRegistry>().IsOff("create_order").Should().BeTrue();
    }

    [Fact]
    [Trait("Rule", "recipe.hub")]
    public async Task A_client_that_does_not_say_its_page_is_offered_no_recipe()
    {
        var (app, _) = await ServerAsync();
        await using var _app = app;
        await using var client = WasmClient(app, new FakeJs(), uri: null);
        app.Chat.Say("Hello.");

        await client.GetRequiredService<IMentorOrchestrator>().SendMessageAsync("hello").WaitAsync(Turn);

        app.Chat.Calls[0].ToolNames.Should().NotContain("create_order",
            "a client older than the recipes, or another kind of client, sends no path and cannot run them");
    }

    [Fact]
    [Trait("Rule", "recipe.hub")]
    public async Task A_browser_that_does_not_answer_ends_the_call_and_leaves_the_recipe_on()
    {
        var (app, observer) = await ServerAsync();
        await using var _app = app;
        var js = new FakeJs { Hold = new TaskCompletionSource() };
        await using var client = WasmClient(app, js, "http://localhost/recipes/form");
        app.Chat.CallTool("create_order", new { customer = "Anna" }).Say("No answer.");

        var hub = client.GetRequiredService<Novior.MentorAgent.Blazor.Hub.MentorHubClient>();
        await hub.StartAsync();
        app.Services.GetRequiredService<MentorHubScopes>().GetOrCreate(hub.ConnectionId!)
            .GetRequiredService<HubRecipeRunner>().Answer = TimeSpan.FromSeconds(1);

        await client.GetRequiredService<IMentorOrchestrator>().SendMessageAsync("An order for Anna")
            .WaitAsync(Turn);   // the wait ends: the turn does not hang on the browser
        await FlushAsync(app);

        js.Runs.Should().ContainSingle();
        observer.Steps("create_order").Should().Equal(["Allowed/allowed", "Failed/exception"]);
        observer.Seen.Single(d => d.Outcome == MentorActionOutcome.Failed).ExceptionType.Should().Be(typeof(TimeoutException).FullName);
        app.Services.GetRequiredService<MentorRecipeRegistry>().IsOff("create_order").Should().BeFalse(
            "a browser that did not answer is not a page that changed");
    }

    [Fact]
    [Trait("Rule", "recipe.hub-result")]
    public async Task A_run_is_answered_once_by_its_own_connection_and_a_wrong_answer_changes_nothing()
    {
        var (app, observer) = await ServerAsync();
        await using var _app = app;
        await using var client = WasmClient(app, new FakeJs { Hold = new TaskCompletionSource() }, "http://localhost/recipes/form");
        app.Chat.CallTool("create_order", new { customer = "Anna" }).Say("The order is saved.");
        var hub = client.GetRequiredService<Novior.MentorAgent.Blazor.Hub.MentorHubClient>();
        await hub.StartAsync();
        var runner = RunnerOf(app, hub.ConnectionId!);

        var turn = client.GetRequiredService<IMentorOrchestrator>().SendMessageAsync("An order for Anna");
        await Until(() => runner.Waiting.Count == 1);
        var run = runner.Waiting.Single();
        var mine = $"connectionId={hub.ConnectionId}&runId={run}";

        await using var other = app.CreateHubConnection();
        await other.StartAsync();
        await other.InvokeAsync("UpdatePageContext", new PageContextSnapshot());
        (await PostAsync(app, $"connectionId={other.ConnectionId}&runId={run}&done=true&step=2")).Should().Be(404,
            "the run is not the other connection's");
        (await PostAsync(app, $"connectionId={hub.ConnectionId}&runId={Guid.NewGuid()}&done=true&step=2")).Should().Be(404, "no such run");
        (await PostAsync(app, $"{mine}&done=true&step=1")).Should().Be(400, "done is at the end of the two steps");
        (await PostAsync(app, $"{mine}&done=false&step=2&reason=missing")).Should().Be(400, "the recipe has no third step");
        runner.Waiting.Should().Equal([run], "a refused answer changes nothing: the run still waits");

        (await PostAsync(app, $"{mine}&done=true&step=2")).Should().Be(200);
        (await PostAsync(app, $"{mine}&done=false&step=0&reason=missing")).Should().Be(404, "a run is answered once");
        await turn.WaitAsync(Turn);
        await FlushAsync(app);
        observer.Steps("create_order").Should().Equal(["Allowed/allowed", "Executed/completed"]);
    }

    [Fact]
    [Trait("Rule", "recipe.hub")]
    public async Task A_run_ends_at_once_when_the_clients_connection_closes()
    {
        var (app, observer) = await ServerAsync();
        await using var _app = app;
        await using var client = WasmClient(app, new FakeJs { Hold = new TaskCompletionSource() }, "http://localhost/recipes/form");
        app.Chat.CallTool("create_order", new { customer = "Anna" }).Say("Saved.");
        var hub = client.GetRequiredService<Novior.MentorAgent.Blazor.Hub.MentorHubClient>();
        await hub.StartAsync();
        var runner = RunnerOf(app, hub.ConnectionId!);

        var turn = client.GetRequiredService<IMentorOrchestrator>().SendMessageAsync("An order for Anna");
        await Until(() => runner.Waiting.Count == 1);
        var closed = System.Diagnostics.Stopwatch.StartNew();
        await hub.DisposeAsync();

        await Until(() => runner.Waiting.Count == 0);
        closed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30), "not the two minutes of the wait");
        for (var i = 0; i < 100 && observer.Steps("create_order").Count < 2; i++)
        {
            await FlushAsync(app);
            await Task.Delay(50);
        }
        observer.Steps("create_order").Should().Equal(["Allowed/allowed", "Stopped/session-ended"],
            "the call's last decision reaches the observers, though the connection's scope is gone");
        await FluentActions.Awaiting(() => turn).Should().ThrowAsync<Exception>("the client's turn went with its connection");
    }

    [Fact]
    [Trait("Rule", "recipe.hub")]
    public async Task Values_too_long_for_the_hub_are_refused_before_they_are_sent_and_the_connection_stays()
    {
        var (app, _) = await ServerAsync();
        await using var _app = app;
        var js = new FakeJs();
        await using var client = WasmClient(app, js, "http://localhost/recipes/form");
        app.Chat.CallTool("create_order", new { customer = new string('a', 64 * 1024) }).Say("Too long.");

        await client.GetRequiredService<IMentorOrchestrator>().SendMessageAsync("An order for a very long name").WaitAsync(Turn);

        js.Runs.Should().BeEmpty("nothing reached the browser");
        ToolResult(app).Should().Contain("too long");
        client.GetRequiredService<Novior.MentorAgent.Blazor.Hub.MentorHubClient>().IsConnected.Should().BeTrue();
        app.Services.GetRequiredService<MentorRecipeRegistry>().IsOff("create_order").Should().BeFalse();
    }

    [Fact]
    [Trait("Rule", "recipe.hub-result")]
    public async Task Nothing_of_the_page_leaves_the_client_only_done_step_and_reason()
    {
        var (app, observer) = await ServerAsync();
        await using var _app = app;
        var sent = new Sent(app.CreateHandler());
        var js = new FakeJs { Answer = @"{""done"":false,""step"":0,""reason"":""Anna Rossi, Via Roma 1"",""value"":""secret""}" };
        await using var client = WasmClient(app, js, "http://localhost/recipes/form", sent);
        app.Chat.CallTool("create_order", new { customer = "Anna" }).Say("Not done.");

        await client.GetRequiredService<IMentorOrchestrator>().SendMessageAsync("An order for Anna").WaitAsync(Turn);
        await FlushAsync(app);

        var answer = sent.Requests.Should().ContainSingle().Which;
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(answer.Query);
        query.Keys.Should().BeEquivalentTo(["connectionId", "runId", "done", "step", "reason"]);
        query["reason"].ToString().Should().Be("error", "a reason that is not one of the runner's words is not sent");
        answer.ToString().Should().NotContain("Anna").And.NotContain("secret");
        observer.Seen.Single(d => d.Outcome == MentorActionOutcome.Failed).Detail.Should().Be("step 1: error");
    }

    [Fact]
    [Trait("Rule", "recipe.hub")]
    public void The_hub_takes_from_the_client_a_path_and_nothing_else()
    {
        var runner = new HubRecipeRunner(null!);
        string? Shown(string? path)
        {
            runner.Show(path);
            return runner.PagePath;
        }

        Shown("/recipes/form").Should().Be("/recipes/form");
        Shown("/recipes/form?customer=Anna").Should().BeNull("a query can carry what the user typed");
        Shown("/recipes/form#top").Should().BeNull();
        Shown("recipes/form").Should().BeNull();
        Shown("/" + new string('a', 2048)).Should().BeNull();
        Shown(null).Should().BeNull();
    }

    [Fact]
    [Trait("Rule", "recipe.hub-result")]
    public async Task A_recipes_answer_comes_from_its_connections_owner_and_says_one_of_the_runners_words()
    {
        await using var app = await ServerAppFactory.CreateAsync();
        await using var anna = app.CreateHubConnectionAs("anna");
        await anna.StartAsync();
        await anna.InvokeAsync("UpdatePageContext", new PageContextSnapshot());   // the scope is there once a call went through
        var id = anna.ConnectionId!;

        async Task<int> Post(string query, string user)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/mentor/recipe-result?{query}");
            request.Headers.Add(ServerAppFactory.AsUserHeader, user);
            return (int)(await app.Client.SendAsync(request)).StatusCode;
        }

        var run = Guid.NewGuid();
        (await Post($"connectionId={id}&runId={run}&done=false&step=0&reason=no-row", "anna")).Should().Be(404, "no run of hers waits");
        (await Post($"connectionId={id}&runId={run}&done=false&step=0&reason=no-row", "bruno")).Should().Be(403, "not his connection");
        (await Post($"connectionId={id}&runId={run}&done=false&step=0&reason=no-row", "-")).Should().Be(401);
        (await Post($"connectionId=gone&runId={run}&done=false&step=0&reason=no-row", "anna")).Should().Be(404);
        (await Post($"connectionId={id}&runId={run}&done=false&step=0&reason=No%20row%3Cb%3E", "anna")).Should().Be(400,
            "the reason reaches the observers: only the runner's words");
        (await Post($"connectionId={id}&runId={run}&done=false&step=0", "anna")).Should().Be(400);
        (await Post($"connectionId={id}&runId={run}&done=false&step=51&reason=no-row", "anna")).Should().Be(400);
        (await Post($"connectionId={id}&runId={run}&done=true&step=-1", "anna")).Should().Be(400);
    }
}
