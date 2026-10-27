using System.Net;
using FluentAssertions;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Tests.Harness;
using Xunit;

namespace Novior.MentorAgent.Tests.Federation;

/// <summary>
/// The MCP endpoint keeps the sessions of the Streamable HTTP transport. ModelContextProtocol 2.0 made a server
/// stateless by default: no session, and no GET or DELETE on the endpoint. MentorAgent asks for the stateful
/// transport, so a client of the 1.x endpoint finds it as it was, the 400 of a GET without a session included.
/// </summary>
public sealed class McpSessionModeTests
{
    [Fact]
    public async Task Initialize_opens_a_session()
    {
        await using var app = await ServerAppFactory.CreateAsync(
            configure: o => o.McpServerEnabled = true,
            mapEndpoints: e => e.MapMentorAgentMcp());

        var mcp = await McpTestSession.OpenAsync(app.Client);

        mcp.SessionId.Should().NotBeNullOrEmpty("the initialize response carries Mcp-Session-Id");
    }

    [Fact]
    public async Task A_GET_without_a_session_answers_400_naming_the_missing_header()
    {
        await using var app = await ServerAppFactory.CreateAsync(
            configure: o => o.McpServerEnabled = true,
            mapEndpoints: e => e.MapMentorAgentMcp());

        // The stream a client opens with GET: without text/event-stream in Accept the SDK answers 406 first.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/mcp");
        request.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await app.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "the 1.0.0 CHANGELOG documents this answer");
        (await response.Content.ReadAsStringAsync()).Should().Contain("Mcp-Session-Id");
    }
}
