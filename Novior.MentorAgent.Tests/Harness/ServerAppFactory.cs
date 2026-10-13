using System.Security.Claims;
using System.Text.Encodings.Web;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Server.Extensions;
using Novior.MentorAgent.Tests.App;
using Novior.MentorAgent.Tests.Fakes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Novior.MentorAgent.Tests.Harness;

/// <summary>
/// Hosts <c>Novior.MentorAgent.Server</c> in memory: a real SignalR hub, real SSE endpoints and the real
/// approve/cancel/tour/metrics routes, over <see cref="TestServer"/>.
///
/// <para>
/// This is what makes the server package testable at all. Its behaviour lives in the seams between
/// SignalR, DI scoping and HTTP — a scope created per hub invocation, an approval that must arrive
/// over HTTP because the hub is busy, a message size ceiling that aborts a connection rather than
/// erroring. None of that can be reached by calling a method on the hub class directly, and all of
/// it is behaviour the READMEs promise to client authors who are not compiling against us.
/// </para>
/// </summary>
internal sealed class ServerAppFactory : IAsyncDisposable
{
    /// <summary>
    /// Header that makes one HTTP request act as a different caller: a user id, or <c>"-"</c> for
    /// anonymous. Lets a single application serve a hub connection owned by one user and an HTTP
    /// request made by somebody else, which is the only way to test "a stranger cannot read this".
    /// </summary>
    internal const string AsUserHeader = TestAuthHandler.AsUserHeader;

    /// <summary>
    /// Header that overrides, for one request, the claim types the user id is issued under: a comma-separated
    /// list (<c>"name"</c> stands for <see cref="ClaimTypes.Name"/>, <c>"nameid"</c> for
    /// <see cref="ClaimTypes.NameIdentifier"/>). Lets one test hold a hub connection and an HTTP caller whose
    /// principals carry the same value under different claims (BUG-130).
    /// </summary>
    internal const string IdClaimsHeader = TestAuthHandler.IdClaimsHeader;

    private readonly IHost _host;

    private ServerAppFactory(IHost host, FakeChatClient chat, TestLogCollector logs)
    {
        _host = host;
        Chat = chat;
        Logs = logs;
    }

    /// <summary>The scripted model behind the server.</summary>
    internal FakeChatClient Chat { get; }

    /// <summary>Server-side log records, for the degradation assertions.</summary>
    internal TestLogCollector Logs { get; }

    /// <summary>An <see cref="HttpClient"/> bound to the in-memory server.</summary>
    internal HttpClient Client => _host.GetTestClient();

    private TestServer Server => _host.GetTestServer();

    /// <summary>The host's service provider, for asserting on resolved options.</summary>
    internal IServiceProvider Services => _host.Services;

    /// <summary>
    /// Builds the host. <paramref name="role"/> signs the caller in with that role for every
    /// request — the metrics endpoint is role-gated, and anonymous is a case in its own right.
    /// </summary>
    /// <param name="configureServices">
    /// Extra host registrations, applied before <c>AddMentorAgentServer()</c>. This is how the CORS
    /// tests install the exact policy the server README tells hosts to write — CORS is the host's
    /// responsibility, not the package's, so the only way to verify the documented recipe is to
    /// follow it.
    /// </param>
    /// <param name="configureApp">
    /// Extra middleware, applied after <c>UseRouting()</c> and before the endpoints. An endpoint mapped
    /// here with its own <c>UseEndpoints</c> runs BEFORE authentication, so its handler never sees a
    /// signed-in user — use <paramref name="mapEndpoints"/> for that.
    /// </param>
    /// <param name="mapEndpoints">
    /// Extra endpoints mapped next to the server's own, after <c>UseAuthentication()</c> and
    /// <c>UseAuthorization()</c> — how an MCP handler gets to see the caller's principal.
    /// </param>
    /// <param name="roleClaimType">
    /// The claim type the test principal's roles are issued under, and the identity's
    /// <c>RoleClaimType</c>. Null keeps <see cref="ClaimTypes.Role"/>; <c>"roles"</c> is what an Entra ID
    /// token, or JwtBearer with <c>MapInboundClaims = false</c>, produces (BUG-103).
    /// </param>
    /// <param name="idClaimTypes">
    /// The claim types the user id is issued under, for this principal and for <see cref="AsUserHeader"/>
    /// callers. Null keeps <see cref="ClaimTypes.NameIdentifier"/> and <see cref="ClaimTypes.Name"/>;
    /// <c>[ClaimTypes.Name]</c> is what Windows authentication gives, <c>["sub"]</c> what JwtBearer with
    /// <c>MapInboundClaims = false</c> gives (BUG-130).
    /// </param>
    /// <param name="configureServicesFirst">
    /// Host registrations made before <c>AddMentorAgent()</c> and <c>AddMentorAgentServer()</c> — for the
    /// cases where the order matters, as <c>AddRazorComponents()</c> before the server (BUG-131).
    /// </param>
    internal static async Task<ServerAppFactory> CreateAsync(
        Action<MentorOptions>? configure = null,
        string? userId = null,
        string[]? roles = null,
        Action<IServiceCollection>? configureServices = null,
        Action<IApplicationBuilder>? configureApp = null,
        Action<IEndpointConventionBuilder>? configureTransports = null,
        Action<Microsoft.AspNetCore.Routing.IEndpointRouteBuilder>? mapEndpoints = null,
        string? roleClaimType = null,
        string[]? idClaimTypes = null,
        Action<IServiceCollection>? configureServicesFirst = null)
    {
        var chat = new FakeChatClient();
        var logs = new TestLogCollector();

        var builder = Host.CreateDefaultBuilder();
        builder.ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureLogging(b =>
            {
                b.ClearProviders();
                b.SetMinimumLevel(LogLevel.Debug);
                b.AddProvider(new TestLoggerProvider(logs));
            });

            web.ConfigureServices(services =>
            {
                services.AddSignalR();
                services.AddRouting();

                // A scheme that authenticates every request as the supplied principal. Real hosts
                // use JWT or cookies; what MentorAgent consumes is only the resulting
                // ClaimsPrincipal, so this keeps the test about MentorAgent rather than about a
                // token format.
                services.AddSingleton(new TestPrincipal(userId, roles ?? [], roleClaimType ?? ClaimTypes.Role,
                    idClaimTypes ?? [ClaimTypes.NameIdentifier, ClaimTypes.Name]));

                // Session snapshots are protected with Data Protection (BUG-110). Ephemeral keys: a
                // test must not write key files into the machine's profile.
                services.AddDataProtection().UseEphemeralDataProtectionProvider();
                services.AddAuthentication(TestAuthHandler.Scheme)
                        .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.Scheme, _ => { });
                services.AddAuthorization();

                configureServicesFirst?.Invoke(services);

                services.AddScoped<TestOrderService>();
                services.AddScoped<TestWeatherService>();
                services.AddScoped<TestShippingAgent>();
                services.AddScoped<TestExpenseSkill>();
                services.AddScoped<TestAnalysisTools>();

                services.AddMentorAgent(o =>
                {
                    o.AppName = "TestServerApp";
                    o.ScanAssemblies = [typeof(TestOrderService).Assembly];
                    o.ChatClient = chat;
                    configure?.Invoke(o);
                });

                services.AddMentorAgentServer();
                configureServices?.Invoke(services);
            });

