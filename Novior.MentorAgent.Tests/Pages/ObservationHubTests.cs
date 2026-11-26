using FluentAssertions;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Novior.MentorAgent.Abstractions.Core;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Blazor.Extensions;
using Novior.MentorAgent.Blazor.Hub;
using Novior.MentorAgent.Blazor.Models;
using Novior.MentorAgent.Blazor.Services;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.Harness;
using Xunit;

namespace Novior.MentorAgent.Tests.Pages;

/// <summary>
/// Story 1640 (L6), slice B — what the widget of a WebAssembly client observes goes to the hub
/// (<c>ReportObservedPage</c>), and the server keeps it as it keeps its own: checked, from two users, within limits,
/// off with the option. The client stops when the server says so, or cannot take it.
/// </summary>
public sealed class ObservationHubTests
{
    private static readonly System.Reflection.Assembly Tests = typeof(ObservationHubTests).Assembly;

    private static Task<ServerAppFactory> ServerAsync(bool observe = true) => ServerAppFactory.CreateAsync(o =>
    {
        o.AutoMentor.Enabled = true;
        o.AutoMentor.ObservePages = observe;
        o.AutoMentor.PageAssemblies = [Tests];
    });

    /// <summary>BUG-256: an observed selector the server keeps, the developer's data-mentor-id.</summary>
    private static string M(string id) => $"button[data-mentor-id=\"{id}\"]";

    private static List<PageObservedElement?> Dialog() =>
    [
        new PageObservedElement { Element = "button", Label = "Confirm", Type = "button", Selector = M("confirm") },
        new PageObservedElement { Element = "input", Type = "password", Label = "Password", Selector = M("pwd") },
    ];

    private static IReadOnlyList<MentorUiManifest.Element> Kept(ServerAppFactory app, string route)
        => app.Services.GetRequiredService<MentorPageDirectory>().Pages().Single(p => p.Route == route).Observed;

    [Fact]
    [Trait("Rule", "page.observe")]
    [Trait("Rule", "page.observe-shared")]
    public async Task Two_WebAssembly_clients_that_see_a_dialog_make_it_the_pages_through_the_hub()
    {
        await using var app = await ServerAsync();
        MentorHubClient Client() => new(new MentorAgentBlazorOptions
        {
            HubUrl = new Uri(app.BaseAddress, "mentor-hub").ToString(),
            ConfigureConnection = c => c.HttpMessageHandlerFactory = _ => app.CreateHandler(),
            PageAssemblies = [Tests],
        }, NullLogger<MentorHubClient>.Instance);
        await using var first = Client();
        await using var second = Client();
        await first.StartAsync();
        await second.StartAsync();

        var observer = new WasmPageObserver(first, NullLogger<WasmPageObserver>.Instance);
        observer.IsOn.Should().BeTrue("the client cannot read the server's option: the server's answer decides");
        (await observer.ReportAsync("/l1/home", Dialog(), null)).Should().BeTrue();
        (await observer.ReportAsync("/l1/home", Dialog(), null)).Should().BeTrue();
        Kept(app, "/l1/home").Should().BeEmpty("one connection with nobody signed in is one user, however often it sends");

        (await new WasmPageObserver(second, NullLogger<WasmPageObserver>.Instance).ReportAsync("/l1/home", Dialog(), null)).Should().BeTrue();
        Kept(app, "/l1/home").Select(e => e.Selector).Should().Equal([M("confirm")], "the password field is not kept, whatever a client sends");
    }

    [Fact]
    [Trait("Rule", "page.observe-route")]
    public async Task A_client_observes_only_once_the_server_knows_its_pages_however_slowly_they_arrive()
    {
        // The server reads no page of its own: /l1/home is only the client's, sent with ReportPages, which this client
        // reads slowly after it connects. An observation sent before it would reach the server first, find no page and
        // be left out. (Once ReportPages is sent, the hub takes a connection's calls in order.)
        await using var app = await ServerAppFactory.CreateAsync(o => o.AutoMentor.Enabled = true);
        MentorHubClient Client() => new(new MentorAgentBlazorOptions
        {
            HubUrl = new Uri(app.BaseAddress, "mentor-hub").ToString(),
            ConfigureConnection = c => c.HttpMessageHandlerFactory = _ => app.CreateHandler(),
            PageAssemblies = [Tests],
        }, NullLogger<MentorHubClient>.Instance) { BeforeReportPages = () => Task.Delay(1500) };
        await using var first = Client();
        await using var second = Client();

        foreach (var client in new[] { first, second })
        {
            var start = client.StartAsync();
            (await new WasmPageObserver(client, NullLogger<WasmPageObserver>.Instance).ReportAsync("/l1/home", Dialog(), null)).Should().BeTrue();
            await start;
        }

        await Until(() => Kept(app, "/l1/home").Count > 0);
        Kept(app, "/l1/home").Select(e => e.Selector).Should().Equal([M("confirm")], "both observations waited for the client's pages");
    }

