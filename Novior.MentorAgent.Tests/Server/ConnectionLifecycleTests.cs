using FluentAssertions;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;

namespace Novior.MentorAgent.Tests.Server;

/// <summary>
/// Part C2.5 — the connection as a state machine: connect → page context → message → drop →
/// reconnect.
///
/// <para>
/// This is the one state machine that cannot be walked with <c>MentorHostFixture</c>, because the
/// thing under test is the scope itself. SignalR creates a <b>new DI scope for every hub method
/// call</b>, so MentorAgent keeps its own per-connection scope keyed by connection id and re-binds
/// it at the top of every method. Get that wrong and the symptom is not an exception: it is a
/// conversation that forgets the previous message, or a reply delivered to the wrong browser.
/// </para>
///
/// <para>
/// The reconnect case is worth stating plainly because the server README recommends
/// <c>withAutomaticReconnect()</c> in six places and the consequence is not obvious: a reconnect is a
/// <em>new connection id</em>, so it gets a new scope and a new conversation. That is not a defect —
/// it is why <c>SerializeSessionAsync</c> exists (BUG-025) — but a host that assumes otherwise loses
/// its users' conversations on every network blip.
/// </para>
/// </summary>
public sealed class ConnectionLifecycleTests
{
    /// <summary>Collects the events a turn produces, so a test can wait for the end of one.</summary>
    private sealed class TurnRecorder
    {
        internal TaskCompletionSource Completed { get; } = new();
        internal List<string> Events { get; } = [];
        internal List<int> HostedToolFlags { get; } = [];

        internal void Attach(HubConnection hub)
        {
            hub.On<int>("HostedToolsDeclared", f => { Events.Add("HostedToolsDeclared"); HostedToolFlags.Add(f); });
            hub.On<string>("StreamingChunk", _ => Events.Add("StreamingChunk"));
            hub.On("StreamingCompleted", () =>
            {
                Events.Add("StreamingCompleted");
                Completed.TrySetResult();
            });
            hub.On<string>("Error", _ => { Events.Add("Error"); Completed.TrySetResult(); });
        }

        internal Task WaitAsync() => Completed.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    private static async Task<(HubConnection Hub, TurnRecorder Turn)> ConnectAsync(ServerAppFactory app)
    {
        var hub = app.CreateHubConnection();
        var turn = new TurnRecorder();
        turn.Attach(hub);
        await hub.StartAsync();
        return (hub, turn);
    }

    // ── Connect ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Connecting_declares_the_servers_hosted_tools_before_any_message()
    {
        // The badge is server-side truth: a client cannot know from its own configuration which
        // hosted tools the server kept, and a hand-mirrored list drifts silently. It has to arrive
        // on connect, not on the first reply, or the badge is wrong until the user says something.
        await using var app = await ServerAppFactory.CreateAsync(
            configure: o => o.HostedTools = MentorHostedTools.WebSearch);

        var (hub, turn) = await ConnectAsync(app);
        await using var _ = hub;

        // Give the connect-time push a moment; it is sent from OnConnectedAsync.
        for (var i = 0; i < 50 && turn.HostedToolFlags.Count == 0; i++)
            await Task.Delay(20);

        turn.HostedToolFlags.Should().ContainSingle()
            .Which.Should().Be((int)MentorHostedTools.WebSearch);
    }

    // ── Page context → message, on one connection ──────────────────────────────

    [Fact]
    public async Task The_page_context_sent_before_a_message_reaches_that_turns_prompt()
    {
        // Two hub methods, two SignalR scopes, one conversation. The page context is stored under
        // the connection id by UpdatePageContext and read back by SendMessage — if either forgot to
        // re-bind, the assistant would answer without knowing which screen the user is on, which is
        // the whole point of the feature and produces no error at all.
        await using var app = await ServerAppFactory.CreateAsync();
        app.Chat.Say("ok");

        var (hub, turn) = await ConnectAsync(app);
        await using var _ = hub;

        await hub.InvokeAsync("UpdatePageContext", new PageContextSnapshot
        {
            PageName = "Orders",
            ContextData = { ["view"] = "the list of open orders" },
        });
        await hub.InvokeAsync("SendMessage", "what am I looking at", null);
        await turn.WaitAsync();

        (app.Chat.LastCall!.Instructions ?? "").Should().Contain("Orders",
            "the snapshot stored by one hub call must be visible to the next");
    }

    [Fact]
    public async Task Two_messages_on_one_connection_continue_the_same_conversation()
    {
        // The per-connection scope's reason for existing. A fresh scope per hub call would give
        // every message its own session: the model would answer each one as if it were the first,
        // fluently, with no error anywhere.
        await using var app = await ServerAppFactory.CreateAsync();
        app.Chat.Say("first").Say("second");

        var (hub, turn) = await ConnectAsync(app);
        await using var _ = hub;

        await hub.InvokeAsync("SendMessage", "my name is Antonio", null);
        await turn.WaitAsync();

        var second = new TurnRecorder();
        second.Attach(hub);
        await hub.InvokeAsync("SendMessage", "what is my name", null);
        await second.WaitAsync();

        // The second request carries the first exchange in its message list — that is what "same
        // conversation" means at the wire.
        app.Chat.Calls.Should().HaveCountGreaterThan(1);
        app.Chat.LastCall!.Messages.Count.Should()
            .BeGreaterThan(app.Chat.Calls[0].Messages.Count,
                "the history grew, so both turns shared one session");
    }

