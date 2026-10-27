using System.Text.RegularExpressions;
using A2A;
using FluentAssertions;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Tests.App;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Novior.MentorAgent.Tests.Federation;

/// <summary>
/// BUG-210 — the caller's half of BUG-199. A MentorAgent that had delegated a request to a peer over A2A and was stopped
/// ended its own turn and told the peer nothing: the peer finished the task, actions that ask for no confirmation
/// included. Found live on the samples' A2A pair, where the peer's task ended Completed six seconds after the Stop.
/// The caller here is the library's own A2A client and the peer a MentorAgent server: a peer that streams, as every
/// MentorAgent does, and one whose card declares no streaming, which is asked without a stream (BUG-197). The handoff
/// workflow reads every agent streaming and hands it the turn's token, so a Stop reaches the client as that token.
/// </summary>
public sealed class A2ACallerStopTests
{
    private static Task<ServerAppFactory> PeerAsync()
        => ServerAppFactory.CreateAsync(
            configure: o =>
            {
                o.A2AServerEnabled = true;
                o.A2AServerUrl = "http://localhost/a2a";
                o.MemoryAutoCapture = false;
            },
            configureServices: s => s.AddSingleton<TestOrderService>(),   // one instance, so the test reads what the task ran
            configureApp: a => a.UseEndpoints(e => e.MapMentorAgentA2A()));

    /// <summary>
    /// The caller's client for the peer, built from its card as the remote agents are, and a second client to read the
    /// peer's tasks with. A peer that does not stream publishes a card that says so; here it is the same server.
    /// </summary>
    private static async Task<(A2AChatClient Caller, IA2AClient Reader)> ConnectAsync(
        ServerAppFactory peer, bool streamed, TestLogCollector callerLog)
    {
        var card = await new A2ACardResolver(new Uri("http://localhost"), peer.Client).GetAgentCardAsync();
        card.Capabilities!.Streaming.Should().BeTrue("a MentorAgent peer streams");
        var reader = A2AClientFactory.Create(card, peer.Client);

        if (!streamed)
            card.Capabilities.Streaming = false;
        var logger = new TestLoggerProvider(callerLog).CreateLogger("caller");
        return (A2AChatClient.ForCard(card, A2AClientFactory.Create(card, peer.Client), "PeerInstance", logger), reader);
    }

    /// <summary>The delegated request, read as the handoff workflow reads an agent: streaming.</summary>
    private static async Task<string> DelegateAsync(A2AChatClient caller, CancellationToken ct)
    {
        var text = new System.Text.StringBuilder();
        await foreach (var update in caller.GetStreamingResponseAsync(
                           [new ChatMessage(ChatRole.User, "how many open orders are there?")], cancellationToken: ct))
            text.Append(update.Text);
        return text.ToString();
    }

