using FluentAssertions;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Novior.MentorAgent.Tests.Federation;

/// <summary>
/// BUG-107 — an MCP <c>tools/call</c> skipped <c>OnToolResult</c>, <c>RateLimitPerUser</c> and the tool
/// metrics.
///
/// <para>
/// The call handler invoked the tool and returned its result with nothing around it. A host that redacts
/// results with <c>OnToolResult</c> — documented as running "for every tool invocation" — served the
/// unredacted result to MCP callers; the per-user limit let 30 calls through with a limit of 20; the
/// dashboard's top actions did not move. Measured live on the ApiServer of the published rc.13.
/// </para>
/// </summary>
public sealed class McpCallGatesTests
{
    private static Task<ServerAppFactory> CreateAsync(Action<MentorOptions> configure, string? userId = "u1")
        => ServerAppFactory.CreateAsync(
            userId: userId,
            configure: o =>
            {
                o.McpServerEnabled = true;
                configure(o);
            },
            mapEndpoints: e => e.MapMentorAgentMcp());

    // ── OnToolResult ────────────────────────────────────────────────────────────

    [Fact]
    public async Task OnToolResult_runs_for_an_mcp_tools_call()
    {
        await using var app = await CreateAsync(o =>
            o.OnToolResult = (name, result) => name == "count_open_orders" ? "REDACTED-BY-HOOK" : result);
        var mcp = await McpTestSession.OpenAsync(app.Client);

        var (text, isError) = await mcp.CallAsync("count_open_orders");

        isError.Should().BeFalse();
        text.Should().Contain("REDACTED-BY-HOOK", "the hook that redacts a result for the model redacts it for an MCP caller too");
    }

    [Fact]
    public async Task OnToolResult_sees_the_same_result_shape_as_on_the_chat_path()
    {
        // A redaction hook written against what it receives in a chat turn must match on MCP as well.
        Type? overMcp = null, overChat = null;

        await using (var app = await CreateAsync(o => o.OnToolResult = (_, r) => { overMcp = r?.GetType(); return r; }))
        {
            var mcp = await McpTestSession.OpenAsync(app.Client);
            await mcp.CallAsync("count_open_orders");
        }

        await using (var host = MentorHostFixture.Create(o =>
        {
            o.MemoryAutoCapture = false;
            o.OnToolResult = (_, r) => { overChat = r?.GetType(); return r; };
        }))
        {
            host.Chat!.CallTool("count_open_orders").Say("ok");
            await host.RunTurnAsync("quanti ordini aperti?");
        }

        overChat.Should().NotBeNull("the chat path ran the hook");
        overMcp.Should().Be(overChat);
    }

    [Fact]
    public async Task A_hook_that_throws_never_releases_the_unredacted_result()
    {
        await using var app = await CreateAsync(o =>
            o.OnToolResult = (name, result) => name == "mark_order_reviewed"
                ? throw new InvalidOperationException("redaction store down")
                : result);
        var mcp = await McpTestSession.OpenAsync(app.Client);

        var (text, isError) = await mcp.CallAsync("mark_order_reviewed", new { orderId = 4471 });

        text.Should().NotContain("marked as reviewed", "a result the hook could not redact must not leave the server");
        isError.Should().BeTrue("the caller must be able to tell that the call did not return its result");
        app.Logs.Records.Where(r => r.Level >= LogLevel.Error).Should().BeEmpty(
            "the tool ran; a failed redaction is reported to the caller, not logged as a crashed tool");
    }

    // ── RateLimitPerUser ────────────────────────────────────────────────────────

    [Fact]
    public async Task RateLimitPerUser_bounds_mcp_calls_per_caller()
    {
        await using var app = await CreateAsync(o => o.RateLimitPerUser = 2);
        var mcp = await McpTestSession.OpenAsync(app.Client);

        (await mcp.CallAsync("count_open_orders")).IsError.Should().BeFalse();
        (await mcp.CallAsync("count_open_orders")).IsError.Should().BeFalse();
        var (text, isError) = await mcp.CallAsync("count_open_orders");

        isError.Should().BeTrue();
        text.Should().Contain("Rate limit");
        app.Logs.Records.Where(r => r.Level >= LogLevel.Error).Should().BeEmpty(
            "a refused call is an answer, not a crash (BUG-101's lesson: never throw out of the call handler)");
    }

    [Fact]
    public async Task Another_caller_keeps_their_own_allowance()
    {
        await using var app = await CreateAsync(o => o.RateLimitPerUser = 1);
        var u1 = await McpTestSession.OpenAsync(app.Client);
        await u1.CallAsync("count_open_orders");
        (await u1.CallAsync("count_open_orders")).IsError.Should().BeTrue();

        var u2 = await McpTestSession.OpenAsync(app.Client, asUser: "u2");

        (await u2.CallAsync("count_open_orders")).IsError.Should().BeFalse();
    }

