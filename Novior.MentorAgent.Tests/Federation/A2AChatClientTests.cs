using System.Runtime.CompilerServices;
using A2A;
using FluentAssertions;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Novior.MentorAgent.Tests.Federation;

/// <summary>
/// <see cref="A2AChatClient"/> — the adapter that lets a remote A2A agent stand in the handoff
/// graph as though it were a local specialist.
///
/// <para>
/// It is a translation layer between two protocols, and every translation layer has the same
/// failure mode: something on one side has no counterpart on the other, and the mapping quietly
/// chooses a default. Here that is the response shape — A2A answers with either a direct
/// <c>Message</c> or a <c>Task</c> whose history holds the answer — and the conversation id, which
/// A2A calls a context and the Agent Framework does not model at all. Losing the context turns a
/// remote specialist into one with no memory between turns, and nothing in the transcript says so.
/// </para>
/// </summary>
public sealed class A2AChatClientTests
{
    /// <summary>
    /// A scripted <see cref="IA2AClient"/>. Only the two send methods are ever reached by the
    /// adapter; the other nine exist because the interface demands them, and throwing is the
    /// honest body — if the adapter ever starts calling one, the test that does so should say
    /// which rather than quietly receive a null.
    /// </summary>
    private sealed class FakeA2AClient : IA2AClient
    {
        internal List<SendMessageRequest> Sent { get; } = [];
        internal Func<SendMessageRequest, SendMessageResponse>? OnSend { get; set; }
        internal List<StreamResponse> StreamEvents { get; } = [];
        internal int Streams { get; private set; }

        public Task<SendMessageResponse> SendMessageAsync(
            SendMessageRequest request, CancellationToken cancellationToken = default)
        {
            Sent.Add(request);
            return Task.FromResult(OnSend?.Invoke(request) ?? FromMessage(AgentSays("ok")));
        }

        public async IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(
            SendMessageRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Streams++;
            Sent.Add(request);
            if (Opening is { } opening) await opening(cancellationToken);
            foreach (var evt in StreamEvents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return evt;
            }
            if (Hold is { } hold) await hold(cancellationToken);
            await Task.CompletedTask;
        }

        /// <summary>Awaited before the scripted events: a peer slow to say anything.</summary>
        internal Func<CancellationToken, Task>? Opening { get; set; }

        /// <summary>Awaited after the scripted events: a peer still working on the task.</summary>
        internal Func<CancellationToken, Task>? Hold { get; set; }

        /// <summary>The tasks the adapter asked the peer to cancel (BUG-210).</summary>
        internal List<string> Cancelled { get; } = [];

        /// <summary>Awaited before the cancel is answered: a peer slow to take it.</summary>
        internal Func<CancellationToken, Task>? OnCancel { get; set; }

        public async Task<AgentTask> CancelTaskAsync(CancelTaskRequest r, CancellationToken ct = default)
        {
            Cancelled.Add(r.Id);
            if (OnCancel is { } slow) await slow(ct);
            return new AgentTask { Id = r.Id, Status = new A2A.TaskStatus { State = TaskState.Canceled } };
        }

