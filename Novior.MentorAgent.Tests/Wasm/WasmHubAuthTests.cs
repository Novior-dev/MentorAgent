using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Novior.MentorAgent.Blazor.Models;
using Novior.MentorAgent.Tests.Docs;
using Xunit;

namespace Novior.MentorAgent.Tests.Wasm;

/// <summary>
/// BUG-046 — the WASM client must be able to authenticate its hub connection.
///
/// Without it the server's identity bridge reads <c>HubCallerContext.User</c> and finds nothing, so
/// every <c>RequiredRoles</c> action is blocked, <c>RateLimitPerUser</c> becomes one global limit,
/// and — the serious one — every user of the deployment shares the single memory key
/// <c>"anonymous"</c> while <c>MemoryAutoCapture</c> keeps extracting personal facts into it.
///
/// The connection is built inside a <c>sealed</c> class with no seam a unit test can observe, so the
/// wiring half is checked at the source level, in the same style as <c>WireParityTests</c>. That is
/// weaker than executing it, and it is the only thing that can fail when someone "simplifies"
/// <c>WithUrl(options.HubUrl, …)</c> back to <c>WithUrl(options.HubUrl)</c> — which is exactly how
/// this defect existed in the first place.
/// </summary>
public class WasmHubAuthTests
{
    private static string HubClientSource()
    {
        var path = Path.Combine(RepositoryRoot(), RepoFiles.Blazor, "Hub", "MentorHubClient.cs");
        File.Exists(path).Should().BeTrue($"MentorHubClient.cs should be at {path}");
        return File.ReadAllText(path);
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, RepoFiles.Solution)))
            dir = dir.Parent;

        dir.Should().NotBeNull("the tests must be able to find the repository root");
        return dir!.FullName;
    }

    [Fact]
    public void The_options_expose_an_access_token_provider()
    {
        var property = typeof(MentorAgentBlazorOptions).GetProperty(nameof(MentorAgentBlazorOptions.AccessTokenProvider));

        property.Should().NotBeNull(
            "a WASM host has no other way to authenticate the hub: MentorHubClient is sealed, " +
            "AddMentorAgentBlazor takes no connection argument, and a cookie cannot travel " +
            "cross-origin over HTTP");
        property!.CanWrite.Should().BeTrue();
    }

    [Fact]
    public void The_options_expose_a_connection_escape_hatch()
    {
        typeof(MentorAgentBlazorOptions)
            .GetProperty(nameof(MentorAgentBlazorOptions.ConfigureConnection))
            .Should().NotBeNull("headers, transports and credentials must be reachable without a new option each time");
    }

    [Fact]
    public async Task A_null_provider_is_the_documented_signed_out_state_and_not_a_crash()
    {
        // The provider is Func<Task<string?>>: returning null must be legal, because a client that
        // has not signed in yet still has to connect — the widget is usable anonymously by design.
        var options = new MentorAgentBlazorOptions { AccessTokenProvider = () => Task.FromResult<string?>(null) };

        options.AccessTokenProvider.Should().NotBeNull();
        (await options.AccessTokenProvider!()).Should().BeNull();
    }

    [Fact]
    public void The_hub_client_passes_the_token_provider_to_the_connection()
    {
        var source = HubClientSource();

        source.Should().Contain("AccessTokenProvider",
            "the option is inert unless MentorHubClient hands it to HttpConnectionOptions");
        source.Should().Contain("options.ConfigureConnection",
            "the escape hatch must run too, or a host cannot set a header");
    }

    [Fact]
    public void The_hub_client_configures_the_connection_rather_than_taking_the_bare_url_overload()
    {
        var source = HubClientSource();
        var withUrl = source.Split("WithUrl(", StringSplitOptions.None);

        withUrl.Length.Should().Be(2, "there should be exactly one WithUrl call");

        // The bare overload — WithUrl(hubUrl) with no callback — is the defect. Anchor on the
        // closing of the first argument (the resolved HubUrl since BUG-145): a configured call
        // continues with a comma.
        var afterCall = withUrl[1];
        afterCall.Should().MatchRegex(@"^[\w.]+,",
            "WithUrl must take the HttpConnectionOptions callback; the single-argument overload is BUG-046");
    }
}
