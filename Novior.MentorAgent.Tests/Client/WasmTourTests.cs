using FluentAssertions;
using Novior.MentorAgent.Blazor.Models;
using Novior.MentorAgent.Blazor.Services;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Novior.MentorAgent.Tests.Client;

/// <summary>
/// Tour-URL resolution on WebAssembly, which has already shipped one silent failure.
///
/// <para>
/// The tour is generated on the server — the only side that knows the registered pages and tools —
/// so the client fetches it. Resolving that URL against the <b>client's</b> base address instead of
/// the <b>server's</b> is wrong whenever the two are on different origins, which is the documented
/// deployment for this package. And it fails invisibly: a SPA host answers any unknown path with
/// <c>index.html</c> and HTTP 200, so the client gets a page, fails to parse it as JSON, and the
/// tour simply never appears.
/// </para>
///
/// <para>
/// The second trap is in the browser and worse. There, as on Linux, <c>Uri.TryCreate("/mentor/tour",
/// UriKind.Absolute, …)</c> <b>succeeds</b>, yielding <c>file:///mentor/tour</c> — so a check for
/// "is this absolute" treats the default relative path as an explicit override and goes to the
/// local filesystem. These tests run on Windows, where the same call fails: they pin that no
/// <c>file:</c> URL comes out, not the parse itself.
/// </para>
///
/// <para>
/// Asserted through the warning the fetch logs, which carries the resolved URL. That keeps the
/// resolution private where it belongs while still testing the real thing rather than a copy of
/// the logic.
/// </para>
/// </summary>
public sealed class WasmTourTests(ITestOutputHelper output)
{
    /// <summary>An <see cref="ILogger{T}"/> that writes into the shared collector.</summary>
    private static ILogger<T> CollectingLogger<T>(TestLogCollector logs)
        => new TestLoggerProvider(logs).CreateLogger(typeof(T).FullName!) as ILogger<T>
           ?? new TypedLogger<T>(new TestLoggerProvider(logs).CreateLogger(typeof(T).FullName!));

    /// <summary>Adapts a plain <see cref="ILogger"/> to <see cref="ILogger{T}"/>.</summary>
    /// <summary>Builds the tour service and returns the URL it actually tried to fetch.</summary>
    private async Task<string> ResolvedUrlAsync(string hubUrl, string? tourUrl = null, string clientBase = "https://client.test/")
    {
        var logs = new TestLogCollector();
        var options = new MentorAgentBlazorOptions { HubUrl = hubUrl };
        if (tourUrl is not null) options.TourUrl = tourUrl;

        var tour = new WasmMentorTour(
            new StubNavigationManager(clientBase),
            options,
            CollectingLogger<WasmMentorTour>(logs));

        // Nothing is listening on any of these addresses, so the fetch fails and the warning names
        // the URL — which is the value under test.
        //
        // The ports here must therefore be ones **no sample ever binds**. They used to read 5169,
        // which is the headless sample's own documented port: running that app during Part D made
        // the fetch succeed, the tour come back with real steps, and this test fail for a reason
        // that had nothing to do with URL resolution. A test whose correctness depends on a port
        // being free is a test that fails on whoever happens to have the app running.
        var steps = await tour.GetStepsAsync();
        steps.Should().BeEmpty("a tour that cannot be fetched must degrade, not throw");

        var text = logs.WarningText;
        output.WriteLine(text);
        return text;
    }

    // ── Cross-origin: the documented deployment ────────────────────────────────

    [Fact]
    public async Task A_relative_tour_path_resolves_against_the_servers_origin_not_the_clients()
    {
        var logged = await ResolvedUrlAsync(hubUrl: "http://localhost:55169/mentor-hub");

        logged.Should().Contain("http://localhost:55169/mentor/tour");
        logged.Should().NotContain("client.test",
            "asking the client's own host for the tour returns its index.html with a 200");
    }