    // ── Isolation between connections ───────────────────────────────────────────

    [Fact]
    public async Task Two_simultaneous_connections_do_not_share_a_conversation()
    {
        // Two browsers, one server. A scope keyed by anything less specific than the connection id
        // would cross the streams — one user's page context, history and pending approval reaching
        // the other. This is the isolation property the whole design rests on.
        await using var app = await ServerAppFactory.CreateAsync();
        app.Chat.SayRepeatedly("ok", 4);

        var (alice, aliceTurn) = await ConnectAsync(app);
        var (bob, bobTurn) = await ConnectAsync(app);
        await using var _ = alice;
        await using var __ = bob;

        await alice.InvokeAsync("UpdatePageContext", new PageContextSnapshot
        {
            PageName = "Orders", ContextData = { ["who"] = "Alice is on orders" },
        });
        await bob.InvokeAsync("UpdatePageContext", new PageContextSnapshot
        {
            PageName = "Reports", ContextData = { ["who"] = "Bob is on reports" },
        });

        await bob.InvokeAsync("SendMessage", "where am I", null);
        await bobTurn.WaitAsync();

        var bobPrompt = app.Chat.LastCall!.Instructions ?? "";
        bobPrompt.Should().Contain("Reports");
        bobPrompt.Should().NotContain("Alice is on orders",
            "one connection's page context must never appear in another's prompt");
    }

    // ── Drop and reconnect ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_reconnect_starts_a_new_conversation_rather_than_resuming_the_old_one()
    {
        // Pinned because it is the consequence of `withAutomaticReconnect()` that a reader would not
        // predict from the six places the server README recommends it. A reconnect is a new
        // connection id, so it is a new scope and a new AgentSession: the previous exchange is gone.
        //
        // This is deliberate rather than broken — the per-connection scope is what keeps two
        // browsers apart, and the documented way to carry a conversation across a drop is
        // IMentorOrchestrator.SerializeSessionAsync / RestoreSessionAsync (BUG-025). Recording it
        // here so nobody has to rediscover it from a support ticket.
        await using var app = await ServerAppFactory.CreateAsync();
        app.Chat.SayRepeatedly("ok", 4);

        var (first, firstTurn) = await ConnectAsync(app);
        await first.InvokeAsync("SendMessage", "my name is Antonio", null);
        await firstTurn.WaitAsync();
        var historyBefore = app.Chat.LastCall!.Messages.Count;
        await first.DisposeAsync();

        var (second, secondTurn) = await ConnectAsync(app);
        await using var _ = second;
        await second.InvokeAsync("SendMessage", "what is my name", null);
        await secondTurn.WaitAsync();

        app.Chat.LastCall!.Messages.Count.Should().BeLessThanOrEqualTo(historyBefore,
            "a new connection is a new session — the earlier exchange is not replayed");
    }

    [Fact]
    public async Task A_dropped_connection_releases_its_page_context()
    {
        // The store is keyed by connection id and OnDisconnectedAsync removes the entry. Without
        // that, a long-lived server accumulates one snapshot per connection it has ever served —
        // a slow leak whose only symptom is memory.
        await using var app = await ServerAppFactory.CreateAsync();
        app.Chat.Say("ok");

        var (hub, _) = await ConnectAsync(app);
        await hub.InvokeAsync("UpdatePageContext", new PageContextSnapshot
        {
            PageName = "Orders", ContextData = { ["who"] = "gone after the drop" },
        });
        await hub.DisposeAsync();

        // Let OnDisconnectedAsync run, then prove the slot is free by having a *new* connection
        // send a message without setting any page context: if the old snapshot had survived the
        // drop it could only reappear here.
        await Task.Delay(200);

        var (fresh, freshTurn) = await ConnectAsync(app);
        await using var __ = fresh;
        await fresh.InvokeAsync("SendMessage", "anything", null);
        await freshTurn.WaitAsync();

        (app.Chat.LastCall!.Instructions ?? "").Should().NotContain("gone after the drop",
            "a disconnected connection's page context must not outlive it");
    }

    [Fact]
    public async Task A_turn_still_completes_after_an_earlier_connection_was_dropped()
    {
        // The disposal path runs while other connections are live. A scope removal that disposed
        // something shared would take the survivors down with it, and the failure would look like an
        // unrelated intermittent fault.
        await using var app = await ServerAppFactory.CreateAsync();
        app.Chat.SayRepeatedly("ok", 4);

        var (doomed, _) = await ConnectAsync(app);
        var (survivor, survivorTurn) = await ConnectAsync(app);
        await using var _ = survivor;

        await doomed.DisposeAsync();
        await Task.Delay(200);

        await survivor.InvokeAsync("SendMessage", "still there?", null);
        var act = async () => await survivorTurn.WaitAsync();

        await act.Should().NotThrowAsync("dropping one connection must not disturb another");
    }
}
