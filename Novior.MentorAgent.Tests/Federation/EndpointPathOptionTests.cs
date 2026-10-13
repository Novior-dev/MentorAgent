using System.Net;
using FluentAssertions;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace Novior.MentorAgent.Tests.Federation;

/// <summary>
/// <see cref="Options.MentorOptions.McpServerPath"/> and <see cref="Options.MentorOptions.A2AServerPath"/>.
///
/// <para>
/// Both options were public, documented ("Endpoint path for the MCP server", "Base path for the A2A
/// endpoint"), set in README snippets — and read by nothing: <c>MapMentorAgentMcp</c> and
/// <c>MapMentorAgentA2A</c> had their own <c>"/mcp"</c> / <c>"/a2a"</c> defaults. Found by the rc.11
/// README audit. The options are now the defaults; a path passed to the method still wins.
/// </para>
/// </summary>
public sealed class EndpointPathOptionTests
{
    private static StringContent ToolsList() => new(
        """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", System.Text.Encoding.UTF8, "application/json");

    [Fact]
    public async Task McpServerPath_moves_the_mcp_endpoint()
    {
        await using var app = await ServerAppFactory.CreateAsync(
            configure: o => { o.McpServerEnabled = true; o.McpServerPath = "/tools"; },
            configureApp: a => { a.UseCors(); a.UseEndpoints(e => e.MapMentorAgentMcp()); });

        (await app.Client.PostAsync("/tools", ToolsList())).StatusCode.Should().NotBe(HttpStatusCode.NotFound,
            "the option names the path when the host passes none");
        (await app.Client.PostAsync("/mcp", ToolsList())).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A2AServerPath_moves_the_task_endpoint_and_the_card_points_at_it()
    {
        await using var app = await ServerAppFactory.CreateAsync(
            configure: o => { o.A2AServerEnabled = true; o.AppName = "Acme"; o.A2AServerPath = "/agent"; },
            configureApp: a => a.UseEndpoints(e => e.MapMentorAgentA2A()));

        var card = await app.Client.GetStringAsync("/.well-known/agent-card.json");
        card.Should().Contain("\"url\":\"/agent\"",
            "with no A2AServerUrl the card falls back to the mapped path, which is now the option");

        (await app.Client.PostAsync("/agent", ToolsList())).StatusCode.Should().NotBe(HttpStatusCode.NotFound);
        (await app.Client.PostAsync("/a2a", ToolsList())).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_path_passed_to_the_method_still_wins()
    {
        await using var app = await ServerAppFactory.CreateAsync(
            configure: o => { o.A2AServerEnabled = true; o.AppName = "Acme"; o.A2AServerPath = "/agent"; },
            configureApp: a => a.UseEndpoints(e => e.MapMentorAgentA2A("/explicit")));

        (await app.Client.PostAsync("/explicit", ToolsList())).StatusCode.Should().NotBe(HttpStatusCode.NotFound);
        (await app.Client.PostAsync("/agent", ToolsList())).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_defaults_are_unchanged()
    {
        await using var app = await ServerAppFactory.CreateAsync(
            configure: o => { o.A2AServerEnabled = true; o.AppName = "Acme"; o.McpServerEnabled = true; },
            configureApp: a => { a.UseCors(); a.UseEndpoints(e => { e.MapMentorAgentMcp(); e.MapMentorAgentA2A(); }); });

        (await app.Client.PostAsync("/mcp", ToolsList())).StatusCode.Should().NotBe(HttpStatusCode.NotFound);
        (await app.Client.PostAsync("/a2a", ToolsList())).StatusCode.Should().NotBe(HttpStatusCode.NotFound);
    }
}
