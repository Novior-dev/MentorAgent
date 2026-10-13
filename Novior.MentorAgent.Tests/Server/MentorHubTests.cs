using System.Collections.Concurrent;
using FluentAssertions;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;

namespace Novior.MentorAgent.Tests.Server;

/// <summary>
/// The SignalR hub over a real <c>HubConnection</c> — until now, zero coverage.
///
/// <para>
/// A fake connection would prove nothing here. Everything worth testing about this hub is a
/// property of the genuine transport: that SignalR dispatches one invocation at a time per
/// connection (which is why approvals travel over HTTP), that a fresh DI scope is created per
/// invocation (so the connection id must be re-established every time), and that the event names
/// on the wire match what the README tells client authors to subscribe to.
/// </para>
/// </summary>
public sealed class MentorHubTests
{
    /// <summary>Subscribes to every documented server→client event and records arrivals in order.</summary>
    private sealed class EventRecorder
    {
        private readonly ConcurrentQueue<string> _order = new();

        internal ConcurrentQueue<string> StreamedChunks { get; } = new();
        internal TaskCompletionSource CompletedSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal List<string> Order => [.. _order];

        internal void Attach(HubConnection hub)
        {
            hub.On<string>("StreamingChunk", c => { _order.Enqueue("StreamingChunk"); StreamedChunks.Enqueue(c); });
            hub.On("StreamingCompleted", () => { _order.Enqueue("StreamingCompleted"); CompletedSignal.TrySetResult(); });
            hub.On<bool>("BusyChanged", _ => _order.Enqueue("BusyChanged"));
            hub.On<string>("Error", _ => { _order.Enqueue("Error"); CompletedSignal.TrySetResult(); });
            hub.On<int>("HostedToolsDeclared", _ => _order.Enqueue("HostedToolsDeclared"));
        }
    }

    private static async Task<(HubConnection Hub, EventRecorder Events)> ConnectAsync(ServerAppFactory app)
    {
        var hub = app.CreateHubConnection();
        var events = new EventRecorder();
        events.Attach(hub);
        await hub.StartAsync();
        return (hub, events);
    }

    [Fact]
    public async Task A_client_can_connect_to_the_documented_hub_path()
    {
        await using var app = await ServerAppFactory.CreateAsync();

        var (hub, _) = await ConnectAsync(app);
        try
        {
            hub.State.Should().Be(HubConnectionState.Connected);
            hub.ConnectionId.Should().NotBeNullOrEmpty(
                "the connection id is what POST /mentor/cancel is addressed to");
        }
        finally { await hub.DisposeAsync(); }
    }

    [Fact]
    public async Task HostedToolsDeclared_arrives_once_on_connect()
    {
        // Documented as "sent once on connect, before any message" — it is what lets a client
        // render its capability badge from the server's truth instead of a local copy.
        await using var app = await ServerAppFactory.CreateAsync(
            o => o.HostedTools = MentorHostedTools.WebSearch);

        var (hub, events) = await ConnectAsync(app);
        try
        {
            await WaitForAsync(() => events.Order.Contains("HostedToolsDeclared"));

            events.Order.Count(e => e == "HostedToolsDeclared").Should().Be(1);
        }
        finally { await hub.DisposeAsync(); }
    }

    [Fact]
    public async Task SendMessage_streams_chunks_then_completes()
    {
        await using var app = await ServerAppFactory.CreateAsync();
        app.Chat.Say("Hello from the hub");

        var (hub, events) = await ConnectAsync(app);
        try
        {
            await hub.InvokeAsync("SendMessage", "hi", null);
            await events.CompletedSignal.Task.WaitAsync(TimeSpan.FromSeconds(10));

            string.Concat(events.StreamedChunks).Should().Contain("Hello from the hub");

            // Order, not just presence: a chunk after completion, or completion before the last
            // token, is the class of defect that only an ordered assertion can see.
            var order = events.Order;
            order.Should().Contain("StreamingChunk");
            order.LastIndexOf("StreamingChunk").Should().BeLessThan(
                order.IndexOf("StreamingCompleted"),
                "every chunk must arrive before completion is announced");
        }
        finally { await hub.DisposeAsync(); }
    }

