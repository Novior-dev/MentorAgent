using System.Collections.Concurrent;
using System.Diagnostics;
using FluentAssertions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Novior.MentorAgent.Tests.Gating;

/// <summary>
/// Story 180 — how the gate's decisions reach the observers: every observer, in order, and never on the action's
/// path. An observer that is slow, hangs or throws delays nothing and stops nothing, and the others still get
/// everything: each observer has a queue and a reader of its own.
/// </summary>
public sealed class ActionObserverDeliveryTests
{
    private static (MentorActionDecisions Decisions, TestLogCollector Logs) Build(
        TimeSpan timeout, int capacity, params IMentorActionObserver[] observers)
    {
        var services = new ServiceCollection();
        foreach (var observer in observers) services.AddMentorActionObserver(observer);
        var logs = new TestLogCollector();
        var logger = new TestLoggerProvider(logs).CreateLogger(typeof(MentorActionDecisions).FullName!);
        return (new MentorActionDecisions(services.BuildServiceProvider(), logger, TimeProvider.System, capacity, timeout), logs);
    }

    private static MentorActionDecision Decision(int i) => new()
    {
        Outcome     = MentorActionOutcome.Executed,
        ReasonCode  = MentorActionReasons.Completed,
        ToolName    = $"tool_{i}",
        DisplayName = $"Tool {i}",
        Surface     = MentorActionSurface.InProcess,
        Timestamp   = DateTimeOffset.UtcNow,
    };

    /// <summary>Publishes on another thread: a publisher that waited for an observer fails the test instead of hanging it.</summary>
    private static async Task PublishAsync(MentorActionDecisions decisions, params int[] numbers)
    {
        var publishing = Task.Run(() => { foreach (var i in numbers) decisions.Publish(Decision(i)); });
        (await Task.WhenAny(publishing, Task.Delay(TimeSpan.FromSeconds(2)))).Should().BeSameAs(publishing,
            "Publish only queues: the action never waits for an observer");
    }

