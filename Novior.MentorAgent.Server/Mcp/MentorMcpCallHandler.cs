using System.Diagnostics;
using System.Security.Claims;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Core.Mcp;
using Novior.MentorAgent.Observability;
using Novior.MentorAgent.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

// Story 105: moved from MentorAgent with the MCP server it serves — it reads the request's remote
// address through IHttpContextAccessor, which is ASP.NET Core.
namespace Novior.MentorAgent.Server.Mcp;

/// <summary>
/// The MCP server's <c>tools/call</c>, with the gates every other surface already applies (BUG-107):
/// <see cref="MentorOptions.RateLimitPerUser"/>, the tool metrics, and — through
/// <see cref="McpToolResultFunction"/> — <see cref="MentorOptions.OnToolResult"/>. The call used to invoke
/// the tool and return its result with nothing around it: a host that redacts with the hook served the
/// unredacted result to MCP callers, 30 calls went through a limit of 20, and the dashboard never moved.
///
/// <para>
/// Nothing is thrown out of here: the SDK logs any exception from the call handler as a crashed tool and
/// tells the caller only "An error occurred" (BUG-101). A refused or unknown call is a result with
/// <c>isError</c>.
/// </para>
/// </summary>
internal sealed class MentorMcpCallHandler(
    MentorAgentMcpToolCollection tools,
    MentorRateLimiter rateLimiter,
    MentorMetrics metrics,
    IOptions<MentorOptions> options,
    ILogger<MentorMcpCallHandler> logger,
    MentorTelemetry? telemetry = null)
{
    private readonly MentorOptions _options = options.Value;

    internal async ValueTask<CallToolResult> HandleAsync(
        RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var name = context.Params?.Name;

        // Resolved once for the request: the same principal decides the role gate and names the caller.
        // Roles are decided only from McpCallerPrincipal — reading them from the request's user when the
        // host set no resolver would publish role-gated actions to hosts that never opted in.
        var rolePrincipal = tools.ResolveCaller();
        var caller = CallerId(rolePrincipal ?? context.User)
                     ?? RemoteAddress(context.Services);

        // Rate limit first, so every call counts — an unknown name too, which bounds name probing.
        if (_options.RateLimitPerUser > 0)
        {
            var key = "mcp:" + (caller ?? "anonymous");   // no id and no address: one shared allowance
            var (allowed, _) = rateLimiter.CheckAndIncrement(key);
            if (!allowed)
            {
                Audit(name, caller, "rate-limited", started);
                return Error(
                    $"Rate limit reached: at most {_options.RateLimitPerUser} tool calls every " +
                    $"{_options.RateLimitWindowSecs} s for this caller. Retry in " +
                    $"{rateLimiter.GetSecondsUntilReset(key)} s.");
            }
        }

        // A name the server does not list for this caller: a gated action it withholds, one only another
        // role may use, or one that never existed. The same words for all of them, so probing names reveals
        // nothing about gated actions (BUG-101).
        var tool = tools.FindTool(name, rolePrincipal);
        if (tool is null)
        {
            Audit(name, caller, "unknown", started);
            return Error($"Unknown tool: '{name}'. This server does not offer it; tools/list names the tools it does.");
        }

        // Metered only once the tool is known: an unknown name is the caller's choice, and counting it
        // would let anyone grow the metrics without bound.
        metrics.RecordToolCall(tool.ProtocolTool.Name);
        // "mentoragent.tool" is a metric tag dashboards group by, not a namespace: keep it on a rename.
        telemetry?.ToolCalls.Add(1, new KeyValuePair<string, object?>("mentoragent.tool", tool.ProtocolTool.Name));
        using var activity = telemetry?.StartActivity($"mentor.mcp.tool.{tool.ProtocolTool.Name}");

        var outcome = "failed";
        try
        {
            var result = await tool.InvokeAsync(context, cancellationToken);
            outcome = result.IsError == true ? "error" : "ok";
            return result;
        }
        finally
        {
            Audit(tool.ProtocolTool.Name, caller, outcome, started);
        }
    }

    /// <summary>
    /// The caller's stable id for the rate limit — <c>NameIdentifier</c>, else <c>sub</c> or <c>oid</c> (a
    /// JwtBearer principal with <c>MapInboundClaims = false</c> has no <c>NameIdentifier</c>), else the
    /// name — or null when there is none, never a constant shared by every signed-in caller.
    /// </summary>
    private static string? CallerId(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true) return null;

        var id = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
                 ?? principal.FindFirst("sub")?.Value
                 ?? principal.FindFirst("oid")?.Value
                 ?? principal.Identity?.Name;
        return string.IsNullOrEmpty(id) ? null : "user:" + id;
    }

    /// <summary>
    /// A caller with no id is keyed by its remote address when the host exposes it
    /// (<c>IHttpContextAccessor</c>); behind a proxy that is the proxy unless the host applies forwarded
    /// headers. Without one, every such caller shares one allowance: never the MCP session, which costs
    /// nothing to open and would hand out a fresh allowance on every <c>initialize</c>.
    /// </summary>
    private static string? RemoteAddress(IServiceProvider? services)
        => services?.GetService(typeof(IHttpContextAccessor))
               is IHttpContextAccessor { HttpContext.Connection.RemoteIpAddress: { } address }
            ? "ip:" + address
            : null;

    /// <summary>
    /// One Information line per call. The caller is a fingerprint, never the id itself (often an e-mail
    /// address); arguments and results are never logged.
    /// </summary>
    private void Audit(string? tool, string? caller, string outcome, long started)
    {
        if (!logger.IsEnabled(LogLevel.Information)) return;

        // The name is the caller's text: no control character reaches the log (a newline would forge a line).
        var shown = tool is null ? "(none)" : new string([.. tool.Take(64).Select(c => char.IsControl(c) ? '?' : c)])
                                               + (tool.Length > 64 ? "…" : "");
        logger.LogInformation(
            "[MentorAgent:MCP] tools/call '{Tool}' by {Caller}: {Outcome} in {Ms} ms.",
            shown, caller is null ? "anonymous" : "caller#" + Fingerprint(caller), outcome,
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    private static string Fingerprint(string value)
        => Convert.ToHexString(
               System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))[..10];

    private static CallToolResult Error(string text)
        => new() { IsError = true, Content = [new TextContentBlock { Text = text }] };
}
