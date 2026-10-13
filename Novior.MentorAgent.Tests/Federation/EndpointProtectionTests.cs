using System.Net;
using FluentAssertions;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace Novior.MentorAgent.Tests.Federation;

/// <summary>
/// The two endpoints MentorAgent publishes to other machines — <c>/mcp</c> and <c>/a2a</c> — and
/// whether a host can put a lock on them.
///
/// <para>
/// It could not. Both mapping methods discarded the convention builders they got back, so
/// <c>RequireAuthorization()</c> had nowhere to attach and the only way to protect the endpoints was
/// to stop calling the methods and re-map everything by hand. Found by reading the rc.4 sample's own
/// surface: <c>/mcp</c> answered a full tool list to an anonymous caller, and <c>update_stock</c> was
/// on it. <c>MapMentorAgentServer(configureTransports:)</c> had already established the shape of the
/// fix (BUG-060); these two had been left behind.
/// </para>
/// </summary>
public sealed class EndpointProtectionTests
{
    [Fact]
    public async Task The_mcp_endpoint_can_be_protected_by_the_host()
    {
        await using var app = await ServerAppFactory.CreateAsync(
            configure: o => o.McpServerEnabled = true,
            configureApp: a => { a.UseCors(); a.UseAuthorization(); a.UseEndpoints(e => e.MapMentorAgentMcp(configure: x => x.RequireAuthorization())); });

        var res = await app.Client.PostAsync("/mcp", new StringContent(
            """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""",
            System.Text.Encoding.UTF8, "application/json"));

        res.StatusCode.Should().BeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden],
            "the host asked for this endpoint to require authentication");
    }

    [Fact]
    public async Task Without_the_handle_the_mcp_endpoint_stays_open_exactly_as_before()
    {
        // The compatibility guarantee: adding the parameter must change nothing for hosts that do
        // not pass it.
        await using var app = await ServerAppFactory.CreateAsync(
            configure: o => o.McpServerEnabled = true,
            configureApp: a => { a.UseCors(); a.UseAuthorization(); a.UseEndpoints(e => e.MapMentorAgentMcp()); });

        var res = await app.Client.PostAsync("/mcp", new StringContent(
            """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""",
            System.Text.Encoding.UTF8, "application/json"));

        res.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_a2a_endpoints_can_be_protected_by_the_host()
    {
        await using var app = await ServerAppFactory.CreateAsync(
            configure: o => { o.A2AServerEnabled = true; o.AppName = "Acme"; },
            configureApp: a => { a.UseAuthorization(); a.UseEndpoints(e => e.MapMentorAgentA2A(configure: x => x.RequireAuthorization())); });

        var card = await app.Client.GetAsync("/.well-known/agent-card.json");

        card.StatusCode.Should().BeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden],
            "the card is a discovery document, and some hosts do not publish it to the world");
    }

    [Fact]
    public async Task Without_the_handle_the_agent_card_stays_public()
    {
        await using var app = await ServerAppFactory.CreateAsync(
            configure: o => { o.A2AServerEnabled = true; o.AppName = "Acme"; },
            configureApp: a => { a.UseAuthorization(); a.UseEndpoints(e => e.MapMentorAgentA2A()); });

        (await app.Client.GetAsync("/.well-known/agent-card.json")).IsSuccessStatusCode
            .Should().BeTrue();
    }
}
