using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Novior.MentorAgent.Abstractions.Core;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Middleware;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Novior.MentorAgent.Utility;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Novior.MentorAgent.Tests.Gating;

/// <summary>
/// What a failing action tells the chat, the model and the log (BUG-147, BUG-148, BUG-152).
///
/// <para>
/// The gate's catch covered the tool and everything after it. A redaction hook or a screen update that
/// threw reported an action that had already run as failed, and the model ran it again. The chat showed the
/// exception's own text even when the app had set <c>OnException</c> to say something else, and a Stop
/// pressed while the tool ran was logged as a failure. The Warnings named the user and carried the whole
/// exception. What the model gets does not change: the app's message, else the exception's.
/// </para>
///
/// <para>
/// Driven through <see cref="MentorAgentUIMiddleware.InvokeGatedAsync"/> with a tool body written here: the
/// gate is the subject, and the model is not needed to make a tool throw.
/// </para>
/// </summary>
public sealed class ToolFailureTests
{
    /// <summary>The kind of text an exception carries: a host name and a user's e-mail address.</summary>
    private const string Secret = "connection to db-prod-7 refused for mario.rossi@example.com";

    private static ValueTask<object?> Throws(CancellationToken _) => throw new InvalidOperationException(Secret);

    private static async Task<(string Result, List<string> Failed)> RunAsync(
        MentorHostFixture host,
        Func<CancellationToken, ValueTask<object?>> invoke,
        string toolName = "probe_tool",
        CancellationToken ct = default)
    {
        var failed = new List<string>();
        host.Get<IMentorStateService>().OnActionFailed += failed.Add;

        var result = await host.Get<MentorAgentUIMiddleware>().InvokeGatedAsync(toolName, null, invoke, ct);
        return (result?.ToString() ?? "", failed);
    }

    private static string ActionFailed(MentorHostFixture host)
        => new MentorLocalizer(host.Options.Language).Get("action.failed");

    // ── What the chat and the model are told (BUG-147) ─────────────────────────────

