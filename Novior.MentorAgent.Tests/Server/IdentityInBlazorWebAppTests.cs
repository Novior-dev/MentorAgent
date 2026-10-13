using A2A;
using System.Net.Http.Json;
using System.Security.Claims;
using FluentAssertions;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Memory;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Novior.MentorAgent.Tests.Server;

/// <summary>
/// The caller of the hub, of <c>/mentor/chat</c> and of A2A reaches MentorAgent in a Blazor Web App too
/// (BUG-131).
///
/// <para>
/// Up to rc.14 MentorAgent read the user from the host's <see cref="AuthenticationStateProvider"/>. In an
/// app with <c>AddRazorComponents()</c> that is Blazor's, which throws outside a circuit, and the server's
/// bridge was registered with <c>TryAdd</c> and lost to it: every role-gated action was refused to everyone
/// with an error logged, and memory and <c>RateLimitPerUser</c> followed a connection, not a person. Now the
/// transports record their caller and the core reads it first; the host's provider answers in circuits.
/// </para>
/// </summary>
public sealed class IdentityInBlazorWebAppTests
{
    public enum HostShape
    {
        /// <summary><c>AddMentorAgentServer()</c>, then <c>AddRazorComponents()</c>: the server's provider wins.</summary>
        BlazorAfterTheServer,
        /// <summary><c>AddRazorComponents()</c> first: Blazor's provider, which throws outside a circuit.</summary>
        BlazorBeforeTheServer,
        /// <summary>A provider of the host's own, registered after us, that answers "anonymous" outside a circuit.</summary>
        HostProviderAnonymousOutsideACircuit,
        /// <summary>No <see cref="AuthenticationStateProvider"/> at all.</summary>
        NoProvider,
    }

    public enum Transport { Hub, Sse, A2A }

    internal sealed class AnonymousOutsideACircuit : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
            => Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    internal static void AddBlazor(IServiceCollection services)
        => services.AddRazorComponents().AddInteractiveServerComponents();

    internal static Task<ServerAppFactory> HostAsync(HostShape shape, Action<Novior.MentorAgent.Options.MentorOptions>? configure = null)
        => ServerAppFactory.CreateAsync(
            configure: o =>
            {
                o.A2AServerEnabled = true;
                o.A2AServerUrl = "http://localhost/a2a";
                o.MemoryAutoCapture = false;
                configure?.Invoke(o);
            },
            userId: "ada",
            roles: ["Admin"],
            configureServicesFirst: shape == HostShape.BlazorBeforeTheServer ? AddBlazor : null,
            configureServices: shape switch
            {
                HostShape.BlazorAfterTheServer => AddBlazor,
                HostShape.HostProviderAnonymousOutsideACircuit => s =>
                {
                    AddBlazor(s);
                    s.AddScoped<AuthenticationStateProvider, AnonymousOutsideACircuit>();
                },
                HostShape.NoProvider => s => s.RemoveAll<AuthenticationStateProvider>(),
                _ => null,
            },
            mapEndpoints: e => e.MapMentorAgentA2A());

    /// <summary>
    /// Runs one turn over <paramref name="transport"/>, as the fixture's principal (ada, with Admin) when
    /// <paramref name="user"/> is null, otherwise as that user without roles.
    /// </summary>
    internal static async Task RunAsAsync(ServerAppFactory app, Transport transport, string? user, string text)
    {
        switch (transport)
        {
            case Transport.Hub:
                await using (var hub = user is null ? app.CreateHubConnection() : app.CreateHubConnectionAs(user))
                {
                    await hub.StartAsync();
                    await hub.InvokeAsync("SendMessage", text, null).WaitAsync(TimeSpan.FromSeconds(30));
                }
                break;

            case Transport.Sse:
            {
                var request = new HttpRequestMessage(HttpMethod.Post, "/mentor/chat") { Content = JsonContent.Create(new { message = text }) };
                if (user is not null) request.Headers.Add(ServerAppFactory.AsUserHeader, user);
                var response = await app.Client.SendAsync(request);
                await response.Content.ReadAsStringAsync();
                break;
            }

            case Transport.A2A:
            {
                var client = app.Client;
                if (user is not null) client.DefaultRequestHeaders.Add(ServerAppFactory.AsUserHeader, user);
                var card = await new A2A.A2ACardResolver(new Uri("http://localhost"), client).GetAgentCardAsync();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await A2A.A2AClientFactory.Create(card, client).SendMessageAsync(text, A2A.Role.User, null, deadline.Token);
                break;
            }
        }
    }

    internal static List<string> ToolResults(ServerAppFactory app)
        => app.Chat.Calls
              .SelectMany(c => c.Messages)
              .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
              .Select(r => r.Result?.ToString() ?? "")
              .Distinct()
              .ToList();

    [Theory]
    [MemberData(nameof(Cases))]
    public Task The_caller_with_the_role_runs_the_action_and_one_without_is_refused_without_an_error(
        HostShape shape, Transport transport)
        => RolesAsync(shape, transport);

