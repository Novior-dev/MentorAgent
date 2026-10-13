using FluentAssertions;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Novior.MentorAgent.Tests.Federation;

/// <summary>
/// BUG-108 — the README's MCP steps, followed as written, gave a 500 on every request.
///
/// <para>
/// <c>MapMentorAgentMcp</c> attached the <c>MentorAgentMcp</c> CORS policy to the endpoint. ASP.NET Core
/// refuses to run an endpoint that carries CORS metadata when no CORS middleware ran — "contains CORS
/// metadata, but a middleware was not found that supports CORS" — and the README called
/// <c>app.UseCors()</c> something the policy "takes effect once" you add. Every sample and test called
/// it, so none could show the failure. Whether the host added the middleware cannot be read at map time
/// (ASP.NET Core records no such flag, and a host may add it after mapping), so the endpoint no longer
/// carries a policy of its own: MCP clients that are not browsers need none, and a policy that allows
/// any origin is not a default for an endpoint that runs actions. A host with a browser client opts in.
/// </para>
/// </summary>
public sealed class McpCorsTests
{
    private const string Origin = "https://elsewhere.example";

    private static HttpRequestMessage Preflight() => new(HttpMethod.Options, "/mcp")
    {
        Headers =
        {
            { "Origin", Origin },
            { "Access-Control-Request-Method", "POST" },
            { "Access-Control-Request-Headers", "content-type" },
        },
    };

    [Fact]
    public async Task The_mcp_endpoint_answers_on_a_pipeline_without_UseCors()
    {
        // The README's Steps 1-2 and nothing else.
        await using var app = await ServerAppFactory.CreateAsync(
            configure: o => o.McpServerEnabled = true,
            mapEndpoints: e => e.MapMentorAgentMcp());

        var mcp = await McpTestSession.OpenAsync(app.Client);
        var tools = await mcp.ListToolNamesAsync();

        tools.Should().Contain("count_open_orders");
    }

    [Fact]
    public async Task By_default_no_other_origin_is_granted_access()
    {
        await using var app = await ServerAppFactory.CreateAsync(
            configure: o => o.McpServerEnabled = true,
            configureApp: a => a.UseCors(),
            mapEndpoints: e => e.MapMentorAgentMcp());

        using var response = await app.Client.SendAsync(Preflight());

        // Only the header is asserted: without CORS metadata the preflight is answered by routing (405),
        // and what matters is that no browser on another origin is told it may call the endpoint.
        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse(
            "an endpoint that runs the application's actions must not be open to every web page by default");
    }

    [Fact]
    public async Task The_documented_opt_in_applies_the_MentorAgentMcp_policy()
    {
        // The one line the README gives a host with a browser-based MCP client.
        await using var app = await ServerAppFactory.CreateAsync(
            configure: o => o.McpServerEnabled = true,
            configureApp: a => a.UseCors(),
            mapEndpoints: e => e.MapMentorAgentMcp(configure: c => c.RequireCors("MentorAgentMcp")));

        using var response = await app.Client.SendAsync(Preflight());

        response.Headers.TryGetValues("Access-Control-Allow-Origin", out var allowed).Should().BeTrue();
        allowed!.Should().ContainSingle().Which.Should().BeOneOf("*", Origin);
    }

    [Fact]
    public async Task A_policy_the_host_registered_before_AddMentorAgent_is_not_replaced_by_any_origin()
    {
        // Review of the rc.14 fix: AddMentorAgent's own AddPolicy ran after the host's and overwrote it by
        // name, so a host that restricted its origins got AllowAnyOrigin on /mcp. Since rc.15 the policy comes
        // from AddMentorAgentMcp, which MentorHostFixture does not call: there this test had nothing that
        // could replace the host's policy. AddMentorAgentServer calls it.
        await using var app = await ServerAppFactory.CreateAsync(
            configure: o => o.McpServerEnabled = true,
            configureServicesFirst: s => s.AddCors(c => c.AddPolicy("MentorAgentMcp", p => p.WithOrigins("https://tools.example.com"))));

        var policy = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Cors.Infrastructure.CorsOptions>>()
            .Value.GetPolicy("MentorAgentMcp")!;

        policy.AllowAnyOrigin.Should().BeFalse();
        policy.Origins.Should().Equal("https://tools.example.com");
    }
}