    [Fact]
    public async Task Anonymous_callers_cannot_reset_their_allowance_by_opening_a_new_session()
    {
        // An MCP session costs nothing to mint. Keyed on the session, every initialize would have been a
        // fresh allowance; anonymous callers share one.
        await using var app = await CreateAsync(o => o.RateLimitPerUser = 2, userId: null);

        var first = await McpTestSession.OpenAsync(app.Client);
        await first.CallAsync("count_open_orders");
        await first.CallAsync("count_open_orders");

        var second = await McpTestSession.OpenAsync(app.Client);

        (await second.CallAsync("count_open_orders")).IsError.Should().BeTrue();
    }

    [Fact]
    public async Task Mcp_calls_do_not_use_up_the_chat_allowance()
    {
        // A tool call is not a model turn: an automation must not lock its own user out of the widget.
        await using var app = await CreateAsync(o => o.RateLimitPerUser = 2);
        var mcp = await McpTestSession.OpenAsync(app.Client);
        await mcp.CallAsync("count_open_orders");
        await mcp.CallAsync("count_open_orders");

        app.Services.GetRequiredService<MentorRateLimiter>().CheckAndIncrement("u1").Allowed.Should().BeTrue();
    }

    // ── Metrics and audit ───────────────────────────────────────────────────────

    [Fact]
    public async Task Mcp_calls_are_counted_in_the_dashboard()
    {
        await using var app = await CreateAsync(_ => { });
        var mcp = await McpTestSession.OpenAsync(app.Client);

        await mcp.CallAsync("count_open_orders");
        await mcp.CallAsync("count_open_orders");

        app.Services.GetRequiredService<MentorMetrics>().GetSnapshot().TopActions
            .Should().ContainEquivalentOf(new MentorActionCount("count_open_orders", 2));
    }

    [Fact]
    public async Task An_unknown_name_is_not_metered()
    {
        // The name is the caller's choice: counting it would let anyone grow the metrics without bound.
        await using var app = await CreateAsync(_ => { });
        var mcp = await McpTestSession.OpenAsync(app.Client);

        await mcp.CallAsync("no_such_tool");
        await mcp.CallAsync("cancel_order");

        app.Services.GetRequiredService<MentorMetrics>().GetSnapshot().TopActions
            .Select(a => a.Action).Should().NotContain(["no_such_tool", "cancel_order"]);
    }

    [Fact]
    public async Task Every_call_leaves_one_audit_line_without_the_callers_identity_in_clear()
    {
        await using var app = await CreateAsync(_ => { });
        var mcp = await McpTestSession.OpenAsync(app.Client);

        await mcp.CallAsync("count_open_orders");

        var lines = app.Logs.Records
            .Where(r => r.Level == LogLevel.Information && r.Message.Contains("tools/call 'count_open_orders'"))
            .ToList();
        lines.Should().ContainSingle();
        lines[0].Message.Should().Contain("ok");
        lines[0].Message.Should().NotContain("u1", "a user id or e-mail in every line is personal data in the log");
    }

    [Fact]
    public async Task Signed_in_callers_without_a_NameIdentifier_keep_their_own_allowances()
    {
        // Review of the rc.14 fix: with JwtBearer and MapInboundClaims = false there is no NameIdentifier and
        // often no Name, and every such caller was keyed "authenticated" — one automation locked out all.
        await using var app = await ServerAppFactory.CreateAsync(
            userId: "u1",
            configure: o => { o.McpServerEnabled = true; o.RateLimitPerUser = 1; },
            configureServices: s =>
            {
                s.AddHttpContextAccessor();
                s.AddOptions<MentorOptions>().Configure<Microsoft.AspNetCore.Http.IHttpContextAccessor>((o, http) =>
                    o.McpCallerPrincipal = () => http.HttpContext?.User is { Identity.IsAuthenticated: true } user
                        ? new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                              [new System.Security.Claims.Claim("sub", user.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value)],
                              "Bearer"))
                        : null);
            },
            mapEndpoints: e => e.MapMentorAgentMcp());

        var u1 = await McpTestSession.OpenAsync(app.Client);
        await u1.CallAsync("count_open_orders");
        (await u1.CallAsync("count_open_orders")).IsError.Should().BeTrue();

        var u2 = await McpTestSession.OpenAsync(app.Client, asUser: "u2");
        (await u2.CallAsync("count_open_orders")).IsError.Should().BeFalse("u2's 'sub' is not u1's");
    }

    [Fact]
    public async Task A_tool_name_cannot_forge_a_line_in_the_audit_log()
    {
        await using var app = await CreateAsync(_ => { });
        var mcp = await McpTestSession.OpenAsync(app.Client);

        await mcp.CallAsync("x\n[MentorAgent:MCP] tools/call 'export_ledger' by anonymous: ok");

        app.Logs.Records.Where(r => r.Message.Contains("tools/call 'x"))
            .Should().ContainSingle().Which.Message.Should().NotContain("\n");
    }
}
