using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Novior.MentorAgent.Tests.Harness;

/// <summary>
/// A minimal Streamable-HTTP MCP client: <c>initialize</c>, then JSON-RPC requests on the session.
///
/// <para>
/// The SDK binds a session to the user who opened it, so a test that needs two callers opens two
/// sessions, each with its own <paramref name="asUser"/> — a user id, or <c>"-"</c> for anonymous
/// (<see cref="ServerAppFactory.AsUserHeader"/>). Null keeps the fixture's own principal.
/// </para>
/// </summary>
internal sealed class McpTestSession
{
    private readonly HttpClient _http;
    private readonly string? _asUser;
    private readonly string _path;
    private string? _sessionId;
    private int _id;

    private McpTestSession(HttpClient http, string? asUser, string path)
    {
        _http = http;
        _asUser = asUser;
        _path = path;
    }

    internal static async Task<McpTestSession> OpenAsync(HttpClient http, string? asUser = null, string path = "/mcp")
    {
        var s = new McpTestSession(http, asUser, path);
        s.Initialize = await s.SendAsync("initialize", new
        {
            protocolVersion = "2025-06-18",
            capabilities = new { },
            clientInfo = new { name = "tests", version = "1" },
        });
        await s.PostAsync(new { jsonrpc = "2.0", method = "notifications/initialized" });
        return s;
    }

    /// <summary>The <c>initialize</c> response: <c>result.serverInfo</c> names the server.</summary>
    internal JsonElement Initialize { get; private set; }

    internal async Task<JsonElement> SendAsync(string method, object @params)
    {
        var id = Interlocked.Increment(ref _id);
        var body = await PostAsync(new { jsonrpc = "2.0", id, method, @params });
        var json = body.TrimStart().StartsWith('{')
            ? body
            : body.Split('\n').Where(l => l.StartsWith("data:", StringComparison.Ordinal))
                  .Select(l => l[5..].Trim()).Last(l => l.Contains("\"id\"", StringComparison.Ordinal));
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    /// <summary>The names <c>tools/list</c> returns.</summary>
    internal async Task<IReadOnlyList<string>> ListToolNamesAsync()
    {
        var response = await SendAsync("tools/list", new { });
        return [.. response.GetProperty("result").GetProperty("tools").EnumerateArray()
                           .Select(t => t.GetProperty("name").GetString()!)];
    }

    /// <summary><c>tools/call</c>: the concatenated text blocks and the <c>isError</c> flag.</summary>
    internal async Task<(string Text, bool IsError)> CallAsync(string name, object? arguments = null)
    {
        var response = await SendAsync("tools/call", new { name, arguments = arguments ?? new { } });
        return (Text(response), IsError(response));
    }

    internal static string Text(JsonElement response) =>
        response.TryGetProperty("result", out var r) && r.TryGetProperty("content", out var c)
            ? string.Concat(c.EnumerateArray().Select(b => b.TryGetProperty("text", out var t) ? t.GetString() : ""))
            : response.ToString();

    internal static bool IsError(JsonElement response) =>
        response.TryGetProperty("result", out var r)
        && r.TryGetProperty("isError", out var e)
        && e.ValueKind == JsonValueKind.True;

    private async Task<string> PostAsync(object payload)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, _path)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (_sessionId is not null) req.Headers.Add("Mcp-Session-Id", _sessionId);
        if (_asUser is not null) req.Headers.Add(ServerAppFactory.AsUserHeader, _asUser);

        using var res = await _http.SendAsync(req);
        if (res.Headers.TryGetValues("Mcp-Session-Id", out var ids)) _sessionId = ids.First();
        return await res.Content.ReadAsStringAsync();
    }
}
