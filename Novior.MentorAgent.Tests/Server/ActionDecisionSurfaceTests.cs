using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Novior.MentorAgent.Tests.Server;

/// <summary>
/// Story 180 over the real transports: the hub, <c>/mentor/chat</c> and the MCP server. A refusal for a missing
/// role reaches the observers with the user, the surface and the reason, and the MCP server's audit line no
/// longer calls it "unknown" — while the MCP caller is still told the same for every name it may not use
/// (BUG-101).
/// </summary>
public sealed class ActionDecisionSurfaceTests
{
    private static Task FlushAsync(ServerAppFactory app) => app.Services.GetRequiredService<MentorActionDecisions>().FlushAsync();

    private static ChatResponse ToolCall(ServerAppFactory app, string callId, string tool, Dictionary<string, object?>? args = null)
        => new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(callId, tool, args)])) { ModelId = app.Chat.ModelId };

    private static async Task RunHubTurnAsync(ServerAppFactory app, string message)
    {
        var hub = app.CreateHubConnection();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On("StreamingCompleted", () => completed.TrySetResult());
        hub.On<string>("Error", _ => completed.TrySetResult());
        await hub.StartAsync();
        try
        {
            await hub.InvokeAsync("SendMessage", message, null);
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally { await hub.DisposeAsync(); }
    }

    // ── The hub ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_role_denial_on_the_hub_reaches_the_observer_with_the_user_the_surface_and_the_reason()
    {
        var observer = new RecordingActionObserver();
        await using var app = await ServerAppFactory.CreateAsync(
            userId: "eve", roles: ["Support"], configureServices: s => s.AddMentorActionObserver(observer));
        app.Chat.Respond((_, _) => ToolCall(app, "call-h1", "export_ledger")).Say("I cannot.");

        await RunHubTurnAsync(app, "export the ledger");
        await FlushAsync(app);

        observer.Steps("export_ledger").Should().Equal("Denied/role-missing");
        var denied = observer.Seen.Single();
        denied.Surface.Should().Be(MentorActionSurface.Hub);
        denied.User!.Identity!.Name.Should().Be("eve");
        denied.RequiredRoles.Should().Equal("Admin");
        denied.CallId.Should().Be("call-h1");
        denied.TurnId.Should().NotBeNullOrEmpty();
        denied.SessionId.Should().NotBeNullOrEmpty();
    }

    // ── /mentor/chat and /mentor/approve ─────────────────────────────────────────

    [Fact]
    public async Task The_decision_names_who_confirmed_over_http_and_when()
    {
        // ada's turn on /mentor/chat, confirmed by bob: no hub connection raised it, so nothing ties the answer to
        // the turn's owner, and the decision has to say who actually gave it.
        var observer = new RecordingActionObserver();
        var clock = new TestClock(new DateTimeOffset(2026, 10, 21, 9, 30, 0, TimeSpan.Zero));
        await using var app = await ServerAppFactory.CreateAsync(
            userId: "ada",
            configureServices: s =>
            {
                s.AddMentorActionObserver(observer);
                s.AddSingleton<TimeProvider>(clock);
            });
        app.Chat.CallTool("issue_refund", new { orderId = 7 }).Say("Refund issued.");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var response = await app.Client.GetAsync(
            "/mentor/chat?message=" + Uri.EscapeDataString("refund order 7"), HttpCompletionOption.ResponseHeadersRead, cts.Token);
        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        var received = new StringBuilder();
        var buffer = new byte[4096];
        var approved = false;
        try
        {
            while (await stream.ReadAsync(buffer, cts.Token) is var read and > 0)
            {
                received.Append(Encoding.UTF8.GetString(buffer, 0, read));
                var text = received.ToString();
                if (!approved && text.Contains("\"type\":\"confirmationRequired\""))
                {
                    clock.Now = clock.Now.AddMinutes(2);   // bob answers two minutes later
                    using var approve = new HttpRequestMessage(HttpMethod.Post,
                        $"/mentor/approve?actionId={ActionIdOf(text)}&approved=true");
                    approve.Headers.Add(ServerAppFactory.AsUserHeader, "bob");
                    (await app.Client.SendAsync(approve, cts.Token)).IsSuccessStatusCode.Should().BeTrue();
                    approved = true;
                }
                if (text.Contains("\"type\":\"completed\"")) break;
            }
        }
        catch (OperationCanceledException) { /* deadline — assert on what arrived */ }
        await FlushAsync(app);

        observer.Steps("issue_refund").Should().Equal(
            "Allowed/allowed", "ConfirmationRequested/operator", "Confirmed/operator", "Executed/completed");
        observer.Seen.Should().AllSatisfy(d =>
        {
            d.Surface.Should().Be(MentorActionSurface.Sse);
            d.User!.Identity!.Name.Should().Be("ada");
        });
        var confirmed = observer.Seen.Single(d => d.Outcome == MentorActionOutcome.Confirmed);
        confirmed.ConfirmedBy!.Identity!.Name.Should().Be("bob", "the caller of /mentor/approve, not the turn's user");
        confirmed.Timestamp.Should().Be(new DateTimeOffset(2026, 10, 21, 9, 32, 0, TimeSpan.Zero), "when bob answered");
    }

    private static string ActionIdOf(string body)
    {
        var line = body.Split('\n').First(l => l.Contains("\"type\":\"confirmationRequired\""));
        return System.Text.Json.JsonDocument.Parse(line["data: ".Length..]).RootElement.GetProperty("actionId").GetString()!;
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    // ── The MCP server ──────────────────────────────────────────────────────────

    private static Task<ServerAppFactory> McpAsync(RecordingActionObserver observer, Action<MentorOptions>? configure = null,
        Action<IServiceCollection>? services = null)
        => ServerAppFactory.CreateAsync(
        userId: "ada",
        roles: ["Admin"],
        roleClaimType: "roles",
        configure: o =>
        {
            o.McpServerEnabled = true;
            configure?.Invoke(o);
        },
        configureServices: s =>
        {
            s.AddMentorActionObserver(observer);
            s.AddHttpContextAccessor();
            s.AddOptions<MentorOptions>().Configure<IHttpContextAccessor>(
                (o, http) => o.McpCallerPrincipal = () => http.HttpContext?.User);
            services?.Invoke(s);
        },
        mapEndpoints: e => e.MapMentorAgentMcp());

    private static IReadOnlyList<string> AuditLines(ServerAppFactory app, string tool) =>
        [.. app.Logs.Records
            .Where(r => r.Level == LogLevel.Information && r.Message.Contains($"tools/call '{tool}'"))
            .Select(r => r.Message)];

    [Fact]
    public async Task A_role_denial_on_the_mcp_server_reaches_the_observer_and_is_no_longer_logged_as_unknown()
    {
        var observer = new RecordingActionObserver();
        await using var app = await McpAsync(observer);
        var eve = await McpTestSession.OpenAsync(app.Client, asUser: "eve");   // signed in, no role

        var (text, isError) = await eve.CallAsync("export_ledger");
        await FlushAsync(app);

        isError.Should().BeTrue();
        text.Should().Contain("Unknown tool: 'export_ledger'", "the caller is told the same for every name it may not use");
        AuditLines(app, "export_ledger").Should().ContainSingle().Which.Should().Contain(": denied-role in");

        observer.Steps("export_ledger").Should().Equal("Denied/role-missing");
        var denied = observer.Seen.Single();
        denied.Surface.Should().Be(MentorActionSurface.Mcp);
        denied.AgentKind.Should().BeNull("an MCP client names the tool itself");
        denied.User!.Identity!.Name.Should().Be("eve");
        denied.RequiredRoles.Should().Equal("Admin");
        denied.CallId.Should().Be("2", "the JSON-RPC id of eve's tools/call: initialize was 1");
        denied.TurnId.Should().BeNull();
        denied.SessionId.Should().Be("mcp#" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(eve.SessionId!)))[..10])
            .And.NotContain(eve.SessionId!, "the session id resumes the session: an audit trail must not hand it out");
    }

    [Fact]
    public async Task A_withheld_action_and_an_unknown_name_are_told_apart_in_the_log_only()
    {
        var observer = new RecordingActionObserver();
        await using var app = await McpAsync(observer);
        var ada = await McpTestSession.OpenAsync(app.Client);

        var withheld = await ada.CallAsync("issue_refund", new { orderId = 7 });   // asks for confirmation
        var unknown = await ada.CallAsync("no_such_tool");
        await FlushAsync(app);

        withheld.Text.Should().Contain("Unknown tool: 'issue_refund'");
        unknown.Text.Should().Contain("Unknown tool: 'no_such_tool'");
        AuditLines(app, "issue_refund").Should().ContainSingle().Which.Should().Contain(": withheld in");
        AuditLines(app, "no_such_tool").Should().ContainSingle().Which.Should().Contain(": unknown in");
        observer.Steps("issue_refund").Should().Equal("Denied/withheld");
        observer.Seen.Should().NotContain(d => d.ToolName == "no_such_tool", "a name nobody defined is the caller's text, not a decision");
    }

    [Fact]
    public async Task A_call_over_the_rate_limit_is_refused_with_rate_limited()
    {
        var observer = new RecordingActionObserver();
        await using var app = await McpAsync(observer, o => o.RateLimitPerUser = 1);
        var ada = await McpTestSession.OpenAsync(app.Client);

        await ada.CallAsync("export_ledger");
        (await ada.CallAsync("export_ledger")).IsError.Should().BeTrue();
        await FlushAsync(app);

        observer.Steps("export_ledger").Should().Equal("Allowed/allowed", "Executed/completed", "Denied/rate-limited");
    }

    [Theory]
    [InlineData("failing_action", "Failed/exception", ": failed in")]
    [InlineData("count_open_orders", "Executed/result-withheld", ": error in")]
    public async Task On_the_mcp_server_a_failure_reads_as_on_the_chat_path(string tool, string last, string audit)
    {
        // An action that throws, and one whose OnToolResult hook throws, so it ran and its result is withheld. The
        // caller gets an error for both; the decisions say what happened, as on the chat path.
        var observer = new RecordingActionObserver();
        await using var app = await McpAsync(observer, o =>
        {
            if (tool == "count_open_orders") o.OnToolResult = (_, _) => throw new InvalidOperationException("hook down");
        });
        var ada = await McpTestSession.OpenAsync(app.Client);

        (await ada.CallAsync(tool)).IsError.Should().BeTrue();
        await FlushAsync(app);

        observer.Steps(tool).Should().Equal("Allowed/allowed", last);
        AuditLines(app, tool).Should().ContainSingle().Which.Should().Contain(audit);
    }

    [Fact]
    public async Task A_call_the_mcp_server_serves_is_allowed_then_executed()
    {
        var observer = new RecordingActionObserver();
        await using var app = await McpAsync(observer);
        var ada = await McpTestSession.OpenAsync(app.Client);

        (await ada.CallAsync("export_ledger")).IsError.Should().BeFalse();
        await FlushAsync(app);

        observer.Steps("export_ledger").Should().Equal("Allowed/allowed", "Executed/completed");
        observer.Seen.Should().AllSatisfy(d => d.User!.Identity!.Name.Should().Be("ada"));
        AuditLines(app, "export_ledger").Should().ContainSingle().Which.Should().Contain(": ok in");
    }
}
