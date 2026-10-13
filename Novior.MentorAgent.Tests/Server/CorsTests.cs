using System.Net;
using FluentAssertions;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Novior.MentorAgent.Tests.Server;

/// <summary>
/// The CORS recipe from the server README, executed.
///
/// <para>
/// CORS is the host's responsibility, not the package's — Novior.MentorAgent.Server maps endpoints and
/// deliberately does not install a policy. So what is under test here is the <b>documentation</b>:
/// the README calls this "the #1 cause of SignalR connection failures" and gives an exact snippet,
/// and a reader who follows it gets a working cross-origin handshake or does not. Between the
/// snippet being written and now, nothing has ever run it.
/// </para>
///
/// <para>
/// The failure it guards against is the nastiest kind: the browser blocks the handshake, the server
/// logs nothing because the request never arrives, and the widget simply never connects.
/// </para>
/// </summary>
public sealed class CorsTests
{
    private const string Policy = "MentorAgentClients";
    private const string ClientOrigin = "http://localhost:5001";
    private const string UnknownOrigin = "http://evil.test";

    /// <summary>The README's policy, transcribed.</summary>
    private static Task<ServerAppFactory> CreateWithCorsAsync() => ServerAppFactory.CreateAsync(
        configureServices: services => services.AddCors(options =>
            options.AddPolicy(Policy, policy => policy
                .WithOrigins(
                    "http://localhost:5001",
                    "https://localhost:7001",
                    "http://localhost:5173",
                    "http://localhost:4200")
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials())),
        configureApp: app => app.UseCors(Policy));

    private static HttpRequestMessage Preflight(string path, string origin, string method = "POST")
    {
        var request = new HttpRequestMessage(HttpMethod.Options, path);
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", method);
        request.Headers.Add("Access-Control-Request-Headers", "content-type,x-requested-with");
        return request;
    }

    // ── The documented policy works on every MentorAgent endpoint ───────────────

    [Theory]
    [InlineData("/mentor-hub/negotiate")]
    [InlineData("/mentor/chat")]
    [InlineData("/mentor/approve")]
    [InlineData("/mentor/cancel")]
    [InlineData("/mentor/tour")]
    public async Task A_listed_origin_is_allowed_to_preflight_every_endpoint(string path)
    {
        // All of them, because a policy applied before MapMentorAgentServer covers the whole
        // surface — and a client that can negotiate but cannot POST an approval is stuck at the
        // first confirmation dialog with no way forward.
        await using var app = await CreateWithCorsAsync();

        var response = await app.Client.SendAsync(Preflight(path, ClientOrigin));

        response.Headers.GetValues("Access-Control-Allow-Origin").Should().Contain(ClientOrigin);
    }

    [Fact]
    public async Task The_preflight_allows_credentials_which_is_what_signalr_requires()
    {
        // SignalR browser clients send credentials. Without this header the handshake is blocked
        // even though the origin was allowed — which is the trap the README's `.AllowCredentials()`
        // line exists to close.
        await using var app = await CreateWithCorsAsync();

        var response = await app.Client.SendAsync(Preflight("/mentor-hub/negotiate", ClientOrigin));

        response.Headers.GetValues("Access-Control-Allow-Credentials").Should().Contain("true");
    }

    [Fact]
    public async Task The_preflight_echoes_the_requested_method_and_headers()
    {
        await using var app = await CreateWithCorsAsync();

        var response = await app.Client.SendAsync(Preflight("/mentor/approve", ClientOrigin));

        string.Join(",", response.Headers.GetValues("Access-Control-Allow-Methods"))
            .Should().Contain("POST");
        string.Join(",", response.Headers.GetValues("Access-Control-Allow-Headers"))
            .Should().Contain("content-type");
    }

    [Fact]
    public async Task An_unlisted_origin_gets_no_allow_header()
    {
        // The point of listing origins explicitly. A policy that answers every origin would make
        // the credentialed endpoints reachable from any page the user happens to have open.
        await using var app = await CreateWithCorsAsync();

        var response = await app.Client.SendAsync(Preflight("/mentor-hub/negotiate", UnknownOrigin));

        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse(
            "an origin outside the allow-list must not be told it is welcome");
    }

    [Fact]
    public async Task An_actual_cross_origin_post_carries_the_allow_header_too()
    {
        // The preflight is only half of it: the real request needs the header as well, or the
        // browser discards a response the server considered perfectly successful.
        await using var app = await CreateWithCorsAsync();

        var request = new HttpRequestMessage(HttpMethod.Post, $"/mentor/approve?actionId={Guid.NewGuid()}&approved=true");
        request.Headers.Add("Origin", ClientOrigin);

        var response = await app.Client.SendAsync(request);

        // 404: the id is made up, so nothing waits for it (BUG-128). The browser needs the header on
        // that answer too, or the client cannot read it.
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Headers.GetValues("Access-Control-Allow-Origin").Should().Contain(ClientOrigin);
    }

    // ── Without the policy ──────────────────────────────────────────────────────

    [Fact]
    public async Task Without_a_policy_a_cross_origin_preflight_gets_nothing()
    {
        // The default state, and the reason the README leads with the warning: nothing errors,
        // nothing is logged, the browser just refuses to proceed.
        await using var app = await ServerAppFactory.CreateAsync();

        var response = await app.Client.SendAsync(Preflight("/mentor-hub/negotiate", ClientOrigin));

        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
    }

    [Fact]
    public async Task Same_origin_requests_never_needed_the_policy()
    {
        // The documented "you do NOT need CORS when…" case: no Origin header, no CORS involved.
        await using var app = await ServerAppFactory.CreateAsync();

        var response = await app.Client.GetAsync("/mentor/tour");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Allowing_any_origin_together_with_credentials_is_refused_by_the_framework()
    {
        // The exact reason the README says to list origins explicitly rather than reaching for
        // AllowAnyOrigin(). The CORS spec forbids the combination and ASP.NET Core rejects it while
        // *building the policy* — so a host that tries the shortcut fails to start with a message
        // naming the fix, rather than starting and serving a handshake that never completes. That
        // is the good outcome, and it is worth pinning: an ordinary misconfiguration here would be
        // invisible from the server side, because a blocked preflight never reaches the server.
        var act = async () => await ServerAppFactory.CreateAsync(
            configureServices: services => services.AddCors(options =>
                options.AddPolicy(Policy, policy => policy
                    .AllowAnyOrigin()
                    .AllowAnyHeader()
                    .AllowAnyMethod()
                    .AllowCredentials())),
            configureApp: app => app.UseCors(Policy));

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*wildcard*credentials*")
            .And.Message.Should().Contain("listing individual origins",
                "the error has to name the fix the README gives");
    }
}