    [Fact]
    public async Task UpdatePageContext_is_accepted_before_a_message()
    {
        // The documented client flow calls this before every SendMessage.
        await using var app = await ServerAppFactory.CreateAsync();
        app.Chat.Say("ok");

        var (hub, events) = await ConnectAsync(app);
        try
        {
            // The documented shape is pageName + contextData + uiActions — there is deliberately
            // no url, because the client's router owns navigation and the server never needs it.
            await hub.InvokeAsync("UpdatePageContext", new PageContextSnapshot
            {
                PageName = "Orders",
                ContextData = new Dictionary<string, string?>
                {
                    ["activeFilter"] = "Pending",
                    ["visibleRows"] = "15",
                },
                UIActions =
                [
                    new UIActionInfo
                    {
                        Name = "highlight_row",
                        Description = "Highlights a row in the order table",
                        ParameterHint = "integer: order ID",
                    },
                ],
            });

            await hub.InvokeAsync("SendMessage", "what is on this page?", null);
            await events.CompletedSignal.Task.WaitAsync(TimeSpan.FromSeconds(10));

            events.Order.Should().Contain("StreamingCompleted");
        }
        finally { await hub.DisposeAsync(); }
    }

    [Fact]
    public async Task ResetSession_is_accepted_and_the_connection_survives()
    {
        await using var app = await ServerAppFactory.CreateAsync();
        app.Chat.Say("first").Say("second");

        var (hub, events) = await ConnectAsync(app);
        try
        {
            await hub.InvokeAsync("SendMessage", "one", null);
            await events.CompletedSignal.Task.WaitAsync(TimeSpan.FromSeconds(10));

            await hub.InvokeAsync("ResetSession");

            hub.State.Should().Be(HubConnectionState.Connected,
                "resetting the conversation must not drop the connection");
        }
        finally { await hub.DisposeAsync(); }
    }

    [Fact]
    public async Task The_per_invocation_scope_still_resolves_the_right_connection()
    {
        // SignalR creates a fresh DI scope per hub method call, so anything captured from the
        // connection has to be re-established on every invocation rather than only in
        // OnConnectedAsync. If that ever regresses, the second call is where it shows.
        await using var app = await ServerAppFactory.CreateAsync();
        app.Chat.SayRepeatedly("ok", times: 4);

        var (hub, events) = await ConnectAsync(app);
        try
        {
            await hub.InvokeAsync("SendMessage", "first", null);
            await events.CompletedSignal.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var afterFirst = events.Order.Count;

            await hub.InvokeAsync("SendMessage", "second", null);
            await WaitForAsync(() => events.Order.Count > afterFirst);

            events.Order.Count.Should().BeGreaterThan(afterFirst,
                "a second invocation must be served by its own scope, not silently dropped");
        }
        finally { await hub.DisposeAsync(); }
    }

    [Fact]
    public async Task Two_connections_do_not_share_a_session()
    {
        // Per-connection scoping is what keeps one user's conversation out of another's.
        await using var app = await ServerAppFactory.CreateAsync();
        app.Chat.SayRepeatedly("ok", times: 4);

        var (first, firstEvents) = await ConnectAsync(app);
        var (second, secondEvents) = await ConnectAsync(app);
        try
        {
            first.ConnectionId.Should().NotBe(second.ConnectionId);

            await first.InvokeAsync("SendMessage", "only the first client asked this", null);
            await firstEvents.CompletedSignal.Task.WaitAsync(TimeSpan.FromSeconds(10));

            secondEvents.Order.Should().NotContain("StreamingChunk",
                "a turn on one connection must not stream to another");
        }
        finally
        {
            await first.DisposeAsync();
            await second.DisposeAsync();
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(25);
        }

        throw new TimeoutException("Condition was not met within the timeout.");
    }
}