    [Fact]
    [Trait("Rule", "page.observe-shared")]
    public async Task Two_connections_of_one_signed_in_user_are_one_user()
    {
        await using var app = await ServerAsync();
        await using var a = app.CreateHubConnectionAs("anna");
        await using var b = app.CreateHubConnectionAs("anna");
        await using var c = app.CreateHubConnectionAs("bruno");
        await a.StartAsync();
        await b.StartAsync();
        await c.StartAsync();

        (await a.InvokeAsync<bool>("ReportObservedPage", "/l1/home", Dialog(), null)).Should().BeTrue();
        (await b.InvokeAsync<bool>("ReportObservedPage", "/l1/home", Dialog(), null)).Should().BeTrue();
        Kept(app, "/l1/home").Should().BeEmpty("Anna in two tabs is still one user");

        await c.InvokeAsync<bool>("ReportObservedPage", "/l1/home", Dialog(), null);
        Kept(app, "/l1/home").Should().ContainSingle();
    }

    [Fact]
    [Trait("Rule", "page.observe-limit")]
    public async Task Past_its_messages_a_connection_is_told_to_stop_and_the_log_says_so_once()
    {
        await using var app = await ServerAsync();
        await using var hub = app.CreateHubConnection();
        await hub.StartAsync();

        for (var i = 0; i < MentorPageDirectory.MaxObservedReports; i++)
            (await hub.InvokeAsync<bool>("ReportObservedPage", "/l1/home", new List<PageObservedElement?>(), null)).Should().BeTrue();
        (await hub.InvokeAsync<bool>("ReportObservedPage", "/l1/home", new List<PageObservedElement?>(), null)).Should().BeFalse();
        (await hub.InvokeAsync<bool>("ReportObservedPage", "/l1/home", new List<PageObservedElement?>(), "elements")).Should().BeFalse();

        app.Logs.Records.Count(r => r.Message.Contains("page.observe-limit: the observation of the pages reached")).Should().Be(1);
    }

    [Fact]
    [Trait("Rule", "page.observe-off")]
    public async Task With_ObservePages_off_the_server_keeps_nothing_and_its_first_answer_stops_the_client()
    {
        await using var app = await ServerAsync(observe: false);
        await using var a = app.CreateHubConnectionAs("anna");
        await using var b = app.CreateHubConnectionAs("bruno");
        await a.StartAsync();
        await b.StartAsync();

        (await a.InvokeAsync<bool>("ReportObservedPage", "/l1/home", Dialog(), null)).Should().BeFalse();
        (await b.InvokeAsync<bool>("ReportObservedPage", "/l1/home", Dialog(), null)).Should().BeFalse();
        Kept(app, "/l1/home").Should().BeEmpty();
    }

    [Fact]
    [Trait("Rule", "page.observe")]
    public async Task A_client_whose_hub_connects_late_keeps_what_it_observed_and_sends_it_then()
    {
        // The widget opens the hub at the first message: the observation starts long before. Nothing is lost, and the
        // browser is never told to stop because the hub is not there yet.
        await using var app = await ServerAppFactory.CreateAsync(o => o.AutoMentor.Enabled = true);
        MentorHubClient Client() => new(new MentorAgentBlazorOptions
        {
            HubUrl = new Uri(app.BaseAddress, "mentor-hub").ToString(),
            ConfigureConnection = c => c.HttpMessageHandlerFactory = _ => app.CreateHandler(),
            PageAssemblies = [Tests],
        }, NullLogger<MentorHubClient>.Instance);
        await using var first = Client();
        await using var second = Client();
        using var firstObserver = new WasmPageObserver(first, NullLogger<WasmPageObserver>.Instance);
        using var secondObserver = new WasmPageObserver(second, NullLogger<WasmPageObserver>.Instance);

        var answer = System.Diagnostics.Stopwatch.StartNew();
        (await firstObserver.ReportAsync("/l1/home", Dialog(), null)).Should().BeTrue("the report waits for the hub");
        (await secondObserver.ReportAsync("/l1/home", Dialog(), null)).Should().BeTrue();
        answer.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "the browser has its answer at once");

        await Task.Delay(TimeSpan.FromSeconds(30));
        await first.StartAsync();
        await second.StartAsync();

