using System.Collections.Concurrent;
using System.Text.Json;
using A2A;
using FluentAssertions;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.AI;
using Xunit;

namespace Novior.MentorAgent.Tests.Server;

/// <summary>
/// The machine-readable marker of AI-generated text, for article 50 of the EU AI Act, on every surface a reply leaves
/// by: <c>aiGenerated: true</c> on the chunk and completed frames of <c>/mentor/chat</c>, a <c>ReplyStarted</c>
/// event before each reply on the hub — whose events carry their arguments by position, so a field could not be added
/// to the chunks without breaking the .NET clients — and <c>aiGenerated</c> in the metadata of the reply an A2A
/// caller gets. The widget's own marker is in <c>AiDisclosureTests</c>.
/// </summary>
public sealed class AiMarkerTests
{
    // ── /mentor/chat ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_chunk_and_completed_frames_of_a_reply_are_marked()
    {
        await using var app = await ServerAppFactory.CreateAsync(o => o.MemoryAutoCapture = false);
        app.Chat.Say("Hello from the stream.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var body = await app.Client.GetStringAsync("/mentor/chat?message=" + Uri.EscapeDataString("hi"), deadline.Token);

        var frames = body.Split('\n')
            .Where(l => l.StartsWith("data: ", StringComparison.Ordinal))
            .Select(l => JsonDocument.Parse(l["data: ".Length..]).RootElement)
            .ToList();
        var marked = frames.Where(f => f.GetProperty("type").GetString() is "chunk" or "completed").ToList();
        marked.Should().Contain(f => f.GetProperty("type").GetString() == "chunk")
            .And.Contain(f => f.GetProperty("type").GetString() == "completed");
        marked.Where(f => !IsMarked(f)).Should().BeEmpty("every frame of the reply carries the marker. Body: {0}", body);
    }

    private static bool IsMarked(JsonElement frame)
        => frame.TryGetProperty("aiGenerated", out var value) && value.ValueKind == JsonValueKind.True;

    // ── The hub ──────────────────────────────────────────────────────────────────

    /// <summary>The hub's events in order, with each ReplyStarted's argument.</summary>
    private sealed class Recorder
    {
        private readonly ConcurrentQueue<string> _order = new();
        private int _completed;

        internal ConcurrentQueue<MentorReplyStarted> Started { get; } = new();
        internal List<string> Order => [.. _order];
        internal int Completed => Volatile.Read(ref _completed);

        internal void Attach(HubConnection hub)
        {
            hub.On<MentorReplyStarted>("ReplyStarted", r => { Started.Enqueue(r); _order.Enqueue("ReplyStarted"); });
            hub.On<string>("StreamingChunk", _ => _order.Enqueue("StreamingChunk"));
            hub.On("StreamingCompleted", () => { _order.Enqueue("StreamingCompleted"); Interlocked.Increment(ref _completed); });
        }
    }

    private static async Task<(HubConnection Hub, Recorder Events)> ConnectAsync(ServerAppFactory app)
    {
        var hub = app.CreateHubConnection();
        var events = new Recorder();
        events.Attach(hub);
        await hub.StartAsync();
        return (hub, events);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!condition()) await Task.Delay(20, deadline.Token);
    }

    /// <summary>Each reply: one ReplyStarted, before its first chunk, marked.</summary>
    private static void EachReplyStartsMarked(Recorder events, int replies)
    {
        var order = events.Order;
        events.Started.Should().HaveCount(replies, "one ReplyStarted per reply. Order: {0}", string.Join(", ", order));
        events.Started.Should().OnlyContain(r => r.AiGenerated);

        // Split at each completion: every reply's own events start with ReplyStarted, then its chunks.
        var reply = new List<string>();
        foreach (var e in order)
        {
            if (e == "StreamingCompleted")
            {
                reply.Should().NotBeEmpty();
                reply[0].Should().Be("ReplyStarted", "before the reply's first chunk");
                reply.Count(x => x == "ReplyStarted").Should().Be(1);
                reply.Clear();
            }
            else reply.Add(e);
        }
    }

