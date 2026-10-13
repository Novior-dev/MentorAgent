using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Novior.MentorAgent.Tests.Server;

/// <summary>
/// Human-in-the-loop approval across the SignalR/HTTP boundary — the one server behaviour that
/// cannot be tested any other way, and the one most likely to break silently.
///
/// <para>
/// ASP.NET Core SignalR dispatches at most one hub invocation at a time per connection. A turn that
/// is parked inside <c>WaitForConfirmationAsync</c> is still inside <c>SendMessage</c>, so a
/// <c>RespondToApproval</c> hub call would sit in the queue behind it and only run once the turn it
/// was meant to unblock had already timed out. That is why approval travels over HTTP, and why the
/// approval store is a <b>singleton</b> — the HTTP request gets its own DI scope, and a scoped store
/// would leave the waiting turn parked forever while the endpoint cheerfully returned 200.
/// </para>
///
/// <para>
/// Both halves of that are asserted here by actually parking a turn and releasing it from outside.
/// </para>
/// </summary>
public sealed class HitlOverHttpTests
{
    private sealed record Pending(Guid ActionId, string ToolName, string Message);

    private sealed class TurnRecorder
    {
        internal ConcurrentQueue<Pending> Confirmations { get; } = new();
        internal ConcurrentQueue<string> Chunks { get; } = new();
        internal TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ConfirmationSeen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void Attach(HubConnection hub)
        {
            // The action id crosses the wire as a string, not a Guid — that is the documented
            // payload, and a client author subscribing with the wrong arity gets silence.
            hub.On<string, string, string>("ConfirmationRequired", (id, tool, message) =>
            {
                Confirmations.Enqueue(new Pending(Guid.Parse(id), tool, message));
                ConfirmationSeen.TrySetResult();
            });
            hub.On<string>("StreamingChunk", Chunks.Enqueue);
            hub.On("StreamingCompleted", () => Completed.TrySetResult());
            hub.On<string>("Error", _ => Completed.TrySetResult());
        }
    }

    /// <summary>
    /// Awaits one stage of the HITL handshake and, on timeout, says <b>which</b> stage gave up and
    /// what the turn had actually observed by then.
    ///
    /// <para>
    /// These tests park a real turn inside a real hub invocation and release it from an HTTP request,
    /// so every wait here is genuinely time-bounded — and under full-suite parallel load one of them
    /// occasionally loses the race. A bare <c>TimeoutException</c> from <c>WaitAsync</c> names
    /// nothing: the three stages fail identically, and "the turn never completed" is a very different
    /// finding from "the confirmation never arrived". The standing rule for this campaign is that an
    /// unexplained flake gets a better failure message, not a retry, so the message carries the
    /// stage, the confirmations seen, the number of chunks received and whether completion fired.
    /// </para>
    /// </summary>
    private static async Task StageAsync(Task work, string stage, TurnRecorder turn,
                                         int seconds = 15)
    {
        try
        {
            await work.WaitAsync(TimeSpan.FromSeconds(seconds));
        }
        catch (TimeoutException)
        {
            var seen = turn.Confirmations.Select(c => c.ToolName).ToArray();
            throw new TimeoutException(
                $"HITL handshake stalled at '{stage}' after {seconds}s. " +
                $"Confirmations seen: {(seen.Length == 0 ? "(none)" : string.Join(", ", seen))}. " +
                $"Chunks received: {turn.Chunks.Count}. " +
                $"Completion fired: {turn.Completed.Task.IsCompleted}. " +
                $"Chunk text so far: \"{string.Concat(turn.Chunks)}\".");
        }
    }

    /// <summary>Scripts a turn that calls the confirmation-gated tool, then answers in prose.</summary>
    private static ServerAppFactory Script(ServerAppFactory app)
    {
        app.Chat
            .CallTool("issue_refund", new { orderId = 1001 })
            .Say("Refund for order 1001 has been issued.");
        return app;
    }

    private static async Task<(HubConnection Hub, TurnRecorder Turn)> ConnectAsync(ServerAppFactory app)
    {
        var hub = app.CreateHubConnection();
        var turn = new TurnRecorder();
        turn.Attach(hub);
        await hub.StartAsync();
        return (hub, turn);
    }

    // ── The approval actually unblocks a waiting turn ───────────────────────────

    [Fact]
    public async Task Approving_over_http_releases_a_turn_parked_on_the_hub()
    {
        await using var app = Script(await ServerAppFactory.CreateAsync());
        var (hub, turn) = await ConnectAsync(app);

        try
        {
            // Deliberately not awaited: SendMessage does not return until the turn finishes, and
            // the turn cannot finish until the approval arrives. Awaiting here is the deadlock the
            // HTTP endpoint exists to avoid, so the test has to reproduce the same shape a real
            // client uses.
            var turnTask = hub.InvokeAsync("SendMessage", "refund order 1001", null);

            await StageAsync(turn.ConfirmationSeen.Task, "confirmation raised", turn);
            var pending = turn.Confirmations.Single();
            pending.ToolName.Should().Be("issue_refund");

            var response = await app.Client.PostAsync(
                $"/mentor/approve?actionId={pending.ActionId}&approved=true", null);
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            await StageAsync(turnTask, "SendMessage returned", turn);
            await StageAsync(turn.Completed.Task, "StreamingCompleted raised", turn);

            // The hub sends each chunk fire-and-forget, so under load the last one can land after the
            // completion event (failed once in a loaded full-suite run, rc.14): wait for it briefly.
            for (var i = 0; i < 20 && !string.Concat(turn.Chunks).Contains("issued"); i++)
                await Task.Delay(100);

            string.Concat(turn.Chunks).Should().Contain("issued",
                "the turn must run to completion once approval arrives");
        }
        finally { await hub.DisposeAsync(); }
    }

