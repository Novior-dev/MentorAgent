using System.Text.Json;
using FluentAssertions;
using Novior.MentorAgent.Models;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Novior.MentorAgent.Tests.Federation;

/// <summary>
/// An MCP server of the host that lists a tool without <c>inputSchema</c>. ModelContextProtocol 2.0 throws when it
/// reads such a tool, where 1.x gave it an empty schema, and the whole <c>tools/list</c> answer is lost with it: the
/// server is not connected, the failure is logged with its name, and the assistant goes on without its tools.
/// Driven over HTTP against a minimal server, because the SDK's own reading of the answer is what is under test.
/// </summary>
public sealed class McpToolWithoutSchemaTests
{
    private static async Task<WebApplication> StartServerAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.MapPost("/mcp", async (HttpContext ctx) =>
        {
            using var request = await JsonDocument.ParseAsync(ctx.Request.Body);
            var root = request.RootElement;
            if (!root.TryGetProperty("id", out var id))
            {
                ctx.Response.StatusCode = StatusCodes.Status202Accepted;   // a notification
                return;
            }

            object answer = root.GetProperty("method").GetString() switch
            {
                "initialize" => new
                {
                    jsonrpc = "2.0", id,
                    result = new
                    {
                        protocolVersion = root.GetProperty("params").GetProperty("protocolVersion").GetString(),
                        capabilities = new { tools = new { } },
                        serverInfo = new { name = "schemaless", version = "1" },
                    },
                },
                "tools/list" => new
                {
                    jsonrpc = "2.0", id,
                    result = new
                    {
                        tools = new object[]
                        {
                            new { name = "look_up_without_schema", description = "Has no input schema" },
                            new { name = "look_up_with_schema", description = "Has one", inputSchema = new { type = "object" } },
                        },
                    },
                },
                // Every MCP server answers ping; the client checks a pooled connection with it.
                "ping" => new { jsonrpc = "2.0", id, result = new { } },
                _ => new { jsonrpc = "2.0", id, error = new { code = -32601, message = "Method not found" } },
            };
            await ctx.Response.WriteAsJsonAsync(answer);
        });
        await app.StartAsync();
        return app;
    }

    /// <summary>The address the server listens on: the port is chosen when it starts.</summary>
    private static string Address(WebApplication app)
        => app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

    [Fact]
    public async Task A_server_listing_a_tool_without_inputSchema_is_left_out_and_named_in_the_log()
    {
        await using var server = await StartServerAsync();
        await using var host = MentorHostFixture.Create(o => o.McpServers =
        [
            new MentorMcpServer { Name = "schemaless", ServerUrl = Address(server) + "/mcp" },
        ]);
        host.Chat!.Say("ok");

        (await host.RunTurnAsync()).Should().BeNull("the assistant works without the server");

        host.FirstToolsSent.Should().NotContain(["look_up_without_schema", "look_up_with_schema"],
            "the server's whole tools/list answer is lost with the tool that has no schema");
        host.Logs.HasWarning("Failed to connect to MCP server 'schemaless'").Should().BeTrue(host.Logs.WarningText);
        host.Logs.ExceptionText.Should().Contain("inputSchema", "the logged exception names what the server left out");
    }
}
