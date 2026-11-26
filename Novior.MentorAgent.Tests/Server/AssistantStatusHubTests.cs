using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Blazor.Extensions;
using Novior.MentorAgent.Blazor.Hub;
using Novior.MentorAgent.Blazor.Models;
using Novior.MentorAgent.Blazor.Services;
using Novior.MentorAgent.Server.Hubs;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Xunit;

namespace Novior.MentorAgent.Tests.Server;

/// <summary>
/// Story 1290, slice B — the assistant turned off or on by the application reaches the clients of the hub: a client reads
/// it when it connects (<c>GetAssistantStatus</c>) and gets the changes (<c>AssistantStatus</c>, to every connection),
/// and the WebAssembly widget's <see cref="IMentorAssistantStatus"/> follows them.
/// </summary>
public sealed class AssistantStatusHubTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static Task<ServerAppFactory> AppAsync(TestAssistantSwitch? status)
        => ServerAppFactory.CreateAsync(configureServices: status is null ? null : s => s.AddSingleton<IMentorAssistantStatus>(status));

    private static MentorAssistantStatusBroadcaster Broadcaster(ServerAppFactory app)
        => app.Services.GetServices<IHostedService>().OfType<MentorAssistantStatusBroadcaster>().Single();

    private static async Task Until(Func<bool> condition, string because)
    {
        var until = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException(because);
            await Task.Delay(20);
        }
    }

    private static async Task<(HubConnection Hub, ConcurrentQueue<bool> Seen)> ConnectAsync(ServerAppFactory app)
    {
        var hub = app.CreateHubConnection();
        var seen = new ConcurrentQueue<bool>();
        hub.On<bool>("AssistantStatus", seen.Enqueue);
        await hub.StartAsync();
        // StartAsync ends with the handshake's answer, and the server adds the connection to those Clients.All reaches
        // only after it: a change sent at once could miss this connection, which the full suite's load made happen. A
        // hub method is served once the connection is registered, so after this call every change reaches it.
        await hub.InvokeAsync<bool>("GetAssistantStatus");
        return (hub, seen);
    }

    // ── The hub ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Without_a_status_the_hub_says_the_assistant_is_on()
    {
        await using var app = await AppAsync(null);
        await using var hub = app.CreateHubConnection();
        await hub.StartAsync();

        (await hub.InvokeAsync<bool>("GetAssistantStatus")).Should().BeTrue();
    }

    [Fact]
    public async Task The_hub_says_the_assistant_is_off_when_the_application_turned_it_off()
    {
        await using var app = await AppAsync(new TestAssistantSwitch(on: false));
        await using var hub = app.CreateHubConnection();
        await hub.StartAsync();

        (await hub.InvokeAsync<bool>("GetAssistantStatus")).Should().BeFalse();
    }

    [Fact]
    public async Task Turned_off_and_on_the_assistant_reaches_every_connection_in_order()
    {
        var status = new TestAssistantSwitch();
        await using var app = await AppAsync(status);
        var (first, seenByFirst) = await ConnectAsync(app);
        var (second, seenBySecond) = await ConnectAsync(app);
        await using var _ = first;
        await using var __ = second;

        status.Set(false);
        status.Set(true);
        status.Set(false);
        await Broadcaster(app).Sends;

        await Until(() => seenByFirst.Count == 3 && seenBySecond.Count == 3, "every connection gets every change");
        seenByFirst.Should().Equal(false, true, false);
        seenBySecond.Should().Equal(false, true, false);
        (await first.InvokeAsync<bool>("GetAssistantStatus")).Should().BeFalse();
    }

    [Fact]
    public async Task Changed_without_a_change_sends_nothing()
    {
        var status = new TestAssistantSwitch();
        await using var app = await AppAsync(status);
        var (hub, seen) = await ConnectAsync(app);
        await using var _ = hub;

        status.Raise();               // a service that breaks the rule: still on
        status.Set(false);
        status.Raise();               // still off
        await Broadcaster(app).Sends;

        await Until(() => !seen.IsEmpty, "the change is sent");
        await Task.Delay(200);
        seen.Should().Equal([false], "only the change is sent, not the events that changed nothing");
    }

    [Fact]
    public async Task A_status_that_fails_to_be_read_costs_a_line_in_the_log_and_not_the_next_change()
    {
        var status = new TestAssistantSwitch();
        await using var app = await AppAsync(status);
        var (hub, seen) = await ConnectAsync(app);
        await using var _ = hub;

        status.ThrowOnce = new InvalidOperationException("the console is unreachable: secret detail");
        status.Raise();
        status.HandlerFailures.Should().Be(0, "the broadcaster's handler never throws into the service");

        status.Set(false);
        await Broadcaster(app).Sends;
        await Until(() => !seen.IsEmpty, "the next change is sent");
        seen.Should().Equal(false);
        app.Logs.HasWarning("InvalidOperationException").Should().BeTrue();
        app.Logs.WarningText.Should().NotContain("secret detail", "only the type of the exception is logged");
    }

    [Fact]
    public async Task The_broadcaster_leaves_the_status_when_the_application_stops()
    {
        var status = new TestAssistantSwitch();
        var app = await AppAsync(status);
        status.Handlers.Should().Be(1);

        await app.DisposeAsync();

        status.Handlers.Should().Be(0);
    }

    // ── The WebAssembly client ───────────────────────────────────────────────

    private static MentorHubClient Client(ServerAppFactory app) => new(new MentorAgentBlazorOptions
    {
        HubUrl = new Uri(app.BaseAddress, "mentor-hub").ToString(),
        ConfigureConnection = c => c.HttpMessageHandlerFactory = _ => app.CreateHandler(),
    }, NullLogger<MentorHubClient>.Instance);

    [Fact]
    public async Task The_WebAssembly_status_reads_the_assistant_off_when_it_connects_and_follows_the_changes()
    {
        var status = new TestAssistantSwitch(on: false);
        await using var app = await AppAsync(status);
        var client = Client(app);
        await using var _ = client;
        using var wasm = new WasmAssistantStatus(client, NullLogger<WasmAssistantStatus>.Instance);
        var changes = new ConcurrentQueue<bool>();
        wasm.Changed += (_, _) => throw new InvalidOperationException("a handler that throws");
        wasm.Changed += (_, _) => changes.Enqueue(wasm.IsOn);
        wasm.IsOn.Should().BeTrue("on until the server says otherwise");

        await client.StartAsync();
        await wasm.LastRead;

        wasm.IsOn.Should().BeFalse("read from the hub when the connection opened");
        changes.Should().Equal([false], "a handler that throws does not stop the others");

        status.Set(true);
        await Broadcaster(app).Sends;
        await Until(() => changes.Count == 2, "the change reaches the client");
        wasm.IsOn.Should().BeTrue();

        status.Set(false);
        await Broadcaster(app).Sends;
        await Until(() => changes.Count == 3, "the change reaches the client");
        changes.Should().Equal(false, true, false);
    }

    [Fact]
    public async Task Built_after_the_connection_opened_the_WebAssembly_status_reads_the_value_at_once()
    {
        await using var app = await AppAsync(new TestAssistantSwitch(on: false));
        var client = Client(app);
        await using var _ = client;
        await client.StartAsync();

        // The widget resolves the service when it renders, after the orchestrator connected.
        using var wasm = new WasmAssistantStatus(client, NullLogger<WasmAssistantStatus>.Instance);
        await wasm.LastRead;

        wasm.IsOn.Should().BeFalse();
    }

    [Fact]
    public async Task Without_a_status_on_the_server_the_WebAssembly_status_stays_on_and_raises_nothing()
    {
        await using var app = await AppAsync(null);
        var client = Client(app);
        await using var _ = client;
        using var wasm = new WasmAssistantStatus(client, NullLogger<WasmAssistantStatus>.Instance);
        var raised = 0;
        wasm.Changed += (_, _) => raised++;

        await client.StartAsync();
        await wasm.LastRead;

        wasm.IsOn.Should().BeTrue();
        raised.Should().Be(0);
    }

    [Fact]
    public async Task A_WebAssembly_widget_that_sent_nothing_yet_shows_the_assistant_off()
    {
        // As the widget does: it resolves the status when it renders, and nobody has started the connection.
        await using var app = await AppAsync(new TestAssistantSwitch(on: false));
        var client = Client(app);
        await using var _ = client;
        using var wasm = new WasmAssistantStatus(client, NullLogger<WasmAssistantStatus>.Instance);

        await Until(() => !wasm.IsOn, "the status opens the connection to read the value, before anyone writes");
        app.Chat.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task With_the_assistant_off_the_WebAssembly_orchestrator_sends_no_turn_even_before_it_knew()
    {
        var status = new TestAssistantSwitch(on: false);
        await using var app = await AppAsync(status);
        app.Chat.Say("A reply that must not be asked for.");
        var options = new MentorAgentBlazorOptions
        {
            HubUrl = new Uri(app.BaseAddress, "mentor-hub").ToString(),
            ConfigureConnection = c => c.HttpMessageHandlerFactory = _ => app.CreateHandler(),
        };
        var client = new MentorHubClient(options, NullLogger<MentorHubClient>.Instance);
        var wasm = new WasmAssistantStatus(client, NullLogger<WasmAssistantStatus>.Instance);
        await using var orchestrator = new WasmMentorOrchestrator(client, new WasmMentorPageContext(),
            NullLogger<WasmMentorOrchestrator>.Instance, new HttpClient(), options) { Status = wasm };

        // Sent at once, as a user who writes before the value arrived.
        await orchestrator.SendMessageAsync("Show the orders");

        app.Chat.CallCount.Should().Be(0, "no turn reaches the hub while the assistant is off");
        wasm.IsOn.Should().BeFalse();

        status.Set(true);
        await Broadcaster(app).Sends;
        await Until(() => wasm.IsOn, "the change reaches the client");
        await orchestrator.SendMessageAsync("Show the orders");
        app.Chat.CallCount.Should().Be(1, "on again, the turn goes");
        wasm.Dispose();
    }

    private static (MentorAgentBlazorOptions Options, Func<bool> Reachable) Unreachable(ServerAppFactory app, Func<bool> reachable)
        => (new MentorAgentBlazorOptions
        {
            HubUrl = new Uri(app.BaseAddress, "mentor-hub").ToString(),
            ConfigureConnection = c => c.HttpMessageHandlerFactory = _ => new Gate(app.CreateHandler(), reachable),
        }, reachable);

    /// <summary>A server that does not answer until <c>reachable</c> says so.</summary>
    private sealed class Gate(HttpMessageHandler inner, Func<bool> reachable) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => reachable() ? base.SendAsync(request, cancellationToken) : throw new HttpRequestException("unreachable");
    }

    /// <summary>The levels a component logged at.</summary>
    private sealed class Levels<T> : ILogger<T>
    {
        public ConcurrentQueue<LogLevel> Seen { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Seen.Enqueue(logLevel);
    }

    [Fact]
    public async Task Two_openings_at_once_make_one_connection_and_the_second_waits_for_it()
    {
        await using var app = await AppAsync(null);
        var client = Client(app);
        await using var _ = client;

        var first = client.StartAsync();
        await client.StartAsync();   // as the first message while the widget's own opening is under way

        (await client.GetAssistantStatusAsync()).Should().BeTrue("the connection is open when the second opening returns");
        await first;
        client.IsConnected.Should().BeTrue();
    }

    [Fact]
    public async Task With_the_server_unreachable_at_load_the_early_opening_logs_one_quiet_line_and_the_first_message_tries_again()
    {
        await using var app = await AppAsync(new TestAssistantSwitch());
        app.Chat.Say("Here are the orders.");
        var reachable = false;
        var (options, _) = Unreachable(app, () => reachable);
        var hubLog = new Levels<MentorHubClient>();
        var client = new MentorHubClient(options, hubLog);
        await using var __ = client;
        using var wasm = new WasmAssistantStatus(client, NullLogger<WasmAssistantStatus>.Instance);
        await Until(() => hubLog.Seen.Contains(LogLevel.Information), "the early opening failed");
        hubLog.Seen.Should().NotContain(l => l >= LogLevel.Warning, "a server not up yet at load is no error for the page");

        reachable = true;
        await using var orchestrator = new WasmMentorOrchestrator(client, new WasmMentorPageContext(),
            NullLogger<WasmMentorOrchestrator>.Instance, new HttpClient(), options) { Status = wasm };
        await orchestrator.SendMessageAsync("Show the orders");

        app.Chat.CallCount.Should().Be(1, "the first message opens the connection as before");
        wasm.IsOn.Should().BeTrue();
    }

    [Fact]
    public async Task A_read_of_the_status_that_fails_lets_the_message_go_as_on()
    {
        var status = new TestAssistantSwitch(on: true);
        await using var app = await AppAsync(status);
        app.Chat.Say("Here are the orders.");
        var client = Client(app);
        await using var _ = client;
        status.ThrowOnce = new InvalidOperationException("the console is unreachable");   // GetAssistantStatus fails
        using var wasm = new WasmAssistantStatus(client, NullLogger<WasmAssistantStatus>.Instance);
        var options = new MentorAgentBlazorOptions { HubUrl = new Uri(app.BaseAddress, "mentor-hub").ToString() };
        await using var orchestrator = new WasmMentorOrchestrator(client, new WasmMentorPageContext(),
            NullLogger<WasmMentorOrchestrator>.Instance, new HttpClient(), options) { Status = wasm };

        await orchestrator.SendMessageAsync("Show the orders");

        app.Chat.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task A_message_waits_for_the_read_under_way_and_is_not_sent_when_it_says_off()
    {
        var status = new TestAssistantSwitch(on: false);
        await using var app = await AppAsync(status);
        app.Chat.Say("A reply that must not be asked for.");
        var client = Client(app);
        await using var _ = client;
        await client.StartAsync();                       // the connection is open, its value not read yet
        using var hold = new ManualResetEventSlim(false);
        status.Hold = hold;                              // the server takes a while to answer
        using var wasm = new WasmAssistantStatus(client, NullLogger<WasmAssistantStatus>.Instance);
        var options = new MentorAgentBlazorOptions { HubUrl = new Uri(app.BaseAddress, "mentor-hub").ToString() };
        await using var orchestrator = new WasmMentorOrchestrator(client, new WasmMentorPageContext(),
            NullLogger<WasmMentorOrchestrator>.Instance, new HttpClient(), options) { Status = wasm };
        var release = Task.Delay(300).ContinueWith(_ => { status.Hold = null; hold.Set(); }, TaskScheduler.Default);

        await orchestrator.SendMessageAsync("Show the orders");

        app.Chat.CallCount.Should().Be(0, "the message waited for the value, and the value is off");
        wasm.IsOn.Should().BeFalse();
        await release;
    }

    [Fact]
    public async Task A_read_of_the_status_that_does_not_end_holds_a_message_only_up_to_its_limit()
    {
        var status = new TestAssistantSwitch(on: false);
        await using var app = await AppAsync(status);
        using var hold = new ManualResetEventSlim(false);
        status.Hold = hold;                              // GetAssistantStatus does not answer
        var client = Client(app);
        await using var _ = client;
        using var wasm = new WasmAssistantStatus(client, NullLogger<WasmAssistantStatus>.Instance);
        try
        {
            await client.StartAsync();
            var watch = System.Diagnostics.Stopwatch.StartNew();

            (await wasm.ReadIsOnAsync(TimeSpan.FromMilliseconds(200), CancellationToken.None)).Should().BeTrue("as on, not yet known");

            watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "the wait has a limit");
        }
        finally
        {
            status.Hold = null;
            hold.Set();
        }
    }

    [Fact]
    public async Task AddMentorAgentBlazor_gives_the_orchestrator_the_status()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new HttpClient());
        services.AddMentorAgentBlazor(o => o.HubUrl = "http://127.0.0.1:9/mentor-hub");
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var orchestrator = scope.ServiceProvider.GetRequiredService<WasmMentorOrchestrator>();

        orchestrator.Status.Should().BeSameAs(provider.GetRequiredService<IMentorAssistantStatus>());
    }

    [Fact]
    public void AddMentorAgentBlazor_registers_the_status_and_leaves_the_applications_own()
    {
        var services = new ServiceCollection();
        services.AddMentorAgentBlazor(_ => { });
        services.Single(d => d.ServiceType == typeof(IMentorAssistantStatus)).ImplementationType
            .Should().Be(typeof(WasmAssistantStatus));
        services.Single(d => d.ServiceType == typeof(IMentorAssistantStatus)).Lifetime
            .Should().Be(ServiceLifetime.Singleton);

        var own = new ServiceCollection();
        var mine = new TestAssistantSwitch();
        own.AddSingleton<IMentorAssistantStatus>(mine);
        own.AddMentorAgentBlazor(_ => { });
        own.Single(d => d.ServiceType == typeof(IMentorAssistantStatus)).ImplementationInstance.Should().BeSameAs(mine);
    }
}