    private static async Task Until(Func<bool> condition, string what)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition())
        {
            if (deadline.IsCancellationRequested) throw new TimeoutException($"Waited 15 s for {what}.");
            await Task.Delay(20);
        }
    }

    /// <summary>The id of the one task the peer received, from its handler's log.</summary>
    private static string TaskId(ServerAppFactory peer)
        => peer.Logs.Records.Select(r => Regex.Match(r.Message, @"Task '([0-9a-f]+)' received"))
               .Single(m => m.Success).Groups[1].Value;

    /// <summary>The peer's turn has ended: an action ran, or the handler wrote how the task ended without one.</summary>
    private static bool Ended(ServerAppFactory peer, string taskId)
        => peer.Services.GetRequiredService<TestOrderService>().Invoked.Count > 0
           || peer.Logs.Records.Any(r => r.Message.Contains(taskId, StringComparison.Ordinal)
                                      && (r.Message.Contains("the caller cancelled it", StringComparison.Ordinal)
                                          || r.Message.Contains("produced no reply", StringComparison.Ordinal)));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]   // up to 1.1.0, in both cases, the peer ran the action and the task ended Completed
    public async Task A_stop_during_the_delegation_cancels_the_peers_task_and_no_action_runs(bool streamed)
    {
        await using var peer = await PeerAsync();
        var model = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.Chat.BeforeRespond = () => model.Task;
        peer.Chat.CallTool("count_open_orders").Say("Two open orders.");
        var callerLog = new TestLogCollector();
        var (caller, reader) = await ConnectAsync(peer, streamed, callerLog);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);

        var delegation = DelegateAsync(caller, stop.Token);
        await Until(() => peer.Chat.CallCount > 0, "the peer's turn to reach its model");
        var taskId = TaskId(peer);

        stop.Cancel();   // the user's Stop
        // Released either way: a caller still waiting for the peer would otherwise wait for this model for ever.
        var stoppedAlone = await Task.WhenAny(delegation, Task.Delay(TimeSpan.FromSeconds(5), deadline.Token)) == delegation;
        model.SetResult();   // the peer's model answers, with a tool call
        await Until(() => Ended(peer, taskId), "the peer's turn to end");

        var said = string.Join(Environment.NewLine, callerLog.Records.Select(r => $"[{r.Level}] {r.Message}"))
                   + Environment.NewLine + callerLog.ExceptionText;
        stoppedAlone.Should().BeTrue("the stopped turn does not wait for the peer to finish");
        await FluentActions.Awaiting(() => delegation).Should().ThrowAsync<OperationCanceledException>();
        callerLog.Records.Should().Contain(r => r.Message.Contains($"Task '{taskId}' of the remote agent 'PeerInstance' cancelled",
            StringComparison.Ordinal), $"the caller cancels the task it delegated; it logged:{Environment.NewLine}{said}");
        (await reader.GetTaskAsync(new GetTaskRequest { Id = taskId }, deadline.Token)).Status.State
            .Should().Be(TaskState.Canceled, "nobody waits for the task once the turn that delegated it is stopped");
        peer.Services.GetRequiredService<TestOrderService>().Invoked.Should().BeEmpty("the peer runs nothing after the Stop");
        peer.Logs.Records.Should().Contain(r => r.Message.Contains($"Task '{taskId}' cancelled by the caller", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_delegation_nobody_stops_returns_the_peers_answer_and_cancels_nothing(bool streamed)
    {
        await using var peer = await PeerAsync();
        peer.Chat.CallTool("count_open_orders").Say("Two open orders.");
        var callerLog = new TestLogCollector();
        var (caller, reader) = await ConnectAsync(peer, streamed, callerLog);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var answer = await DelegateAsync(caller, deadline.Token);

        answer.Should().Contain("Two open orders");
        answer.Should().NotContain("Processing", "the peer's working-state note is not the answer");
        (await reader.GetTaskAsync(new GetTaskRequest { Id = TaskId(peer) }, deadline.Token)).Status.State
            .Should().Be(TaskState.Completed);
        peer.Services.GetRequiredService<TestOrderService>().Invoked.Should().ContainSingle();
        peer.Logs.Records.Should().NotContain(r => r.Message.Contains("cancelled by the caller", StringComparison.Ordinal));
        callerLog.Records.Should().NotContain(r => r.Message.Contains("cancel", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// BUG-211 — found by the live check of this fix: a Stop during the first message of a session, while the
    /// coordinator read a remote agent's card, was logged as the agent not answering and kept it from the cache for a
    /// minute; and the coordinator was built without it, so the session never had the agent. The peer here takes the
    /// connection and never answers, so the Stop always lands while the card is read.
    /// </summary>
    [Fact]
    public async Task A_stop_while_a_remote_agents_card_is_read_leaves_the_agent_for_the_next_message()
    {
        using var silent = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        silent.Start();
        var held = new System.Collections.Concurrent.ConcurrentBag<System.Net.Sockets.TcpClient>();
        _ = Task.Run(async () =>
        {
            try { while (true) held.Add(await silent.AcceptTcpClientAsync()); }
            catch (Exception) { /* the listener stopped */ }
        });
        var url = $"http://127.0.0.1:{((System.Net.IPEndPoint)silent.LocalEndpoint).Port}";
        await using var host = MentorHostFixture.Create(o =>
        {
            o.MemoryAutoCapture = false;
            o.RemoteAgents = [new Novior.MentorAgent.Models.MentorRemoteAgent { Name = "PeerInstance", Description = "The other deployment", AgentCardUrl = url }];
        });
        host.Chat!.Say("Hello.").Say("Hello again.");

        var first = host.RunTurnAsync("hello");
        await Until(() => !held.IsEmpty, "the card to be asked for");
        host.Orchestrator.CancelCurrentRequest();   // the user's Stop
        (await first).Should().BeNull("a Stop during the first message is a clean exit");

        // The peer answers from now on.
        await FluentActions.Awaiting(() => host.Get<A2ACardCache>().GetAsync($"PeerInstance|{url}|", "PeerInstance", url,
                _ => Task.FromResult(new AgentCard
                {
                    Name = "PeerInstance", Version = "1.0.0",
                    SupportedInterfaces = [new AgentInterface { Url = url + "/a2a", ProtocolBinding = ProtocolBindingNames.JsonRpc, ProtocolVersion = "1.0" }],
                }), default))
            .Should().NotThrowAsync("a Stop is not remembered as an agent that does not answer");
        (await host.RunTurnAsync("hello again")).Should().BeNull();

        host.Logs.Records.Count(r => r.Message.Contains("Remote agent 'PeerInstance' connected from", StringComparison.Ordinal))
            .Should().Be(1, "the next message builds the coordinator with the agent");
        host.Logs.HasWarning("did not answer").Should().BeFalse();
        silent.Stop();
        foreach (var c in held) c.Dispose();
    }
}