        public Task<AgentTask> GetTaskAsync(GetTaskRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ListTasksResponse> ListTasksAsync(ListTasksRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(SubscribeToTaskRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(TaskPushNotificationConfig c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(GetTaskPushNotificationConfigRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ListTaskPushNotificationConfigsResponse> ListTaskPushNotificationConfigsAsync(ListTaskPushNotificationConfigsRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteTaskPushNotificationConfigAsync(DeleteTaskPushNotificationConfigRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentCard> GetExtendedAgentCardAsync(GetExtendedAgentCardRequest r, CancellationToken ct = default) => throw new NotSupportedException();
    }

    // ── Builders for A2A payloads ───────────────────────────────────────────────

    private static Message AgentSays(string text, string? contextId = null) => new()
    {
        Role = A2A.Role.Agent,
        Parts = [Part.FromText(text)],
        ContextId = contextId,
    };

    private static SendMessageResponse FromMessage(Message message)
        => new() { Message = message };

    private static SendMessageResponse FromTask(AgentTask task)
        => new() { Task = task };

    private static A2AChatClient Client(FakeA2AClient fake, string name = "billing")
        => new(fake, name);

    private static IEnumerable<ChatMessage> UserSays(string text)
        => [new ChatMessage(ChatRole.User, text)];

    private static CancellationToken Deadline()
        => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    // ── What is sent ────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_last_user_message_is_what_travels()
    {
        // The remote agent is handed one request, not the whole local conversation — the coordinator
        // has already decided what to ask it. Sending an earlier turn would ask the wrong question.
        var fake = new FakeA2AClient();

        await Client(fake).GetResponseAsync(
            [new ChatMessage(ChatRole.User, "first"),
             new ChatMessage(ChatRole.Assistant, "in between"),
             new ChatMessage(ChatRole.User, "the real question")],
            cancellationToken: Deadline());

        fake.Sent.Should().ContainSingle().Which.Message!.Parts[0].Text.Should().Be("the real question");
    }

    [Fact]
    public async Task A_turn_with_no_user_message_sends_an_empty_request_rather_than_throwing()
    {
        // Reachable when the coordinator hands over a tool-result-only turn. An exception here
        // would surface as a failed handoff for something the remote agent could have ignored.
        var fake = new FakeA2AClient();

        var act = async () => await Client(fake).GetResponseAsync(
            [new ChatMessage(ChatRole.Assistant, "no question here")], cancellationToken: Deadline());

        await act.Should().NotThrowAsync();
    }

    // ── BUG-080 · what must not travel ──────────────────────────────────────────

    [Fact]
    public async Task The_local_routers_marker_does_not_cross_the_wire()
    {
        // Found live on the published rc.8, the first build in which a delegation to a live peer
        // completed. route_to_specialist prefixes the workflow's input with "[Specialist requested:
        // NAME]" so the LOCAL router cannot lose the name (BUG-076) — and this adapter sent the last
        // user message verbatim, so the peer's coordinator read an order addressed to somebody else's
        // router. Every sample calls its remote agent "ShopFlowRemote" and two of them peer each
        // other: the peer obeyed, its peer obeyed, and one question became nine tasks before a
        // server was killed by hand.
        var fake = new FakeA2AClient();

        await Client(fake, "ShopFlowRemote").GetResponseAsync(
            UserSays("[Specialist requested: ShopFlowRemote]\nquanti prodotti a catalogo?"),
            cancellationToken: Deadline());

        fake.Sent.Should().ContainSingle().Which.Message!.Parts[0].Text
            .Should().Be("quanti prodotti a catalogo?");
    }

    [Fact]
    public async Task The_marker_is_stripped_on_the_streaming_path_too()
    {
        // Inside the handoff workflow an agent runs streaming (BUG-078), so this is the path the
        // live loop actually took.
        var fake = new FakeA2AClient();
        fake.StreamEvents.Add(new StreamResponse { Message = AgentSays("8") });

        await foreach (var _ in Client(fake, "ShopFlowRemote").GetStreamingResponseAsync(
                           UserSays("[Specialist requested: ShopFlowRemote]\r\nquanti prodotti a catalogo?"),
                           cancellationToken: Deadline())) { }

        fake.Sent.Should().ContainSingle().Which.Message!.Parts[0].Text
            .Should().Be("quanti prodotti a catalogo?");
    }

    [Fact]
    public async Task A_specialist_name_with_brackets_in_it_leaves_nothing_behind()
    {
        // The name is an argument the model writes. The marker is its whole first line, so what
        // travels is exactly what route_to_specialist was given as the request.
        var fake = new FakeA2AClient();
        var routed = SpecialistMarker.Apply("quanti prodotti a catalogo?", " Agent[EU]\nShopFlowRemote ");

        await Client(fake).GetResponseAsync(UserSays(routed), cancellationToken: Deadline());

        routed.Split('\n').Should().HaveCount(2, "a name is one line whatever the model put in it");
        fake.Sent.Single().Message!.Parts[0].Text.Should().Be("quanti prodotti a catalogo?");
    }

    // ── BUG-081 · what travels beside the text ──────────────────────────────────

    [Fact]
    public async Task The_name_this_host_has_for_the_peer_travels_as_metadata_not_as_text()
    {
        // The name is in the request anyway — "quanti prodotti ha ShopFlowRemote?" — and the peer has
        // no way to know it means itself: on the published rc.9 it answered "I have no access to
        // ShopFlowRemote" 3 times in 6, without calling a tool. As metadata it is data: a peer that
        // does not know the key ignores it, and the text is exactly what the coordinator asked.
        var fake = new FakeA2AClient();

        await Client(fake, "ShopFlowRemote").GetResponseAsync(
            UserSays("quanti prodotti ha ShopFlowRemote?"), cancellationToken: Deadline());

        var sent = fake.Sent.Single().Message!;
        sent.Parts[0].Text.Should().Be("quanti prodotti ha ShopFlowRemote?");
        sent.Metadata.Should().ContainKey(MentorTurnOrigin.AddressedAsMetadataKey)
            .WhoseValue.GetString().Should().Be("ShopFlowRemote");
    }

    [Fact]
    public async Task The_name_travels_on_the_streaming_path_too()
    {
        var fake = new FakeA2AClient();
        fake.StreamEvents.Add(new StreamResponse { Message = AgentSays("8") });

        await foreach (var _ in Client(fake, "ShopFlowRemote").GetStreamingResponseAsync(
                           UserSays("quanti prodotti?"), cancellationToken: Deadline())) { }

        fake.Sent.Single().Message!.Metadata.Should().ContainKey(MentorTurnOrigin.AddressedAsMetadataKey);
    }

    [Fact]
    public async Task A_request_that_only_looks_bracketed_travels_unchanged()
    {
        // The control: only the router's own marker, at the very start, is removed.
        var fake = new FakeA2AClient();

        await Client(fake).GetResponseAsync(
            UserSays("[urgente] quanti prodotti? [Specialist requested: nobody]"), cancellationToken: Deadline());

        fake.Sent.Single().Message!.Parts[0].Text
            .Should().Be("[urgente] quanti prodotti? [Specialist requested: nobody]");
    }

    // ── What comes back ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_direct_message_reply_is_the_answer()
    {
        var fake = new FakeA2AClient { OnSend = _ => FromMessage(AgentSays("invoice 41 is paid")) };

        var response = await Client(fake).GetResponseAsync(UserSays("is invoice 41 paid"), cancellationToken: Deadline());

        response.Text.Should().Be("invoice 41 is paid");
    }

    [Fact]
    public async Task A_task_reply_is_read_from_the_last_thing_the_agent_said()
    {
        // The other half of the protocol: a long-running A2A task answers with its history rather
        // than a message. Reading the wrong end of it returns the question back to the coordinator.
        var fake = new FakeA2AClient
        {
            OnSend = _ => FromTask(new AgentTask
            {
                Id = "t1",
                History =
                [
                    new Message { Role = A2A.Role.User, Parts = [Part.FromText("is invoice 41 paid")] },
                    AgentSays("looking"),
                    AgentSays("invoice 41 is paid"),
                ],
            }),
        };

        var response = await Client(fake).GetResponseAsync(UserSays("is invoice 41 paid"), cancellationToken: Deadline());

        response.Text.Should().Be("invoice 41 is paid");
    }

    [Fact]
    public async Task A_task_whose_history_holds_nothing_from_the_agent_says_so()
    {
        // Better than returning the user's own words back to the coordinator, which reads as the
        // remote agent agreeing with the question.
        var fake = new FakeA2AClient
        {
            OnSend = _ => FromTask(new AgentTask
            {
                Id = "t1",
                History = [new Message { Role = A2A.Role.User, Parts = [Part.FromText("hello")] }],
            }),
        };

        var response = await Client(fake).GetResponseAsync(UserSays("hello"), cancellationToken: Deadline());

        response.Text.Should().Be("No response from remote agent.");
    }

    [Fact]
    public async Task A_reply_that_is_neither_a_message_nor_a_task_does_not_come_back_empty()
    {
        // An empty assistant turn is indistinguishable from a working agent that had nothing to
        // add, and the coordinator would relay the silence as an answer.
        var fake = new FakeA2AClient { OnSend = _ => new SendMessageResponse() };

        var response = await Client(fake).GetResponseAsync(UserSays("hello"), cancellationToken: Deadline());

        response.Text.Should().Be("No response from remote agent.");
    }

    // ── The conversation context ────────────────────────────────────────────────

    [Fact]
    public async Task The_first_turn_carries_no_context_because_there_is_none_yet()
    {
        var fake = new FakeA2AClient { OnSend = _ => FromMessage(AgentSays("ok", contextId: "ctx-1")) };

        await Client(fake).GetResponseAsync(UserSays("first"), cancellationToken: Deadline());

        fake.Sent.Single().Message!.ContextId.Should().BeNull();
    }

    [Fact]
    public async Task The_context_the_remote_agent_returned_is_sent_back_on_the_next_turn()
    {
        // This is what gives a remote specialist a memory. Without it every turn starts a new
        // conversation on the other side, and the failure is invisible: the agent still answers,
        // it just answers as though it had never been asked anything before.
        var fake = new FakeA2AClient { OnSend = _ => FromMessage(AgentSays("ok", contextId: "ctx-1")) };
        var client = Client(fake);

        await client.GetResponseAsync(UserSays("first"), cancellationToken: Deadline());
        await client.GetResponseAsync(UserSays("second"), cancellationToken: Deadline());

        fake.Sent[1].Message!.ContextId.Should().Be("ctx-1");
    }

    [Fact]
    public async Task A_context_that_arrives_on_a_task_is_kept_too()
    {
        var fake = new FakeA2AClient
        {
            OnSend = _ => FromTask(new AgentTask
            {
                Id = "t1", ContextId = "ctx-task", History = [AgentSays("done")],
            }),
        };
        var client = Client(fake);

        await client.GetResponseAsync(UserSays("first"), cancellationToken: Deadline());
        await client.GetResponseAsync(UserSays("second"), cancellationToken: Deadline());

        fake.Sent[1].Message!.ContextId.Should().Be("ctx-task");
    }

    [Fact]
    public async Task A_reply_with_no_context_does_not_wipe_the_one_already_held()
    {
        // Not every message in a conversation echoes the context. Overwriting it with null on one
        // of them would silently restart the remote conversation mid-flow.
        var replies = new Queue<SendMessageResponse>(
        [
            FromMessage(AgentSays("ok", contextId: "ctx-1")),
            FromMessage(AgentSays("still ok")),
            FromMessage(AgentSays("and again")),
        ]);
        var fake = new FakeA2AClient { OnSend = _ => replies.Dequeue() };
        var client = Client(fake);

        await client.GetResponseAsync(UserSays("first"), cancellationToken: Deadline());
        await client.GetResponseAsync(UserSays("second"), cancellationToken: Deadline());
        await client.GetResponseAsync(UserSays("third"), cancellationToken: Deadline());

        fake.Sent[2].Message!.ContextId.Should().Be("ctx-1");
    }

    // ── Streaming ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Streamed_messages_are_forwarded_as_updates()
    {
        var fake = new FakeA2AClient();
        fake.StreamEvents.Add(new StreamResponse { Message = AgentSays("invoice ") });
        fake.StreamEvents.Add(new StreamResponse { Message = AgentSays("41 is paid") });

        var text = new System.Text.StringBuilder();
        await foreach (var update in Client(fake).GetStreamingResponseAsync(
                           UserSays("is invoice 41 paid"), cancellationToken: Deadline()))
            text.Append(update.Text);

        text.ToString().Should().Be("invoice 41 is paid");
    }

    [Fact]
    public async Task Streaming_keeps_the_context_for_the_next_turn()
    {
        var fake = new FakeA2AClient();
        fake.StreamEvents.Add(new StreamResponse { Message = AgentSays("done", contextId: "ctx-stream") });
        var client = Client(fake);

        await foreach (var _ in client.GetStreamingResponseAsync(UserSays("first"), cancellationToken: Deadline())) { }
        await client.GetResponseAsync(UserSays("second"), cancellationToken: Deadline());

        fake.Sent[1].Message!.ContextId.Should().Be("ctx-stream");
    }

    [Fact]
    public async Task Streamed_updates_are_attributed_to_the_assistant()
    {
        // The widget renders on role. An update the framework reads as a user turn would appear on
        // the wrong side of the conversation.
        var fake = new FakeA2AClient();
        fake.StreamEvents.Add(new StreamResponse { Message = AgentSays("hello") });

        await foreach (var update in Client(fake).GetStreamingResponseAsync(
                           UserSays("hi"), cancellationToken: Deadline()))
            update.Role.Should().Be(ChatRole.Assistant);
    }

    // ── A turn stopped during the delegation (BUG-210) ──────────────────────────

    private static StreamResponse Working(string taskId)
        => new() { Task = new AgentTask { Id = taskId, Status = new A2A.TaskStatus { State = TaskState.Working } } };

    [Fact]
    public async Task A_peer_that_never_takes_the_cancel_holds_the_stopped_turn_two_seconds_at_most()
    {
        var fake = new FakeA2AClient
        {
            Hold = ct => Task.Delay(Timeout.Infinite, ct),
            OnCancel = ct => Task.Delay(Timeout.Infinite, ct),
        };
        fake.StreamEvents.Add(Working("t7"));
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var read = Task.Run(async () =>
        {
            await foreach (var _ in Client(fake).GetStreamingResponseAsync(UserSays("refund order 7"), cancellationToken: stop.Token)) { }
        });

        (await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(4))) == read)
            .Should().BeTrue("the cancel is bounded, not the stopped turn's to wait for");
        await FluentActions.Awaiting(() => read).Should().ThrowAsync<OperationCanceledException>();
        fake.Cancelled.Should().Equal(["t7"], "the task of the stream is the one cancelled");
    }

    [Fact]
    public async Task A_stop_before_the_peers_first_event_still_cancels_the_task_it_names()
    {
        // Stopped before anything named the task, the caller has no id to cancel it by. Dropped at once, the request
        // left the task running; it stays open a moment longer, for the event that names it.
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeA2AClient
        {
            Opening = async ct => { await stopped.Task; await Task.Delay(100, ct); },
            Hold = ct => Task.Delay(Timeout.Infinite, ct),
        };
        fake.StreamEvents.Add(Working("t10"));
        using var stop = new CancellationTokenSource();

        var read = Task.Run(async () =>
        {
            await foreach (var _ in Client(fake).GetStreamingResponseAsync(UserSays("refund order 10"), cancellationToken: stop.Token)) { }
        });
        await Until(() => fake.Streams == 1);
        stop.Cancel();
        stopped.SetResult();   // the peer's first event comes after the Stop
        var clock = System.Diagnostics.Stopwatch.StartNew();

        await FluentActions.Awaiting(() => read).Should().ThrowAsync<OperationCanceledException>();
        fake.Cancelled.Should().Equal(["t10"], "the event that came after the Stop named the task to cancel");
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1),
            "the stream ends with the event that names the task, not when the wait for it runs out");
    }

    private static async Task Until(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            deadline.Token.ThrowIfCancellationRequested();
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task A_read_that_fails_once_the_turn_is_stopped_is_reported_as_the_stop()
    {
        // The connection is torn down under the stopped read, which then fails with an IOException rather than a
        // cancellation; above, the workflow would read it as a peer that failed.
        var fake = new FakeA2AClient
        {
            Hold = async ct =>
            {
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { throw new IOException("The response ended prematurely."); }
            },
        };
        fake.StreamEvents.Add(Working("t8"));
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var read = async () =>
        {
            await foreach (var _ in Client(fake).GetStreamingResponseAsync(UserSays("refund order 8"), cancellationToken: stop.Token)) { }
        };

        (await read.Should().ThrowAsync<OperationCanceledException>()).WithInnerException<IOException>();
        fake.Cancelled.Should().Equal(["t8"]);
    }

    [Fact]
    public async Task A_stream_that_reaches_its_answer_cancels_nothing()
    {
        var fake = new FakeA2AClient();
        fake.StreamEvents.Add(Working("t9"));
        fake.StreamEvents.Add(new StreamResponse
        {
            StatusUpdate = new TaskStatusUpdateEvent
            {
                TaskId = "t9",
                Status = new A2A.TaskStatus { State = TaskState.Completed, Message = AgentSays("order 9 refunded") },
            },
        });

        var text = new System.Text.StringBuilder();
        await foreach (var update in Client(fake).GetStreamingResponseAsync(UserSays("refund order 9"), cancellationToken: Deadline()))
            text.Append(update.Text);

        text.ToString().Should().Be("order 9 refunded");
        fake.Cancelled.Should().BeEmpty("a task that ended is not cancelled");
    }

    // ── A peer that does not stream (BUG-197) ───────────────────────────────────

    private static async Task<string> StreamText(A2AChatClient client)
    {
        var text = new System.Text.StringBuilder();
        await foreach (var update in client.GetStreamingResponseAsync(UserSays("is invoice 41 paid"), cancellationToken: Deadline()))
            text.Append(update.Text);
        return text.ToString();
    }

    [Fact]
    public async Task A_peer_whose_card_declares_no_streaming_is_asked_without_a_stream()
    {
        // From A2A 1.0.0-preview3 a server refuses a streamed request unless its agent declares streaming, and the
        // SDK's client reads the refusal as a stream with nothing in it. The handoff workflow always streams, so a
        // turn delegated to such a peer came back with no answer and no error.
        var fake = new FakeA2AClient { OnSend = _ => FromMessage(AgentSays("41 is paid")) };
        var client = A2AChatClient.ForCard(new AgentCard { Name = "billing" }, fake, "billing", LoggerFactory.Create(_ => { }).CreateLogger("test"));

        (await StreamText(client)).Should().Be("41 is paid");
        fake.Streams.Should().Be(0, "the peer does not stream");
    }

    [Fact]
    public async Task A_peer_whose_card_declares_streaming_is_streamed()
    {
        var fake = new FakeA2AClient();
        fake.StreamEvents.Add(new StreamResponse { Message = AgentSays("41 is paid") });
        var card = new AgentCard { Name = "billing", Capabilities = new AgentCapabilities { Streaming = true } };

        (await StreamText(A2AChatClient.ForCard(card, fake, "billing", LoggerFactory.Create(_ => { }).CreateLogger("test"))))
            .Should().Be("41 is paid");
        fake.Streams.Should().Be(1);
    }

    [Fact]
    public async Task A_stream_that_ends_without_an_answer_is_logged()
    {
        var logs = new TestLogCollector();
        var logger = LoggerFactory.Create(b => b.AddProvider(new TestLoggerProvider(logs))).CreateLogger("test");
        var client = new A2AChatClient(new FakeA2AClient(), "billing", streaming: true, logger);

        (await StreamText(client)).Should().BeEmpty();
        logs.HasWarning("'billing' ended its stream without an answer").Should().BeTrue(logs.WarningText);
    }

    // ── Identity ────────────────────────────────────────────────────────────────

    [Fact]
    public void The_agents_name_is_reported_as_the_model_id()
    {
        // Per-model metrics key on the model id, so this is what puts a remote agent's traffic in
        // its own row on the dashboard instead of merging it into the local model's.
        var metadata = Client(new FakeA2AClient(), "billing")
            .GetService(typeof(ChatClientMetadata)) as ChatClientMetadata
            ?? Client(new FakeA2AClient(), "billing").Metadata;

        metadata.DefaultModelId.Should().Be("billing");
    }

    [Fact]
    public void Disposing_the_adapter_does_not_throw_on_a_client_that_is_not_disposable()
    {
        var act = () => Client(new FakeA2AClient()).Dispose();

        act.Should().NotThrow();
    }
}