    private static async Task UntilAsync(Func<bool> condition, string what)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            deadline.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), what);
            await Task.Delay(10);
        }
    }

    /// <summary>Waits on a gate the test opens: a slow observer, or one that hangs.</summary>
    private sealed class HeldObserver : IMentorActionObserver
    {
        internal TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ConcurrentQueue<string> Seen { get; } = new();

        public async ValueTask OnDecisionAsync(MentorActionDecision decision, CancellationToken cancellationToken)
        {
            await Gate.Task;   // ignores the token on purpose: an observer that does not honour it
            Seen.Enqueue(decision.ToolName);
        }
    }

    /// <summary>Throws on the calls it is told to, by number from 1.</summary>
    private sealed class ThrowingObserver(params int[] failOn) : IMentorActionObserver
    {
        private int _calls;

        public ValueTask OnDecisionAsync(MentorActionDecision decision, CancellationToken cancellationToken)
            => failOn.Contains(Interlocked.Increment(ref _calls))
                ? throw new InvalidOperationException("mario.rossi@example.com")
                : ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Two_observers_each_receive_every_decision_in_order()
    {
        var first = new RecordingActionObserver();
        var second = new RecordingActionObserver();
        var (decisions, _) = Build(TimeSpan.FromSeconds(5), 100, first, second);

        for (var i = 0; i < 20; i++) decisions.Publish(Decision(i));
        await decisions.FlushAsync();

        var expected = Enumerable.Range(0, 20).Select(i => $"tool_{i}").ToList();
        first.Seen.Select(d => d.ToolName).Should().Equal(expected);
        second.Seen.Select(d => d.ToolName).Should().Equal(expected);
    }

    [Fact]
    public async Task An_observer_that_throws_is_logged_once_and_the_others_still_receive_everything()
    {
        var thrower = new ThrowingObserver(1, 2, 3, 5);
        var recorder = new RecordingActionObserver();
        var (decisions, logs) = Build(TimeSpan.FromSeconds(5), 100, thrower, recorder);

        for (var i = 0; i < 3; i++) decisions.Publish(Decision(i));
        await decisions.FlushAsync();

        recorder.Seen.Should().HaveCount(3);
        logs.Records.Where(r => r.Level == LogLevel.Warning).Should().ContainSingle(
                "a Warning when it starts failing, Debug while it keeps failing")
            .Which.Message.Should().Contain(nameof(ThrowingObserver)).And.Contain("threw (InvalidOperationException)");
        logs.Records.Should().Contain(r => r.Level == LogLevel.Debug && r.Message.Contains("again"));
        logs.Records.Where(r => r.Level >= LogLevel.Information)
            .Should().NotContain(r => r.Message.Contains("mario.rossi"), "the type only: the message can carry anything");

        // It answers again, then fails again: that is a new Warning.
        decisions.Publish(Decision(3));   // its fourth call answers
        decisions.Publish(Decision(4));   // the fifth fails
        await decisions.FlushAsync();
        logs.Records.Count(r => r.Level == LogLevel.Warning).Should().Be(2, "it had answered in between");
        recorder.Seen.Should().HaveCount(5);
    }

    [Fact]
    public async Task A_slow_observer_holds_neither_the_publisher_nor_the_other_observers()
    {
        var slow = new HeldObserver();
        var recorder = new RecordingActionObserver();
        var (decisions, _) = Build(TimeSpan.FromSeconds(30), 100, slow, recorder);

        try
        {
            await PublishAsync(decisions, 0, 1, 2);

            await UntilAsync(() => recorder.Seen.Count == 3, "the other observer gets everything meanwhile");
            slow.Seen.Should().BeEmpty();
        }
        finally
        {
            slow.Gate.TrySetResult();
        }

        await decisions.FlushAsync();
        slow.Seen.Should().Equal("tool_0", "tool_1", "tool_2");
    }

    [Fact]
    public async Task A_call_past_the_timeout_is_no_longer_awaited()
    {
        var hung = new HeldObserver();
        var (decisions, logs) = Build(TimeSpan.FromMilliseconds(100), 100, hung);

        try
        {
            await PublishAsync(decisions, 0, 1);
            await decisions.FlushAsync();   // both delivered, both given up after 100 ms
        }
        finally
        {
            hung.Gate.TrySetResult();
        }

        logs.Records.Where(r => r.Level == LogLevel.Warning).Should().ContainSingle()
            .Which.Message.Should().Contain("did not answer within 0.1 s");
        logs.Records.Should().Contain(r => r.Level == LogLevel.Debug && r.Message.Contains("did not answer within 0.1 s again"));
    }

    [Fact]
    public async Task A_full_queue_drops_for_that_observer_only_and_says_how_many()
    {
        var held = new HeldObserver();
        var recorder = new RecordingActionObserver();
        var (decisions, logs) = Build(TimeSpan.FromSeconds(30), 2, held, recorder);

        try
        {
            // One at a time, so the other observer's queue, as small, never fills up: only the held one's does.
            for (var i = 0; i < 8; i++)
            {
                await PublishAsync(decisions, i);
                await UntilAsync(() => recorder.Seen.Count == i + 1, "the other observer keeps receiving");
            }

            logs.Records.Where(r => r.Level == LogLevel.Warning).Should().ContainSingle()
                .Which.Message.Should().Contain(nameof(HeldObserver)).And.Contain("is full (2 decisions)");
        }
        finally
        {
            held.Gate.TrySetResult();
        }

        await decisions.FlushAsync();
        decisions.Publish(Decision(8));
        await decisions.FlushAsync();

        var back = logs.Records.Should().ContainSingle(r => r.Level == LogLevel.Information).Subject;
        back.Message.Should().Contain("receiving decisions again");
        var delivered = held.Seen.Count;
        back.Message.Should().Contain($"; {9 - delivered} were dropped");
        held.Seen.Last().Should().Be("tool_8");
    }

    [Fact]
    public void Without_observers_nobody_listens()
    {
        var (decisions, _) = Build(TimeSpan.FromSeconds(5), 100);

        decisions.HasObservers.Should().BeFalse();
        decisions.Publish(Decision(0));   // nothing to do, nothing thrown
    }
}