    internal static async Task RolesAsync(HostShape shape, Transport transport)
    {
        await using var app = await HostAsync(shape);

        app.Chat.CallTool("export_ledger").Say("Fatto.");
        await RunAsAsync(app, transport, user: null, "esporta il registro ordini");
        var afterAda = ToolResults(app);

        app.Chat.CallTool("export_ledger").Say("Serve il ruolo.");
        await RunAsAsync(app, transport, "bob", "esporta il registro ordini");
        var afterBob = ToolResults(app).Except(afterAda).ToList();

        afterAda.Should().Contain(r => r.Contains("ledger"),
            "ada holds Admin; {0} on {1} must see her. Results: {2}", transport, shape, string.Join(" | ", afterAda));
        afterBob.Should().Contain(r => r.Contains("requires one of the following roles"),
            "bob is signed in without Admin. Results: {0}", string.Join(" | ", afterBob));
        app.Logs.Records.Should().NotContain(r => r.Level >= LogLevel.Error,
            "a caller without the role is refused, not an error. Errors: {0}",
            string.Join(Environment.NewLine, app.Logs.Records.Where(r => r.Level >= LogLevel.Error).Select(r => r.Message)));
    }

    public static TheoryData<HostShape, Transport> Cases()
    {
        var data = new TheoryData<HostShape, Transport>();
        // The hub cases are in IdentityInBlazorWebAppHubTests: 4 s a connection in this harness, run in parallel.
        foreach (var shape in Enum.GetValues<HostShape>())
            foreach (var transport in new[] { Transport.Sse, Transport.A2A })
                data.Add(shape, transport);
        return data;
    }

    [Theory]
    [InlineData(HostShape.BlazorAfterTheServer)]
    [InlineData(HostShape.BlazorBeforeTheServer)]
    public async Task A_circuit_keeps_the_user_Blazor_gave_it(HostShape shape)
    {
        // What Blazor does when a circuit or a prerender starts: it hands the scope's provider the user it
        // authenticated. Whichever provider won the registration, MentorAgent must then read that user —
        // not the request's, and not nobody.
        await using var app = await HostAsync(shape);
        await using var scope = app.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        var provider = sp.GetRequiredService<AuthenticationStateProvider>();
        provider.Should().BeAssignableTo<IHostEnvironmentAuthenticationStateProvider>(
            "Blazor sets a circuit's user only through this interface");

        var carol = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "carol"), new Claim(ClaimTypes.Name, "carol")], "Cookies"));
        ((IHostEnvironmentAuthenticationStateProvider)provider)
            .SetAuthenticationState(Task.FromResult(new AuthenticationState(carol)));

        (await sp.GetRequiredService<MentorMemoryService>().GetUserIdAsync()).Should().Be("carol");
    }
}

/// <summary>
/// The hub half of <see cref="IdentityInBlazorWebAppTests"/>, in a class of its own so the two run in parallel.
/// </summary>
public sealed class IdentityInBlazorWebAppHubTests
{
    [Theory]
    [InlineData(IdentityInBlazorWebAppTests.HostShape.BlazorAfterTheServer)]
    [InlineData(IdentityInBlazorWebAppTests.HostShape.BlazorBeforeTheServer)]
    [InlineData(IdentityInBlazorWebAppTests.HostShape.HostProviderAnonymousOutsideACircuit)]
    [InlineData(IdentityInBlazorWebAppTests.HostShape.NoProvider)]
    public Task The_caller_with_the_role_runs_the_action_and_one_without_is_refused_without_an_error(
        IdentityInBlazorWebAppTests.HostShape shape)
        => IdentityInBlazorWebAppTests.RolesAsync(shape, IdentityInBlazorWebAppTests.Transport.Hub);

    [Theory]
    [InlineData(IdentityInBlazorWebAppTests.HostShape.BlazorAfterTheServer)]
    [InlineData(IdentityInBlazorWebAppTests.HostShape.BlazorBeforeTheServer)]
    public async Task The_rate_limit_follows_the_person_across_connections(IdentityInBlazorWebAppTests.HostShape shape)
    {
        // Up to rc.14, with Blazor's provider the key was per connection: a second tab was a new allowance.
        await using var app = await IdentityInBlazorWebAppTests.HostAsync(shape, o => o.RateLimitPerUser = 1);
        app.Chat.Say("Ciao.");

        await IdentityInBlazorWebAppTests.RunAsAsync(app, IdentityInBlazorWebAppTests.Transport.Hub, user: null, "ciao");
        var calls = app.Chat.CallCount;

        await IdentityInBlazorWebAppTests.RunAsAsync(app, IdentityInBlazorWebAppTests.Transport.Hub, user: null, "ciao di nuovo");
        app.Chat.CallCount.Should().Be(calls, "ada's second connection is the same person, over her allowance");

        await IdentityInBlazorWebAppTests.RunAsAsync(app, IdentityInBlazorWebAppTests.Transport.Hub, "bob", "ciao");
        app.Chat.CallCount.Should().BeGreaterThan(calls, "bob has an allowance of his own");
    }
}