            web.Configure(app =>
            {
                app.UseRouting();
                configureApp?.Invoke(app);
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(e =>
                {
                    e.MapMentorAgentServer(configureTransports: configureTransports);
                    mapEndpoints?.Invoke(e);
                });
            });
        });

        var host = await builder.StartAsync();
        return new ServerAppFactory(host, chat, logs);
    }

    /// <summary>
    /// A real <see cref="HubConnection"/> over the in-memory transport. Nothing about the hub's
    /// sequential dispatch — the reason approvals travel over HTTP — survives a fake connection,
    /// so the tests use the genuine client.
    /// </summary>
    internal HubConnection CreateHubConnection(string path = "/mentor-hub")
        => new HubConnectionBuilder()
            .WithUrl(new Uri(Server.BaseAddress, path.TrimStart('/')),
                     o => o.HttpMessageHandlerFactory = _ => Server.CreateHandler())
            .Build();

    /// <summary>
    /// A hub connection opened by a different caller than the fixture's principal — a user id, or
    /// <c>"-"</c> for anonymous (<see cref="AsUserHeader"/>). One application then hosts connections
    /// owned by two users, which is what "another user's snapshot" needs (BUG-110).
    /// </summary>
    internal HubConnection CreateHubConnectionAs(string user, string path = "/mentor-hub")
        => new HubConnectionBuilder()
            .WithUrl(new Uri(Server.BaseAddress, path.TrimStart('/')), o =>
            {
                o.HttpMessageHandlerFactory = _ => Server.CreateHandler();
                o.Headers[AsUserHeader] = user;
            })
            .Build();

    /// <summary>The in-memory server's base address, for clients built outside this class.</summary>
    internal Uri BaseAddress => Server.BaseAddress;

    /// <summary>A handler that reaches the in-memory server, for clients built outside this class.</summary>
    internal HttpMessageHandler CreateHandler() => Server.CreateHandler();

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    /// <summary>The principal every request is authenticated as, or anonymous when the id is null.</summary>
    internal sealed record TestPrincipal(string? UserId, string[] Roles, string RoleClaimType, string[] IdClaimTypes);

    private sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        TestPrincipal principal)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        // `new` because AuthenticationHandler also has a Scheme member; this one is the name, and
        // the base one is the resolved AuthenticationScheme. Both are wanted, so hide deliberately.
        internal new const string Scheme = "Test";

        /// <summary>
        /// Header that overrides the fixture's principal for one request: a user id, or <c>"-"</c>
        /// for anonymous.
        ///
        /// <para>
        /// Needed because some properties are only expressible with <b>two different callers on one
        /// application</b> — a hub connection owned by one user and an HTTP request made by
        /// somebody else. Without it every request carries the same identity and "a stranger cannot
        /// read this" is untestable.
        /// </para>
        /// </summary>
        internal const string AsUserHeader = "X-Test-As-User";

        internal const string IdClaimsHeader = "X-Test-Id-Claims";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var userId = principal.UserId;
            var roles = principal.Roles;

            if (Request.Headers.TryGetValue(AsUserHeader, out var asUser) && asUser.Count > 0)
            {
                var requested = asUser[0];
                if (requested == "-")
                    return Task.FromResult(AuthenticateResult.NoResult());   // anonymous on purpose
                userId = requested;
                roles = [];
            }

            if (userId is null)
                return Task.FromResult(AuthenticateResult.NoResult());

            var idClaimTypes = Request.Headers.TryGetValue(IdClaimsHeader, out var shape) && shape.Count > 0
                ? shape[0]!.Split(',').Select(t => t switch
                  {
                      "name" => ClaimTypes.Name,
                      "nameid" => ClaimTypes.NameIdentifier,
                      _ => t,
                  }).ToArray()
                : principal.IdClaimTypes;
            var claims = idClaimTypes.Select(type => new Claim(type, userId)).ToList();
            claims.AddRange(roles.Select(r => new Claim(principal.RoleClaimType, r)));

            var identity = new ClaimsIdentity(claims, Scheme, ClaimTypes.Name, principal.RoleClaimType);
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
