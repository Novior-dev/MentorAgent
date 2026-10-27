using System.Text.RegularExpressions;
using A2A;
using FluentAssertions;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.App;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Novior.MentorAgent.Tests.Federation;

/// <summary>
/// BUG-199 — a caller that cancelled an A2A task was answered <c>Working</c>, and the task never ended: the SDK
/// cancels the token of the task's turn and then calls <c>CancelAsync</c>, which MentorAgent replaced with a Debug line,
/// so nothing moved the task to <c>Canceled</c>. And the call in progress read <c>session-ended</c>, because the turn
/// was cancelled before anything said it was the caller's Stop. Cancelling now stops the turn as Stop does, before the
/// SDK cancels anything, and the task ends <c>Canceled</c>.
/// </summary>
public sealed class A2ACancelTests
{
    private static Task<ServerAppFactory> HostAsync(RecordingActionObserver observer, TestActionPolicy? policy = null)
        => ServerAppFactory.CreateAsync(
            configure: o =>
            {
                o.A2AServerEnabled = true;
                o.A2AServerUrl = "http://localhost/a2a";
                o.MemoryAutoCapture = false;
                o.ActionPolicyTimeout = TimeSpan.FromSeconds(30);
            },
            configureServices: s =>
            {
                s.AddMentorActionObserver(observer);
                s.AddSingleton<TestOrderService>();   // one instance, so the test reads what the task's scope ran
                if (policy is not null) s.AddMentorActionPolicy(policy);
            },
            configureApp: a => a.UseEndpoints(e => e.MapMentorAgentA2A()));

    private static async Task<IA2AClient> ClientAsync(ServerAppFactory app)
        => A2AClientFactory.Create(await new A2ACardResolver(new Uri("http://localhost"), app.Client).GetAgentCardAsync(), app.Client);

    private static SendMessageRequest Request(string text, bool returnImmediately) => new()
    {
        Message = new Message
        {
            Role      = Role.User,
            MessageId = Guid.NewGuid().ToString("N"),
            Parts     = [Part.FromText(text)],
        },
        Configuration = returnImmediately ? new SendMessageConfiguration { ReturnImmediately = true } : null,
    };

    /// <summary>How the caller asks: streamed, as a MentorAgent caller does; told the task at once; or waiting for the reply.</summary>
    public enum Ask { Streamed, ReturnImmediately, Blocking }

