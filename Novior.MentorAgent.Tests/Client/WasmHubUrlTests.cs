using FluentAssertions;
using Novior.MentorAgent.Blazor.Extensions;
using Novior.MentorAgent.Blazor.Hub;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Novior.MentorAgent.Tests.Client;

/// <summary>
/// BUG-145 — a relative <c>HubUrl</c> in the WebAssembly client.
///
/// <para>
/// The default is <c>"/mentor-hub"</c>, and the README told same-origin hosts to keep it. The .NET
/// SignalR client makes an absolute <see cref="Uri"/> of whatever it is given. In the browser a bare
/// path parses as <c>file:///mentor-hub</c>: the negotiate request never left it ("TypeError: Failed
/// to fetch") and the widget did not connect. On Windows, where these tests run, the same string
/// throws <see cref="UriFormatException"/> while the client is built. The JavaScript client resolves the same string against the
/// page, which is what the WASM client now does with the <see cref="NavigationManager"/>.
/// </para>
///
/// <para>
/// The URL is read where the connection receives it: <c>ConfigureConnection</c> runs on the
/// <c>HttpConnectionOptions</c> when the connection is built, after the URL is set.
/// </para>
/// </summary>
public sealed class WasmHubUrlTests
{
    /// <summary>Resolves <see cref="MentorHubClient"/> the way a WASM host does and returns its URL.</summary>
    private static async Task<Uri?> ConnectedUrlAsync(string hubUrl, string? pageBase)
    {
        Uri? captured = null;
        var services = new ServiceCollection().AddLogging();

        // In WebAssembly the NavigationManager is a singleton, initialised before any service.
        if (pageBase is not null)
            services.AddSingleton<NavigationManager>(new StubNavigationManager(pageBase));

        services.AddMentorAgentBlazor(options =>
        {
            options.HubUrl = hubUrl;
            options.ConfigureConnection = connection => captured = connection.Url;
        });

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        provider.GetRequiredService<MentorHubClient>();
        return captured;
    }

    [Theory]
    [InlineData("/mentor-hub", "http://localhost:5321/", "http://localhost:5321/mentor-hub")]
    [InlineData("/mentor-hub", "https://shop.test/app/", "https://shop.test/mentor-hub")]
    [InlineData("mentor-hub", "https://shop.test/app/", "https://shop.test/app/mentor-hub")]
    public async Task A_relative_hub_url_connects_to_the_pages_server(string hubUrl, string pageBase, string expected)
    {
        var url = await ConnectedUrlAsync(hubUrl, pageBase);

        url.Should().Be(new Uri(expected),
            "a relative HubUrl means the server that serves the page; as written it became file:///mentor-hub");
    }

    [Fact]
    public async Task An_absolute_hub_url_is_used_as_written()
    {
        (await ConnectedUrlAsync("https://api.test:8443/mentor-hub", "http://localhost:5321/"))
            .Should().Be(new Uri("https://api.test:8443/mentor-hub"));
    }

    [Fact]
    public async Task Without_a_navigation_manager_the_client_is_still_built()
    {
        // A container with no NavigationManager falls back to the two-argument constructor, as before.
        (await ConnectedUrlAsync("https://api.test/mentor-hub", pageBase: null))
            .Should().Be(new Uri("https://api.test/mentor-hub"));
    }

    [Theory]
    [InlineData("/mentor-hub", "https://shop.test/", "https://shop.test/mentor-hub")]
    [InlineData("/mentor-hub", "https://shop.test/app/", "https://shop.test/mentor-hub")]
    [InlineData("mentor-hub", "https://shop.test/app/", "https://shop.test/app/mentor-hub")]
    [InlineData("/mentor-hub?tenant=a", "https://shop.test/", "https://shop.test/mentor-hub?tenant=a")]
    [InlineData("https://api.test/mentor-hub", "https://shop.test/", "https://api.test/mentor-hub")]
    [InlineData("http://localhost:5169/mentor-hub", null, "http://localhost:5169/mentor-hub")]
    [InlineData("/mentor-hub", null, "/mentor-hub")]
    [InlineData("ws://localhost:5169/mentor-hub", "https://shop.test/", "ws://localhost:5169/mentor-hub")]
    [InlineData("", "https://shop.test/", "")]
    public void ResolveHubUrl_follows_the_browsers_rules(string hubUrl, string? baseUri, string expected)
    {
        MentorHubClient.ResolveHubUrl(hubUrl, baseUri).Should().Be(expected);
    }

    /// <summary>A <see cref="NavigationManager"/> with a fixed base URI, as WebAssembly initialises it.</summary>
    private sealed class StubNavigationManager : NavigationManager
    {
        internal StubNavigationManager(string baseUri) => Initialize(baseUri, baseUri);

        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }
}