        await Until(() => Kept(app, "/l1/home").Count > 0);
        Kept(app, "/l1/home").Select(e => e.Selector).Should().Equal([M("confirm")], "both observations reached the server once each hub connected");
    }

    [Fact]
    [Trait("Rule", "page.observe-limit")]
    public async Task While_the_hub_is_not_there_the_reports_wait_within_the_limit_the_oldest_dropped_and_leave_in_order()
    {
        var ready = false;
        Action? signal = null;
        var sent = new System.Collections.Concurrent.ConcurrentQueue<string?>();
        using var observer = new WasmPageObserver(() => ready, (path, _, _) => { sent.Enqueue(path); return Task.FromResult(true); },
            NullLogger.Instance, s => { signal = s; return () => signal = null; });

        for (var i = 0; i < WasmPageObserver.MaxQueued + 5; i++)
            (await observer.ReportAsync($"/p/{i}", [], null)).Should().BeTrue();
        sent.Should().BeEmpty();

        ready = true;
        signal!();
        await Until(() => sent.Count >= WasmPageObserver.MaxQueued);
        (await observer.ReportAsync("/p/now", [], null)).Should().BeTrue();

        sent.Should().Equal([.. Enumerable.Range(5, WasmPageObserver.MaxQueued).Select(i => $"/p/{i}"), "/p/now"],
            "the 200 newest waited, in order, and a report sent when the hub is ready comes after them");
        WasmPageObserver.MaxQueued.Should().Be(MentorPageDirectory.MaxObservedReports, "the browser sends no more than that");
    }

    [Fact]
    [Trait("Rule", "page.observe")]
    public async Task A_report_sent_as_the_hub_becomes_ready_leaves_after_those_that_waited_not_before()
    {
        // The hub is ready, its signal not yet run: a new report must not overtake the ones that waited.
        var ready = false;
        var sent = new System.Collections.Concurrent.ConcurrentQueue<string?>();
        using var observer = new WasmPageObserver(() => ready, (path, _, _) => { sent.Enqueue(path); return Task.FromResult(true); },
            NullLogger.Instance, _ => () => { });

        (await observer.ReportAsync("/p/old", [], null)).Should().BeTrue();
        ready = true;
        (await observer.ReportAsync("/p/new", [], null)).Should().BeTrue();

        await Until(() => sent.Count >= 2);
        sent.Should().Equal(["/p/old", "/p/new"]);
    }

    [Fact]
    [Trait("Rule", "page.observe")]
    [Trait("Rule", "page.observe-off")]
    public async Task A_server_that_refuses_or_cannot_take_the_page_stops_the_client_never_the_widget()
    {
        var throwing = new WasmPageObserver(() => true, (_, _, _) => throw new Microsoft.AspNetCore.SignalR.HubException("Method does not exist."), NullLogger.Instance);
        (await throwing.ReportAsync("/l1/home", Dialog(), null)).Should().BeFalse("an older server has no ReportObservedPage");

        // Reports that waited, refused when they leave: the rest are dropped and the browser's next report is told to stop.
        var ready = false;
        Action? signal = null;
        var sent = 0;
        using var off = new WasmPageObserver(() => ready, (_, _, _) => { Interlocked.Increment(ref sent); return Task.FromResult(false); },
            NullLogger.Instance, s => { signal = s; return () => { }; });
        for (var i = 0; i < 3; i++)
            (await off.ReportAsync("/l1/home", Dialog(), null)).Should().BeTrue();
        ready = true;
        signal!();
        await Until(() => Volatile.Read(ref sent) > 0);
        await Task.Delay(200);

        (await off.ReportAsync("/l1/home", Dialog(), null)).Should().BeFalse("the server refused what waited");
        Volatile.Read(ref sent).Should().Be(1, "after the refusal nothing else that waited was sent");
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var waited = 0; !condition() && waited < 10_000; waited += 50)
            await Task.Delay(50);
        condition().Should().BeTrue("within 10 s");
    }

    [Fact]
    [Trait("Rule", "page.observe")]
    public void The_WebAssembly_client_registers_its_observer_and_an_application_one_wins()
    {
        var services = new ServiceCollection();
        services.AddMentorAgentBlazor(_ => { });
        services.Should().ContainSingle(d => d.ServiceType == typeof(IMentorPageObserver))
            .Which.Should().Match<ServiceDescriptor>(d => d.ImplementationType == typeof(WasmPageObserver) && d.Lifetime == ServiceLifetime.Scoped);

        var own = new ServiceCollection();
        own.AddScoped<IMentorPageObserver, NoObserver>();
        own.AddMentorAgentBlazor(_ => { });
        own.Should().ContainSingle(d => d.ServiceType == typeof(IMentorPageObserver)).Which.ImplementationType.Should().Be(typeof(NoObserver));
    }

    private sealed class NoObserver : IMentorPageObserver
    {
        public bool IsOn => false;
        public Task<bool> ReportAsync(string? path, IReadOnlyList<PageObservedElement?>? elements, string? limit) => Task.FromResult(false);
    }
}