    [Fact]
    public async Task The_hub_path_is_dropped_and_only_its_origin_is_used()
    {
        // The hub lives at /mentor-hub; the tour does not live under it.
        var logged = await ResolvedUrlAsync(hubUrl: "https://api.test:8443/some/deep/mentor-hub");

        logged.Should().Contain("https://api.test:8443/mentor/tour");
        logged.Should().NotContain("/some/deep/");
    }

    [Fact]
    public async Task An_explicit_absolute_tour_url_wins_over_the_hub_origin()
    {
        var logged = await ResolvedUrlAsync(
            hubUrl: "http://localhost:55169/mentor-hub",
            tourUrl: "https://cdn.test/tour.json");

        logged.Should().Contain("https://cdn.test/tour.json");
        logged.Should().NotContain("localhost:55169");
    }

    [Fact]
    public async Task A_custom_relative_tour_path_still_resolves_against_the_server()
    {
        var logged = await ResolvedUrlAsync(
            hubUrl: "http://localhost:55169/mentor-hub",
            tourUrl: "/api/onboarding");

        logged.Should().Contain("http://localhost:55169/api/onboarding");
    }

    // ── Same-origin hosting ────────────────────────────────────────────────────

    [Fact]
    public async Task A_relative_hub_url_means_same_origin_so_the_path_is_left_relative()
    {
        // Blazor Auto, or a server that also serves the client. The HttpClient's BaseAddress is
        // the app's own origin, which is already the right one.
        var logged = await ResolvedUrlAsync(hubUrl: "/mentor-hub");

        logged.Should().Contain("/mentor/tour");
        logged.Should().NotContain("file:",
            "in the browser a bare path parses as an absolute file:// URI, and treating it as one " +
            "sends the request to the local filesystem");
    }

    [Fact]
    public async Task A_relative_hub_url_never_produces_a_file_scheme_request()
    {
        // The same guard stated as its own case, because this is the regression that shipped.
        var logged = await ResolvedUrlAsync(hubUrl: "/mentor-hub", tourUrl: "/mentor/tour");

        logged.Should().NotContain("file://");
    }

    [Fact]
    public async Task An_empty_hub_url_degrades_to_the_relative_path_rather_than_throwing()
    {
        var logged = await ResolvedUrlAsync(hubUrl: "");

        logged.Should().Contain("/mentor/tour");
    }

    [Fact]
    public async Task A_non_http_hub_url_is_not_treated_as_an_origin()
    {
        // ws:// is a plausible thing for someone to write for a socket transport. It parses as an
        // absolute URI, so only the scheme check keeps it out.
        var logged = await ResolvedUrlAsync(hubUrl: "ws://localhost:55169/mentor-hub");

        logged.Should().NotContain("ws://");
    }

    // ── Degradation ────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_failed_fetch_is_a_warning_and_an_empty_tour_never_an_exception()
    {
        // "No tour" is a missing nicety; the widget has to open regardless.
        var logged = await ResolvedUrlAsync(hubUrl: "http://localhost:59999/mentor-hub");

        logged.Should().Contain("Could not fetch the tour");
    }

    [Fact]
    public async Task The_result_is_cached_so_a_dead_server_is_not_polled_on_every_open()
    {
        var logs = new TestLogCollector();
        var tour = new WasmMentorTour(
            new StubNavigationManager("https://client.test/"),
            new MentorAgentBlazorOptions { HubUrl = "http://localhost:59999/mentor-hub" },
            CollectingLogger<WasmMentorTour>(logs));

        await tour.GetStepsAsync();
        var afterFirst = logs.Records.Count;
        await tour.GetStepsAsync();
        await tour.GetStepsAsync();

        logs.Records.Count.Should().Be(afterFirst,
            "the empty result is cached, so replaying the tour must not re-attempt the fetch");
    }

    /// <summary>
    /// A <see cref="NavigationManager"/> with a fixed base URI. The real one needs a live Blazor
    /// renderer; all the tour uses is <c>BaseUri</c>.
    /// </summary>
    private sealed class StubNavigationManager : NavigationManager
    {
        internal StubNavigationManager(string baseUri) => Initialize(baseUri, baseUri);

        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }
}