    [Fact]
    public async Task Each_reply_on_the_hub_starts_with_a_marked_ReplyStarted()
    {
        await using var app = await ServerAppFactory.CreateAsync(o => o.MemoryAutoCapture = false);
        app.Chat.Say("First reply.").Say("Second reply.");

        var (hub, events) = await ConnectAsync(app);
        try
        {
            await hub.InvokeAsync("SendMessage", "hi", null);
            await WaitForAsync(() => events.Completed >= 1);
            await hub.InvokeAsync("SendMessage", "again", null);
            await WaitForAsync(() => events.Completed >= 2);

            EachReplyStartsMarked(events, replies: 2);
        }
        finally { await hub.DisposeAsync(); }
    }

    [Fact]
    public async Task A_reply_delegated_to_a_specialist_starts_with_one_ReplyStarted()
    {
        await using var app = await ServerAppFactory.CreateAsync(o => o.MemoryAutoCapture = false);
        app.Chat.Respond((messages, options) =>
        {
            var tools = options?.Tools?.Select(t => t.Name).ToList() ?? [];
            var answered = messages.LastOrDefault()?.Contents.OfType<FunctionResultContent>().Any() == true;

            if (tools.Count == 0) return Text("SAFE");
            if (tools.Contains("route_to_specialist"))
                return answered ? Text("The order is in transit.") : Invoke("route_to_specialist", new() { ["request"] = "where is order 7?" });
            if (tools.All(t => t.StartsWith("handoff_to", StringComparison.Ordinal)))
                return Invoke(options!.Tools!.FirstOrDefault(t => (t.Description ?? "").Contains("shipping", StringComparison.OrdinalIgnoreCase))?.Name
                              ?? tools[0], []);
            if (tools.Contains("get_tracking_status"))
                return answered ? Text("In transit.") : Invoke("get_tracking_status", new() { ["orderId"] = 7 });
            return Text("SAFE");

            ChatResponse Text(string text) => new(new ChatMessage(ChatRole.Assistant, text)) { ModelId = app.Chat.ModelId };
            ChatResponse Invoke(string name, Dictionary<string, object?> args)
                => new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(Guid.NewGuid().ToString("N"), name, args)]))
                {
                    ModelId = app.Chat.ModelId,
                };
        });

        var (hub, events) = await ConnectAsync(app);
        try
        {
            await hub.InvokeAsync("SendMessage", "where is order 7?", null);
            await WaitForAsync(() => events.Completed >= 1);

            app.Chat.Calls.Should().Contain(c => c.ToolNames.Contains("get_tracking_status"),
                "the specialist answered. Warnings: {0}", app.Logs.WarningText);
            EachReplyStartsMarked(events, replies: 1);
        }
        finally { await hub.DisposeAsync(); }
    }

    // ── A2A ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_reply_an_A2A_caller_gets_is_marked_in_its_metadata()
    {
        await using var app = await ServerAppFactory.CreateAsync(
            configure: o => { o.A2AServerEnabled = true; o.A2AServerUrl = "http://localhost/a2a"; o.MemoryAutoCapture = false; },
            configureApp: a => a.UseEndpoints(e => e.MapMentorAgentA2A()));
        // A tool-backed answer: a peer's figure with no tool behind it is replaced by the grounding check (BUG-081).
        app.Chat.CallTool("count_open_orders").Say("Three open orders.");

        var card = await new A2ACardResolver(new Uri("http://localhost"), app.Client).GetAgentCardAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var response = await A2AClientFactory.Create(card, app.Client).SendMessageAsync(
            new SendMessageRequest
            {
                Message = new Message
                {
                    MessageId = Guid.NewGuid().ToString("N"),
                    Role = Role.User,
                    Parts = [Part.FromText("how many open orders?")],
                },
            }, deadline.Token);

        var reply = response.Task?.Status?.Message ?? response.Message;
        reply.Should().NotBeNull();
        string.Concat(reply!.Parts.Select(p => p.Text)).Should().Contain("Three open orders.");
        reply.Metadata.Should().NotBeNull().And.ContainKey("aiGenerated");
        reply.Metadata!["aiGenerated"].ValueKind.Should().Be(JsonValueKind.True);
    }
}
