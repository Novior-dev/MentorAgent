using System.Collections.Concurrent;
using System.Net;
using FluentAssertions;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Novior.MentorAgent.Tests.Server;

/// <summary>
/// The identity bridge: ASP.NET Core's authenticated principal reaching the MentorAgent core
/// through the standard <c>AuthenticationStateProvider</c>, so <c>RequiredRoles</c>, per-user
/// memory and per-user rate limiting work for headless clients and not only inside Blazor.
///
/// <para>
/// This is a security control, so the case that matters is the failing one. A bridge that quietly
/// hands over an empty principal does not throw and does not log — it just makes every role check
/// pass or fail uniformly, and which of those two it is decides whether the bug is "nothing works"
/// or "everything is exposed". The tests below pin the direction: <b>closed</b>.
/// </para>
/// </summary>
public sealed class IdentityBridgeTests
{
    private sealed class Recorder
    {
        internal ConcurrentQueue<string> Chunks { get; } = new();
        internal TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void Attach(HubConnection hub)
        {
            hub.On<string>("StreamingChunk", Chunks.Enqueue);
            hub.On("StreamingCompleted", () => Completed.TrySetResult());
            hub.On<string>("Error", _ => Completed.TrySetResult());
        }
    }

    /// <summary>Runs one turn in which the model calls <paramref name="tool"/>, and returns the log.</summary>
    private static async Task<string> RunToolTurnAsync(ServerAppFactory app, string tool, object? args = null)
    {
        app.Chat.CallTool(tool, args).Say("finished");

        var hub = app.CreateHubConnection();
        var recorder = new Recorder();
        recorder.Attach(hub);
        await hub.StartAsync();

        try
        {
            await hub.InvokeAsync("SendMessage", "do the thing", null);
            await recorder.Completed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally { await hub.DisposeAsync(); }

        // Without the startup notice that lists the writes running without confirmation (story 198): it
        // names export_ledger by design, and these tests look for a refusal of it.
        return string.Join("\n", app.Logs.Records
            .Where(r => r.Level >= LogLevel.Warning && !r.Message.Contains("without confirmation"))
            .Select(r => r.Message));
    }

    // ── Fails closed ────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_anonymous_caller_is_refused_a_role_gated_tool()
    {
        // export_ledger requires the Admin role and nothing else — no confirmation — so this
        // isolates the role gate from every other reason a tool might not run.
        await using var app = await ServerAppFactory.CreateAsync();

        var warnings = await RunToolTurnAsync(app, "export_ledger");

        warnings.Should().Contain("export_ledger").And.Contain("blocked",
            "an unauthenticated caller must not reach a tool marked with RequiredRoles");
    }

    [Fact]
    public async Task An_authenticated_caller_without_the_role_is_still_refused()
    {
        // The other half: being logged in is not the same as being authorised, and conflating the
        // two is the classic way a role gate ends up as an authentication check.
        await using var app = await ServerAppFactory.CreateAsync(
            userId: "ada", roles: ["Support"]);

        var warnings = await RunToolTurnAsync(app, "export_ledger");

        warnings.Should().Contain("export_ledger").And.Contain("blocked");
    }

    [Fact]
    public async Task A_caller_with_the_role_reaches_the_tool()
    {
        await using var app = await ServerAppFactory.CreateAsync(
            userId: "ada", roles: ["Admin"]);

        var warnings = await RunToolTurnAsync(app, "export_ledger");

        warnings.Should().NotContain("export_ledger",
            "the principal has to survive the hop from HubCallerContext into the core");
    }

    [Fact]
    public async Task Holding_one_of_several_accepted_roles_is_enough()
    {
        await using var app = await ServerAppFactory.CreateAsync(
            userId: "ada", roles: ["Support", "Admin", "Billing"]);

        var warnings = await RunToolTurnAsync(app, "export_ledger");

        warnings.Should().NotContain("export_ledger");
    }

    [Fact]
    public async Task An_ungated_tool_runs_for_an_anonymous_caller()
    {
        // The control. Without this, "every tool is blocked" would pass the tests above for
        // entirely the wrong reason.
        await using var app = await ServerAppFactory.CreateAsync();

        var warnings = await RunToolTurnAsync(app, "count_open_orders");

        warnings.Should().NotContain("count_open_orders");
    }

    // ── Gate ordering ───────────────────────────────────────────────────────────

    [Fact]
    public async Task The_role_gate_runs_before_the_confirmation_gate()
    {
        // cancel_order carries both RequiredRoles and RequiresConfirmation. An anonymous caller
        // must be refused outright and never see a confirmation dialog: asking someone to approve
        // an action they are not allowed to perform leaks that the action exists, and a user who
        // presses Confirm and is then refused has been told the widget is broken.
        await using var app = await ServerAppFactory.CreateAsync();
        app.Chat.CallTool("cancel_order", new { orderId = 7, reason = "test" }).Say("finished");

        var hub = app.CreateHubConnection();
        var confirmations = 0;
        var recorder = new Recorder();
        recorder.Attach(hub);
        hub.On<string, string, string>("ConfirmationRequired", (_, _, _) => Interlocked.Increment(ref confirmations));
        await hub.StartAsync();

        try
        {
            // If the confirmation gate ran first this would park and this call would not return,
            // so the timeout is itself part of the assertion.
            await hub.InvokeAsync("SendMessage", "cancel order 7", null)
                     .WaitAsync(TimeSpan.FromSeconds(15));
            await recorder.Completed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally { await hub.DisposeAsync(); }

        confirmations.Should().Be(0, "the role check must refuse before anyone is asked to approve");
        app.Logs.WarningText.Should().Contain("cancel_order").And.Contain("blocked");
    }

    [Fact]
    public async Task With_the_role_held_the_confirmation_gate_is_reached()
    {
        // The same tool, the same turn, one thing changed. Together with the test above this pins
        // the ordering rather than just the outcome.
        await using var app = await ServerAppFactory.CreateAsync(userId: "ada", roles: ["Manager"]);
        app.Chat.CallTool("cancel_order", new { orderId = 7, reason = "test" }).Say("finished");

        var hub = app.CreateHubConnection();
        var seen = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var recorder = new Recorder();
        recorder.Attach(hub);
        hub.On<string, string, string>("ConfirmationRequired", (id, _, _) => seen.TrySetResult(Guid.Parse(id)));
        await hub.StartAsync();

        try
        {
            var turn = hub.InvokeAsync("SendMessage", "cancel order 7", null);
            var actionId = await seen.Task.WaitAsync(TimeSpan.FromSeconds(15));

            await app.Client.PostAsync($"/mentor/approve?actionId={actionId}&approved=true", null);
            await turn.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally { await hub.DisposeAsync(); }
    }

    // ── The same principal over plain HTTP ──────────────────────────────────────

    [Fact]
    public async Task The_metrics_endpoint_refuses_an_anonymous_caller()
    {
        await using var app = await ServerAppFactory.CreateAsync();

        var response = await app.Client.GetAsync("/mentor/admin/metrics");

        response.StatusCode.Should().BeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden],
            "the dashboard is admin-only and must never fall open");
    }

    [Fact]
    public async Task The_http_identity_path_is_used_when_there_is_no_hub_call()
    {
        // SSE and plain HTTP have no HubCallerContext, so the bridge falls back to
        // HttpContext.User. Both paths must reach the same place.
        await using var app = await ServerAppFactory.CreateAsync(
            userId: "ada", roles: ["Admin"]);

        var response = await app.Client.GetAsync("/mentor/admin/metrics");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_empty_dashboard_role_leaves_the_metrics_endpoint_open()
    {
        // Documented as a development-only escape hatch. Pinned so nobody "tidies" it into a
        // default of Admin and breaks local setups — or, worse, the reverse.
        await using var app = await ServerAppFactory.CreateAsync(o => o.DashboardRole = "");

        var response = await app.Client.GetAsync("/mentor/admin/metrics");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_tour_endpoint_stays_open_to_anonymous_callers()
    {
        // Deliberately unauthenticated: the payload is the same public screen names the assistant
        // already states in conversation, identical for every user, with no metrics and no counts.
        await using var app = await ServerAppFactory.CreateAsync();

        var response = await app.Client.GetAsync("/mentor/tour");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