    /// <summary>
    /// Starts a task and returns its id while its turn runs on. Streamed, the stream is kept open in the background; a
    /// blocking request too, and its task's id is read from the handler's log, as the caller would have it from another
    /// channel.
    /// </summary>
    private static async Task<string> StartAsync(ServerAppFactory app, IA2AClient client, string text, Ask ask, CancellationToken ct)
    {
        if (ask == Ask.ReturnImmediately)
            return (await client.SendMessageAsync(Request(text, returnImmediately: true), ct)).Task?.Id
                   ?? throw new InvalidOperationException("The server answered with a message, not a task.");

        if (ask == Ask.Blocking)
        {
            var known = app.Logs.Records.Count;
            _ = Task.Run(() => client.SendMessageAsync(Request(text, returnImmediately: false), ct), ct);
            string? taskId = null;
            await Until(() => (taskId = app.Logs.Records.Skip(known)
                .Select(r => Regex.Match(r.Message, @"Task '([0-9a-f]+)' received")).FirstOrDefault(m => m.Success)?.Groups[1].Value) is not null,
                "the task to be received");
            return taskId!;
        }

        var id = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var e in client.SendStreamingMessageAsync(Request(text, returnImmediately: false), ct))
                    if ((e.Task?.Id ?? e.StatusUpdate?.TaskId) is { } taskId) id.TrySetResult(taskId);
            }
            catch (Exception ex) { id.TrySetException(ex); }
        }, ct);
        return await id.Task.WaitAsync(ct);
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

    /// <summary>The task's turn has ended: an action ran, or the handler wrote how the task ended without one.</summary>
    private static bool Ended(ServerAppFactory app, string taskId)
        => app.Services.GetRequiredService<TestOrderService>().Invoked.Count > 0
           || app.Logs.Records.Any(r => r.Message.Contains(taskId, StringComparison.Ordinal)
                                     && (r.Message.Contains("the caller cancelled it", StringComparison.Ordinal)
                                         || r.Message.Contains("produced no reply", StringComparison.Ordinal)));

    private static async Task<TaskState> StateAsync(IA2AClient client, string taskId, CancellationToken ct)
        => (await client.GetTaskAsync(new GetTaskRequest { Id = taskId }, ct)).Status.State;

    [Theory]
    [InlineData(Ask.Streamed)]
    [InlineData(Ask.ReturnImmediately)]
    [InlineData(Ask.Blocking)]   // up to 1.1.0 the action ran, and the task ended Completed
    public async Task A_task_cancelled_while_the_model_thinks_ends_Canceled_and_no_action_runs(Ask ask)
    {
        var observer = new RecordingActionObserver();
        await using var app = await HostAsync(observer);
        var model = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Chat.BeforeRespond = () => model.Task;
        app.Chat.CallTool("count_open_orders").Say("Two open orders.");
        var client = await ClientAsync(app);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var taskId = await StartAsync(app, client, "how many open orders are there?", ask, deadline.Token);
        await Until(() => app.Chat.CallCount > 0, "the turn to reach the model");

        var cancelled = await client.CancelTaskAsync(new CancelTaskRequest { Id = taskId }, deadline.Token);
        model.SetResult();   // the model answers, with a tool call
        await Until(() => Ended(app, taskId), "the turn to end");

        cancelled.Status.State.Should().Be(TaskState.Canceled, "tasks/cancel answers with the task it cancelled");
        app.Services.GetRequiredService<TestOrderService>().Invoked.Should().BeEmpty("nothing starts after the cancel");
        (await StateAsync(client, taskId, deadline.Token)).Should().Be(TaskState.Canceled,
            "the turn that was stopped does not end the task a second time");
        app.Logs.Records.Should().Contain(r => r.Message.Contains($"Task '{taskId}' cancelled by the caller", StringComparison.Ordinal));
        app.Logs.HasWarning("produced no reply").Should().BeFalse("a cancelled task is not a failed one");
    }

    [Fact]
    public async Task The_call_in_progress_when_the_task_is_cancelled_is_stopped_and_the_observers_read_stop()
    {
        // The call is in progress: a policy is deciding about it when the cancel comes. The SDK cancels the turn's token
        // before it tells the handler, so the call read session-ended.
        var observer = new RecordingActionObserver();
        var deciding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release  = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool? stopKnownWhenCancelled = null;
        var policy = new TestActionPolicy
        {
            Before = async (request, _) =>
            {
                if (request.Stage != MentorActionStage.Call) return;
                // Read at the very moment the turn is cancelled: whether it already knew it was the caller's Stop. The
                // decision itself is written a moment later, and by then CancelAsync has usually said so too. The turn's
                // own token, not the policy's: the policy's is disposed as soon as its wait gives up.
                var turn = request.Services.GetRequiredService<MentorTurnContext>();
                turn.Cancellation.Register(() => stopKnownWhenCancelled = turn.StopRequested);
                deciding.TrySetResult();
                await release.Task;
            },
        };
        await using var app = await HostAsync(observer, policy);
        app.Chat.CallTool("count_open_orders").Say("Two open orders.");
        var client = await ClientAsync(app);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var taskId = await StartAsync(app, client, "how many open orders are there?", Ask.Streamed, deadline.Token);
        await deciding.Task.WaitAsync(deadline.Token);

        var cancelled = await client.CancelTaskAsync(new CancelTaskRequest { Id = taskId }, deadline.Token);
        release.SetResult();
        await Until(() => Ended(app, taskId), "the turn to end");
        await app.Services.GetRequiredService<MentorActionDecisions>().FlushAsync();

        cancelled.Status.State.Should().Be(TaskState.Canceled);
        app.Services.GetRequiredService<TestOrderService>().Invoked.Should().BeEmpty();
        observer.Steps("count_open_orders").Should().Equal(["Stopped/stop"], "the caller's cancel is a Stop");
        stopKnownWhenCancelled.Should().BeTrue("the turn is told it is the caller's Stop before the SDK cancels anything");
        observer.Seen.Should().AllSatisfy(d => d.Surface.Should().Be(MentorActionSurface.A2A));
    }

    [Fact]
    public async Task A_task_cancelled_while_its_reply_streams_is_not_put_to_the_model_again()
    {
        // A reply written with no tool is put once more for a grounded answer. Cancelled while it streams, the turn has
        // some words and no tool: the second attempt would be a turn the caller never asked for. Waiting for the reply,
        // the SDK does not cancel the token itself.
        var observer = new RecordingActionObserver();
        await using var app = await HostAsync(observer);
        app.Chat.ChunkDelay = TimeSpan.FromMilliseconds(100);
        app.Chat.Say("There are two open orders at the moment, both of them placed earlier this week.");
        var client = await ClientAsync(app);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var taskId = await StartAsync(app, client, "how many open orders are there?", Ask.Blocking, deadline.Token);
        await Until(() => app.Chat.CallCount == 1, "the reply to start streaming");
        await Task.Delay(250);   // a few words out

        await client.CancelTaskAsync(new CancelTaskRequest { Id = taskId }, deadline.Token);
        await Until(() => Ended(app, taskId), "the turn to end");
        await Task.Delay(300);   // time for a second attempt to show

        app.Chat.CallCount.Should().Be(1, "a cancelled task is not put to the model a second time");
        (await StateAsync(client, taskId, deadline.Token)).Should().Be(TaskState.Canceled);
    }

    [Fact]
    public async Task A_task_cancelled_during_its_second_attempt_is_not_judged()
    {
        // The second attempt ends without a tool too, and a model is asked whether the reply states application data.
        // Cancelled while the second attempt streams, that question is not asked.
        var observer = new RecordingActionObserver();
        await using var app = await HostAsync(observer);
        app.Chat.ChunkDelay = TimeSpan.FromMilliseconds(100);
        app.Chat.Say("Two.").Say("There are two open orders at the moment, both of them placed earlier this week.");
        var client = await ClientAsync(app);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var taskId = await StartAsync(app, client, "how many open orders are there?", Ask.Streamed, deadline.Token);
        await Until(() => app.Chat.CallCount == 2, "the second attempt to start streaming");
        await Task.Delay(250);

        await client.CancelTaskAsync(new CancelTaskRequest { Id = taskId }, deadline.Token);
        await Until(() => Ended(app, taskId), "the turn to end");
        await Task.Delay(300);

        app.Chat.CallCount.Should().Be(2, "nobody judges the reply of a cancelled task");
        app.Logs.ExceptionText.Should().BeEmpty();
        (await StateAsync(client, taskId, deadline.Token)).Should().Be(TaskState.Canceled);
    }

    [Fact]
    public async Task Cancelling_one_task_leaves_another_running()
    {
        var observer = new RecordingActionObserver();
        await using var app = await HostAsync(observer);
        var model = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Chat.BeforeRespond = () => model.Task;
        app.Chat.CallTool("count_open_orders").Say("Two open orders.");
        var client = await ClientAsync(app);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var first = await StartAsync(app, client, "how many open orders are there?", Ask.Streamed, deadline.Token);
        await Until(() => app.Chat.CallCount == 1, "the first turn to reach the model");
        var second = await StartAsync(app, client, "and how many are open now?", Ask.Streamed, deadline.Token);
        await Until(() => app.Chat.CallCount == 2, "the second turn to reach the model");

        await client.CancelTaskAsync(new CancelTaskRequest { Id = first }, deadline.Token);
        model.SetResult();
        await Until(() => StateAsync(client, second, deadline.Token).GetAwaiter().GetResult()
                          is TaskState.Completed or TaskState.Failed, "the other task to end");

        (await StateAsync(client, first, deadline.Token)).Should().Be(TaskState.Canceled);
        (await StateAsync(client, second, deadline.Token)).Should().Be(TaskState.Completed);
        app.Services.GetRequiredService<TestOrderService>().Invoked.Should().ContainSingle("the other task ran its action");
    }
}