    [Fact]
    public async Task Denying_over_http_also_releases_the_turn()
    {
        // A denial has to unblock just as reliably as an approval. If only the approve path works,
        // pressing Cancel leaves the widget spinning until the request times out.
        await using var app = Script(await ServerAppFactory.CreateAsync());
        var (hub, turn) = await ConnectAsync(app);

        try
        {
            var turnTask = hub.InvokeAsync("SendMessage", "refund order 1001", null);
            await StageAsync(turn.ConfirmationSeen.Task, "confirmation raised", turn);

            var pending = turn.Confirmations.Single();
            var response = await app.Client.PostAsync(
                $"/mentor/approve?actionId={pending.ActionId}&approved=false", null);
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            await StageAsync(turnTask, "SendMessage returned", turn);
            await StageAsync(turn.Completed.Task, "StreamingCompleted raised", turn);
        }
        finally { await hub.DisposeAsync(); }
    }

    [Fact]
    public async Task The_confirmation_names_the_tool_and_shows_the_arguments_the_model_chose()
    {
        // The whole point of the dialog: the user is approving a specific action with specific
        // values, so a generic "the assistant wants to do something" is not consent.
        await using var app = Script(await ServerAppFactory.CreateAsync());
        var (hub, turn) = await ConnectAsync(app);

        try
        {
            var turnTask = hub.InvokeAsync("SendMessage", "refund order 1001", null);
            await StageAsync(turn.ConfirmationSeen.Task, "confirmation raised", turn);

            var pending = turn.Confirmations.Single();
            pending.Message.Should().Contain("1001",
                "the concrete argument is what the user is being asked to verify");

            await app.Client.PostAsync($"/mentor/approve?actionId={pending.ActionId}&approved=true", null);
            await StageAsync(turnTask, "SendMessage returned", turn);
        }
        finally { await hub.DisposeAsync(); }
    }

    [Fact]
    public async Task The_approval_store_is_a_singleton_so_it_spans_the_two_dispatch_scopes()
    {
        // The structural reason the test above can pass. The hub invocation and the HTTP request
        // each get their own DI scope; a scoped store would let the endpoint resolve a fresh,
        // empty one, return 200, and leave the turn parked forever.
        await using var app = await ServerAppFactory.CreateAsync();

        using var first = app.Services.CreateScope();
        using var second = app.Services.CreateScope();

        first.ServiceProvider.GetRequiredService<MentorApprovalStore>()
            .Should().BeSameAs(second.ServiceProvider.GetRequiredService<MentorApprovalStore>());
    }

    // ── The endpoint's own edge cases ───────────────────────────────────────────

    [Fact]
    public async Task Approving_an_unknown_action_id_is_a_404_rather_than_an_error()
    {
        // Reachable whenever a user double-taps Confirm, or approves after the turn was cancelled.
        // A 500 here would surface in the client as a failed approval for something already done;
        // a 200 (up to rc.14) told the client an approval ran when it reached nothing (BUG-128).
        await using var app = await ServerAppFactory.CreateAsync();

        var response = await app.Client.PostAsync(
            $"/mentor/approve?actionId={Guid.NewGuid()}&approved=true", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Answering_the_same_approval_twice_does_not_release_anything_twice()
    {
        await using var app = Script(await ServerAppFactory.CreateAsync());
        var (hub, turn) = await ConnectAsync(app);

        try
        {
            var turnTask = hub.InvokeAsync("SendMessage", "refund order 1001", null);
            await StageAsync(turn.ConfirmationSeen.Task, "confirmation raised", turn);
            var pending = turn.Confirmations.Single();

            await app.Client.PostAsync($"/mentor/approve?actionId={pending.ActionId}&approved=true", null);
            await StageAsync(turnTask, "SendMessage returned", turn);

            var second = await app.Client.PostAsync(
                $"/mentor/approve?actionId={pending.ActionId}&approved=false", null);

            second.StatusCode.Should().Be(HttpStatusCode.NotFound,
                "a late second answer is a user double-tap: not a server fault, and not an answer anything got");
            app.Logs.WarningText.Should().Contain("request not found",
                "the second answer must be recognised as resolving nothing");
        }
        finally { await hub.DisposeAsync(); }
    }

    [Fact]
    public async Task The_approve_endpoint_rejects_a_call_with_no_action_id()
    {
        await using var app = await ServerAppFactory.CreateAsync();

        var response = await app.Client.PostAsync("/mentor/approve?approved=true", null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Cancelling_over_http_stops_a_turn_that_is_streaming()
    {
        // The Stop button. Same constraint as approval: a CancelRequest hub call would queue behind
        // the SendMessage it was meant to abort.
        await using var app = await ServerAppFactory.CreateAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Chat.Say("this reply is going to be interrupted");
        app.Chat.BeforeRespond = () => gate.Task;

        var (hub, turn) = await ConnectAsync(app);
        try
        {
            var turnTask = hub.InvokeAsync("SendMessage", "tell me a long story", null);

            var response = await app.Client.PostAsync(
                $"/mentor/cancel?connectionId={hub.ConnectionId}", null);
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            gate.TrySetResult();
            await StageAsync(turnTask, "SendMessage returned", turn);

            hub.State.Should().Be(HubConnectionState.Connected,
                "stopping a turn must not drop the connection");
        }
        finally { await hub.DisposeAsync(); }
    }

    [Fact]
    public async Task Cancelling_an_unknown_connection_is_a_404_not_a_500()
    {
        await using var app = await ServerAppFactory.CreateAsync();

        var response = await app.Client.PostAsync("/mentor/cancel?connectionId=never-existed", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