    [Fact]
    public async Task With_OnException_set_the_chat_and_the_model_get_the_apps_message()
    {
        await using var host = MentorHostFixture.Create(o => o.OnException = _ => "The warehouse is offline.");

        var (result, failed) = await RunAsync(host, Throws);

        failed.Should().ContainSingle().Which.Should().Be("probe_tool: The warehouse is offline.");
        result.Should().Contain("The warehouse is offline.").And.NotContain(Secret);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task With_OnException_returning_nothing_the_built_in_message_is_shown_never_the_exceptions(string? returned)
    {
        await using var host = MentorHostFixture.Create(o => o.OnException = _ => returned);

        var (result, failed) = await RunAsync(host, Throws);

        failed.Should().ContainSingle().Which.Should().Be($"probe_tool: {ActionFailed(host)}")
            .And.NotContain(Secret, "the app set OnException: the exception's text is not for the chat");
        result.Should().Contain(Secret,
            "the model still needs the reason to answer; a message for it is what the hook returns");
    }

    [Fact]
    public async Task An_OnException_that_throws_falls_back_to_the_built_in_message()
    {
        await using var host = MentorHostFixture.Create(o => o.OnException = _ => throw new FormatException("hook bug"));

        var (result, failed) = await RunAsync(host, Throws);

        failed.Should().ContainSingle().Which.Should().Be($"probe_tool: {ActionFailed(host)}");
        MentorUtils.DidNotRun(result).Should().BeTrue("the hook's bug does not turn a failure into a crash");
        host.Logs.HasWarning("OnException threw (FormatException)").Should().BeTrue(host.Logs.WarningText);
    }

    [Fact]
    public async Task Without_OnException_the_exceptions_message_is_shown_as_before()
    {
        // The control: the hook is how an app chooses what its users see; without it nothing changes.
        await using var host = MentorHostFixture.Create();

        var (result, failed) = await RunAsync(host, Throws);

        failed.Should().ContainSingle().Which.Should().Be($"probe_tool: {Secret}");
        MentorUtils.DidNotRun(result).Should().BeTrue();
        result.Should().Contain("mario.rossi@example.com");
    }

    // ── An action that ran stays run (BUG-147) ─────────────────────────────────────

    [Fact]
    public async Task An_OnToolResult_that_throws_withholds_the_result_and_does_not_report_a_failure()
    {
        await using var host = MentorHostFixture.Create(o =>
            o.OnToolResult = (_, _) => throw new InvalidOperationException("redaction bug"));
        var ran = 0;

        var (result, failed) = await RunAsync(host, _ => { ran++; return ValueTask.FromResult<object?>("IBAN IT60X0542811101000000123456"); });

        ran.Should().Be(1);
        failed.Should().BeEmpty("the action ran; only its result could not be handed on");
        MentorUtils.DidNotRun(result).Should().BeFalse("a result read as a failure gets the action run again");
        result.Should().Contain("\"result_withheld\":true").And.Contain("Do not run it again")
            .And.NotContain("IBAN", "the hook that should have redacted it never saw it");
        host.Logs.HasWarning("OnToolResult threw for 'probe_tool' (InvalidOperationException)").Should().BeTrue(host.Logs.WarningText);
    }

    [Fact]
    public async Task An_OnToolResult_that_throws_uses_the_apps_message_when_there_is_one()
    {
        await using var host = MentorHostFixture.Create(o =>
        {
            o.OnToolResult = (_, _) => throw new InvalidOperationException("redaction bug");
            o.OnException = _ => "Done, but the details are not available.";
        });

        var (result, _) = await RunAsync(host, _ => ValueTask.FromResult<object?>("raw"));

        result.Should().Contain("Done, but the details are not available.").And.NotContain("raw");
    }

    [Fact]
    public async Task A_screen_update_that_throws_after_the_action_does_not_report_a_failure()
    {
        await using var host = MentorHostFixture.Create();
        host.Get<IMentorStateService>().OnActionCompleted += _ => throw new InvalidOperationException("a widget handler broke");

        var (result, failed) = await RunAsync(host, _ => ValueTask.FromResult<object?>("{\"success\":true,\"stock\":12}"));

        failed.Should().BeEmpty();
        result.Should().Be("{\"success\":true,\"stock\":12}", "the model gets what the action returned");
        host.Logs.HasWarning("'probe_tool' ran, but showing its result failed (InvalidOperationException)")
            .Should().BeTrue(host.Logs.WarningText);
    }

    // ── Stop ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Stop_while_the_tool_runs_is_not_a_failure()
    {
        await using var host = MentorHostFixture.Create();
        using var turn = new CancellationTokenSource();

        var (result, failed) = await RunAsync(host, ct =>
        {
            turn.Cancel();                       // the user presses Stop while the action runs
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult<object?>(null);
        }, ct: turn.Token);

        failed.Should().BeEmpty("a Stop is the user's choice, not an error to show");
        result.Should().Contain("\"stopped_by_user\":true");
        host.Logs.Records.Should().NotContain(r => r.Level >= LogLevel.Warning && r.Message.Contains("probe_tool"),
            host.Logs.WarningText);
    }

    [Fact]
    public async Task A_timeout_inside_the_tool_is_still_a_failure()
    {
        // The control for the Stop case: a cancellation the turn did not ask for is the tool's own failure.
        await using var host = MentorHostFixture.Create();

        var (result, failed) = await RunAsync(host, _ => throw new TaskCanceledException("the warehouse API timed out"));

        failed.Should().ContainSingle();
        MentorUtils.DidNotRun(result).Should().BeTrue();
        host.Logs.HasWarning("Tool 'probe_tool' failed (TaskCanceledException)").Should().BeTrue(host.Logs.WarningText);
    }

    // ── The log (BUG-148) ──────────────────────────────────────────────────────────

    [Fact]
    public async Task A_failed_tool_is_logged_with_its_type_and_not_its_message()
    {
        await using var host = MentorHostFixture.Create();

        await RunAsync(host, Throws);

        host.Logs.HasWarning("Tool 'probe_tool' failed (InvalidOperationException)").Should().BeTrue(host.Logs.WarningText);
        host.Logs.Records.Where(r => r.Level >= LogLevel.Information)
            .Should().NotContain(r => r.Message.Contains("mario.rossi"), "the default levels carry no exception text");
        host.Logs.ExceptionsByLevel.Where(e => e.Level >= LogLevel.Information)
            .Should().BeEmpty("a sink prints an attached exception whole, message included");
        host.Logs.ExceptionsByLevel.Should().Contain(e => e.Level == LogLevel.Debug && e.Exception.Message == Secret,
            "the whole exception is still there for whoever turns Debug on");
    }

    [Fact]
    public async Task A_role_refusal_names_a_fingerprint_and_not_the_user()
    {
        await using var host = MentorHostFixture.Create();
        host.Auth.SignIn("mario.rossi@example.com", "Viewer");

        var (result, _) = await RunAsync(host, Throws, toolName: "cancel_order");   // RequiredRoles = Manager

        result.Should().Contain("Manager");
        var expected = "caller#" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes("user:mario.rossi@example.com")))[..10];
        host.Logs.HasWarning($"{expected} not authorized for tool 'cancel_order'").Should().BeTrue(
            "the same fingerprint as the MCP server's audit line for this user. " + host.Logs.WarningText);
        host.Logs.Records.Should().NotContain(r => r.Message.Contains("mario.rossi"), "at any level");
    }

    // ── The log outside the gate (BUG-152) ─────────────────────────────────────────
    //
    // Four places that run host code for a tool call without going through the gate still wrote the whole
    // exception at Warning: a UI action on either path, and OnToolResult and McpCallerPrincipal on the MCP
    // server.

    private static void OnlyDebugCarriesTheException(TestLogCollector logs)
    {
        logs.Records.Where(r => r.Level >= LogLevel.Information)
            .Should().NotContain(r => r.Message.Contains("mario.rossi"), "the default levels carry no exception text");
        logs.ExceptionsByLevel.Where(e => e.Level >= LogLevel.Information)
            .Should().BeEmpty("a sink prints an attached exception whole, message included");
        logs.ExceptionsByLevel.Should().Contain(e => e.Level == LogLevel.Debug && e.Exception.Message == Secret,
            "the whole exception is still there for whoever turns Debug on");
    }

    [Fact]
    public async Task A_UI_action_that_throws_is_logged_with_its_type_and_not_its_message()
    {
        // Path A: UIActionsMiddleware runs the page's actions as tools of their own.
        await using var host = MentorHostFixture.Create();
        host.Get<IMentorPageContext>().RegisterUIAction("highlight_row", "Highlights a row",
            _ => throw new InvalidOperationException(Secret));
        host.Chat!.CallTool("highlight_row").Say("I could not highlight it.");

        await host.RunTurnAsync("highlight row 5");

        host.Logs.HasWarning("UI action 'highlight_row' threw an exception (InvalidOperationException)")
            .Should().BeTrue(host.Logs.WarningText);
        OnlyDebugCarriesTheException(host.Logs);
    }

    [Fact]
    public async Task A_UI_action_that_throws_under_a_pre_built_agent_is_logged_with_its_type_and_not_its_message()
    {
        // Path B: a pre-built agent reaches the page's actions through the invoke_ui_action dispatcher.
        var inner = new FakeChatClient();
        await using var host = MentorHostFixture.Create(
            o =>
            {
                o.ChatClient = null;
                o.Agent = inner.AsAIAgent(instructions: "You are a test agent.");
            },
            withChatClient: false);
        host.Get<IMentorPageContext>().RegisterUIAction("highlight_row", "Highlights a row",
            _ => throw new InvalidOperationException(Secret));
        inner.CallTool("invoke_ui_action", new { actionName = "highlight_row" }).Say("I could not highlight it.");

        await host.RunTurnAsync("highlight row 5");

        host.Logs.HasWarning("UI action 'highlight_row' failed (InvalidOperationException)")
            .Should().BeTrue(host.Logs.WarningText);
        OnlyDebugCarriesTheException(host.Logs);
    }

    [Fact]
    public async Task An_OnToolResult_that_throws_on_the_MCP_server_is_logged_with_its_type_and_not_its_message()
    {
        await using var app = await ServerAppFactory.CreateAsync(
            configure: o =>
            {
                o.McpServerEnabled = true;
                o.OnToolResult = (_, _) => throw new InvalidOperationException(Secret);
            },
            mapEndpoints: e => e.MapMentorAgentMcp());
        var mcp = await McpTestSession.OpenAsync(app.Client);

        var (_, isError) = await mcp.CallAsync("count_open_orders");

        isError.Should().BeTrue("the result the hook could not redact is withheld");
        app.Logs.HasWarning("OnToolResult threw for 'count_open_orders' (InvalidOperationException)")
            .Should().BeTrue(app.Logs.WarningText);
        OnlyDebugCarriesTheException(app.Logs);
    }

    [Fact]
    public async Task An_McpCallerPrincipal_that_throws_is_logged_with_its_type_and_not_its_message()
    {
        // The resolver reads the caller's identity from the host's own auth stack, so its message can name them.
        await using var app = await ServerAppFactory.CreateAsync(
            configure: o =>
            {
                o.McpServerEnabled = true;
                o.McpCallerPrincipal = () => throw new InvalidOperationException(Secret);
            },
            mapEndpoints: e => e.MapMentorAgentMcp());
        var mcp = await McpTestSession.OpenAsync(app.Client);

        (await mcp.ListToolNamesAsync()).Should().Contain("count_open_orders", "ungated tools survive a broken resolver");

        app.Logs.HasWarning("McpCallerPrincipal threw (InvalidOperationException)").Should().BeTrue(app.Logs.WarningText);
        OnlyDebugCarriesTheException(app.Logs);
    }
}
