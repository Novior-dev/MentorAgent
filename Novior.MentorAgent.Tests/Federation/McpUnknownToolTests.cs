using System.Text.Json;
using FluentAssertions;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Novior.MentorAgent.Tests.Federation;

/// <summary>
/// BUG-101. A <c>tools/call</c> naming a tool the MCP server does not list.
///
/// <para>
/// The server withholds every gated action from <c>tools/list</c>, so a client holding an older list, or
/// a model guessing a name, can ask for one. The call handler threw
/// <see cref="InvalidOperationException"/>, which the SDK treats as a tool that crashed: an Error line with
/// a stack trace in the host's log, and <c>"An error occurred invoking '…'"</c> for the caller, which then
/// cannot tell an unknown tool from a failed one and may retry.
/// </para>
///
/// <para>
/// The specification's shape for an unknown name is a protocol error (<c>-32602</c>), but the SDK logs any
/// exception out of the call handler — <c>McpProtocolException</c> included — as a crashed tool. So the
/// answer is a result with <c>isError</c> and a text that says the tool is unknown: no exception, no Error
/// line, and the same words for a withheld and an undefined name.
/// </para>
/// </summary>
public sealed class McpUnknownToolTests
{
    private static Task<ServerAppFactory> CreateAsync() => ServerAppFactory.CreateAsync(
        configure: o => o.McpServerEnabled = true,
        configureApp: a => { a.UseCors(); a.UseEndpoints(e => e.MapMentorAgentMcp()); });

    [Theory]
    [InlineData("cancel_order")]   // a real action, withheld because it is gated
    [InlineData("no_such_tool")]   // a name that was never defined
    public async Task An_unlisted_name_is_answered_as_an_unknown_tool(string name)
    {
        await using var app = await CreateAsync();
        var mcp = await McpTestSession.OpenAsync(app.Client);

        var response = await mcp.SendAsync("tools/call", new { name, arguments = new { } });

        Text(response).Should().Contain($"Unknown tool: '{name}'",
            $"the caller must learn the tool is not offered, not that it failed; got {response}");
        response.GetProperty("result").GetProperty("isError").GetBoolean().Should().BeTrue();
    }

    private static string Text(JsonElement response) =>
        response.TryGetProperty("result", out var r) && r.TryGetProperty("content", out var c)
            ? string.Concat(c.EnumerateArray().Select(b => b.TryGetProperty("text", out var t) ? t.GetString() : ""))
            : response.ToString();

    [Fact]
    public async Task A_withheld_and_an_undefined_name_get_the_same_answer()
    {
        // An unauthenticated client must not learn which gated actions exist by probing names.
        await using var app = await CreateAsync();
        var mcp = await McpTestSession.OpenAsync(app.Client);

        var withheld = await mcp.SendAsync("tools/call", new { name = "cancel_order", arguments = new { } });
        var undefined = await mcp.SendAsync("tools/call", new { name = "no_such_tool", arguments = new { } });

        Text(withheld).Replace("cancel_order", "X").Should().Be(Text(undefined).Replace("no_such_tool", "X"));
    }

    [Fact]
    public async Task An_unlisted_name_leaves_no_error_in_the_host_log()
    {
        await using var app = await CreateAsync();
        var mcp = await McpTestSession.OpenAsync(app.Client);

        await mcp.SendAsync("tools/call", new { name = "cancel_order", arguments = new { } });
        await mcp.SendAsync("tools/call", new { name = "no_such_tool", arguments = new { } });

        var errors = app.Logs.Records.Where(r => r.Level >= LogLevel.Error).ToList();
        errors.Should().BeEmpty(
            "nothing failed: the caller asked for a tool this server does not offer. Logged: {0}",
            string.Join(" | ", errors.Select(r => $"[{r.Level}] {r.Category}: {r.Message}")));
    }

    [Fact]
    public async Task A_listed_tool_is_still_called()
    {
        // Control: the lookup that now refuses unknown names still finds the published ones.
        await using var app = await CreateAsync();
        var mcp = await McpTestSession.OpenAsync(app.Client);

        var response = await mcp.SendAsync("tools/call", new { name = "count_open_orders", arguments = new { } });

        response.TryGetProperty("result", out var result).Should().BeTrue(response.ToString());
        var failed = result.TryGetProperty("isError", out var isError) && isError.ValueKind == JsonValueKind.True;
        failed.Should().BeFalse(result.ToString());
    }
}
