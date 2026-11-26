using System.Collections.Concurrent;
using System.Diagnostics;
using FluentAssertions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Declarative;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Novior.MentorAgent.Tests.Gating;

/// <summary>
/// Story 1289, slice A — each call to a model reaches the <see cref="IMentorActionObserver"/>s that take them
/// (<see cref="IMentorActionObserver.OnModelCallAsync"/>), with its tokens, the agent, the session, the turn, the user
/// and the tenant: the cost per agent and per user, for the bridge and the console. Never the text.
/// </summary>
public sealed class ModelCallObserverTests
{
    /// <summary>Takes the decisions and the calls to the models, in order.</summary>
    internal sealed class CallObserver : IMentorActionObserver
    {
        private readonly ConcurrentQueue<MentorModelCall> _calls = new();
        private readonly ConcurrentQueue<MentorActionDecision> _decisions = new();

        internal IReadOnlyList<MentorModelCall> Calls => [.. _calls];
        internal IReadOnlyList<MentorActionDecision> Decisions => [.. _decisions];

        public ValueTask OnDecisionAsync(MentorActionDecision decision, CancellationToken cancellationToken)
        {
            _decisions.Enqueue(decision);
            return ValueTask.CompletedTask;
        }

        public ValueTask OnModelCallAsync(MentorModelCall call, CancellationToken cancellationToken)
        {
            _calls.Enqueue(call);
            return ValueTask.CompletedTask;
        }
    }

    private static MentorHostFixture Create(IMentorActionObserver observer, Action<MentorOptions>? configure = null)
        => MentorHostFixture.Create(o =>
        {
            o.MemoryAutoCapture = false;
            configure?.Invoke(o);
        }, s => s.AddMentorActionObserver(observer));

    private static Task FlushAsync(MentorHostFixture host) => host.Get<MentorActionDecisions>().FlushAsync();

    private static ChatResponse ToolCall(MentorHostFixture host, string callId, string tool)
        => new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(callId, tool, null)]))
        {
            ModelId = host.Chat!.ModelId,
            Usage = new UsageDetails { InputTokenCount = 120, OutputTokenCount = 8, CachedInputTokenCount = 100, ReasoningTokenCount = 3 },
        };

    // ── What a call says ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Each_round_trip_of_the_coordinator_is_a_call_with_its_tokens_its_agent_and_who_asked()
    {
        var observer = new CallObserver();
        await using var host = Create(observer);
        host.Auth.SignIn("ada");
        host.Chat!.Respond((_, _) => ToolCall(host, "call-1", "count_open_orders")).SayWithUsage("Three.", 140, 6);

        (await host.RunTurnAsync("how many open orders?")).Should().BeNull();
        await FlushAsync(host);

        var calls = observer.Calls;
        calls.Should().HaveCount(2, "one call for the tool, one for the reply");
        calls.Should().AllSatisfy(c =>
        {
            c.Purpose.Should().Be(MentorModelCallPurpose.Reply);
            c.Outcome.Should().Be(MentorModelCallOutcome.Completed);
            c.Surface.Should().Be(MentorActionSurface.InProcess);
            c.AgentKind.Should().Be(MentorAgentKind.Coordinator);
            c.AgentName.Should().NotBeNullOrWhiteSpace("the coordinator has a name");
            c.User!.Identity!.Name.Should().Be("ada");
            c.UsageMeasured.Should().BeTrue();
            c.ModelId.Should().NotBeNullOrWhiteSpace();
            c.Duration.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
        });
        (calls[0].InputTokens, calls[0].OutputTokens, calls[0].CachedInputTokens, calls[0].ReasoningTokens)
            .Should().Be((120L, 8L, (long?)100, (long?)3));
        (calls[1].InputTokens, calls[1].OutputTokens).Should().Be((140L, 6L));
        calls[1].Timestamp.Should().BeOnOrAfter(calls[0].Timestamp);

        // The same session and turn as the decisions: what puts the two queues back together.
        var decision = observer.Decisions.First(d => d.ToolName == "count_open_orders");
        calls.Should().AllSatisfy(c =>
        {
            c.SessionId.Should().NotBeNullOrEmpty().And.Be(decision.SessionId);
            c.TurnId.Should().NotBeNullOrEmpty().And.Be(decision.TurnId);
            c.AgentName.Should().Be(decision.AgentName ?? c.AgentName);
        });
    }

    [Fact]
    public async Task A_call_carries_no_text_of_the_request_or_the_answer()
    {
        var observer = new CallObserver();
        await using var host = Create(observer);
        host.Chat!.Say("The secret reply is 4711.");

        (await host.RunTurnAsync("tell me the secret 0815")).Should().BeNull();
        await FlushAsync(host);

        var call = observer.Calls.Should().ContainSingle().Subject;
        foreach (var property in typeof(MentorModelCall).GetProperties().Where(p => p.PropertyType == typeof(string)))
            ((string?)property.GetValue(call) ?? "").Should().NotContain("4711").And.NotContain("0815", property.Name);
        call.User.Should().BeNull("nobody signed in");
    }

    [Fact]
    public async Task A_call_that_failed_reaches_the_observers_unmeasured_and_one_stopped_before_it_was_served_is_none()
    {
        var observer = new CallObserver();
        await using var host = Create(observer);

        host.Chat!.ThrowOnNextCall = new InvalidOperationException("provider down");
        await host.RunTurnAsync("first");
        host.Chat.ThrowOnNextCall = new OperationCanceledException();
        await host.RunTurnAsync("second");
        await FlushAsync(host);

        // As the dashboard counts: a call stopped before its first update was not served, nor billed.
        observer.Calls.Select(c => c.Outcome).Should().Equal(MentorModelCallOutcome.Failed);
        observer.Calls.Should().AllSatisfy(c =>
        {
            c.UsageMeasured.Should().BeFalse("the counts are unknown, not zero");
            (c.InputTokens, c.OutputTokens).Should().Be((0L, 0L));
        });
    }

    // ── Who listens ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_observer_that_keeps_the_default_is_not_given_the_calls_and_the_user_is_not_read_for_them()
    {
        // The reads of a turn with no observer at all: what the rest of the turn costs.
        await using var bare = MentorHostFixture.Create(o => o.MemoryAutoCapture = false);
        bare.Chat!.Say("Hello.");
        (await bare.RunTurnAsync("hello")).Should().BeNull();

        var decisionsOnly = new RecordingActionObserver();
        await using var host = Create(decisionsOnly);
        host.Chat!.Say("Hello.");
        host.Get<MentorActionDecisions>().HasObservers.Should().BeTrue();
        host.Get<MentorActionDecisions>().HasCallObservers.Should().BeFalse("its OnModelCallAsync is the interface's, which does nothing");

        (await host.RunTurnAsync("hello")).Should().BeNull();

        host.Auth.Reads.Should().Be(bare.Auth.Reads, "no observer takes the calls: their user is not read for them");

        var calls = new CallObserver();
        await using var listened = Create(calls);
        listened.Chat!.Say("Hello.");
        (await listened.RunTurnAsync("hello")).Should().BeNull();
        listened.Auth.Reads.Should().BeGreaterThan(bare.Auth.Reads, "with an observer of the calls, the call's user is read");
    }

    // ── The agents under the coordinator (slice B) ──────────────────────────────

    private static async Task DelegatedTurnAsync(MentorHostFixture host, string message)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        (await host.RunTurnAsync(message, deadline.Token)).Should().BeNull(host.Logs.WarningText);
        await FlushAsync(host);
    }

    [Fact]
    public async Task A_delegated_turn_attributes_each_call_to_the_coordinator_the_router_or_the_specialist()
    {
        var observer = new CallObserver();
        await using var host = Create(observer);
        host.Auth.SignIn("ada");
        host.Chat!.Respond(ActionDecisionTests.Delegating(host, "where is order 7", "shipping", "get_tracking_status",
            new() { ["orderId"] = 7 }));

        await DelegatedTurnAsync(host, "where is order 7?");

        var calls = observer.Calls;
        calls.Where(c => c.AgentKind == MentorAgentKind.Coordinator).Should().HaveCount(2, "the route, then the reply");
        calls.Where(c => c.AgentKind == MentorAgentKind.Router).Should().NotBeEmpty()
            .And.AllSatisfy(c => c.AgentName.Should().Be("main_coordinator"));
        calls.Where(c => c.AgentKind == MentorAgentKind.Specialist).Should().HaveCount(2, "the tool, then the answer")
            .And.AllSatisfy(c => c.AgentName.Should().Be("ShippingAgent"));

        // Inside the workflow too, the call's own session, turn and user: those of the specialist's decisions.
        var decision = observer.Decisions.First(d => d.ToolName == "get_tracking_status");
        calls.Should().AllSatisfy(c =>
        {
            c.SessionId.Should().Be(decision.SessionId);
            c.TurnId.Should().Be(decision.TurnId);
            c.User!.Identity!.Name.Should().Be("ada");
            c.Purpose.Should().Be(MentorModelCallPurpose.Reply);
        });
        calls.Where(c => c.AgentKind == MentorAgentKind.Specialist).Should().AllSatisfy(c =>
            c.AgentName.Should().Be(decision.AgentName, "the call and the decision name one agent"));
    }

    private sealed class OneTeam(MentorTeamDefinition team) : IMentorTeamSource
    {
        public Task<MentorTeamSourceResult> LoadAsync(MentorTeamSourceContext context, CancellationToken ct = default)
            => Task.FromResult(new MentorTeamSourceResult([team], []));
    }

    [Fact]
    public async Task Each_member_of_a_team_has_its_calls_under_its_own_role()
    {
        var observer = new CallObserver();
        var team = new MentorTeamDefinition
        {
            Name = "Order Review",
            Description = "Reviews an order before anything is done with it.",
            MaxIterations = 4,
            Approver = "Approver",
            Members =
            [
                new MentorTeamMemberDefinition { Role = "Clerk", Instructions = "Look at the order.", Tools = ["count_open_orders"] },
                new MentorTeamMemberDefinition { Role = "Approver", Instructions = "Decide. Reply with the verdict." },
            ],
        };
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false, s =>
        {
            s.AddSingleton<IMentorTeamSource>(new OneTeam(team));
            s.AddMentorActionObserver(observer);
        });
        static bool Is(ChatOptions? options, string role)
            => (options?.Instructions ?? "").Contains($"Your role in the team is: **{role}**", StringComparison.Ordinal);
        host.Chat!.Respond((messages, options) =>
        {
            var answered = messages.LastOrDefault()?.Contents.OfType<FunctionResultContent>().Any() == true;
            ChatResponse Text(string text) => new(new ChatMessage(ChatRole.Assistant, text)) { ModelId = host.Chat.ModelId };
            if (Is(options, "Clerk"))
                return answered ? Text("Seven open orders.") : ToolCall(host, Guid.NewGuid().ToString("N"), "count_open_orders");
            if (Is(options, "Approver")) return Text("Fine. [TEAM:APPROVED]");
            if (options?.Tools?.Any(t => t.Name == "order_review") == true)
                return answered
                    ? Text("relayed")
                    : new(new ChatMessage(ChatRole.Assistant,
                        [new FunctionCallContent("call-team", "order_review", new Dictionary<string, object?> { ["task"] = "review order 7" })]))
                    { ModelId = host.Chat.ModelId };
            return Text("SAFE");
        });

        await DelegatedTurnAsync(host, "review order 7");

        var members = observer.Calls.Where(c => c.AgentKind == MentorAgentKind.TeamMember).ToList();
        members.Where(c => c.AgentName == "Clerk").Should().HaveCount(2, "the tool, then what it found");
        members.Where(c => c.AgentName == "Approver").Should().NotBeEmpty();
        members.Select(c => c.AgentName).Should().OnlyContain(n => n == "Clerk" || n == "Approver");
        observer.Decisions.Where(d => d.ToolName == "count_open_orders").Should().AllSatisfy(d =>
            d.AgentName.Should().Be("Clerk", "the call and the decision name one member"));
    }

    [Fact]
    public async Task The_agents_of_a_source_share_a_client_and_each_call_names_the_agent_running_it()
    {
        var observer = new CallObserver();
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false, s =>
        {
            s.AddMentorActionObserver(observer);
            s.AddMentorAgentDeclarative(d => d.Definitions.Add("""
                kind: GptComponentMetadata
                name: OrdersDesk
                description: Counts and lists the orders.
                instructions: You count orders.
                tools:
                  - kind: function
                    name: count_open_orders
                """));
        });
        host.Chat!.Respond(ActionDecisionTests.Delegating(host, "count the open orders", "orders", "count_open_orders", []));

        await DelegatedTurnAsync(host, "how many open orders?");

        observer.Calls.Where(c => c.AgentKind == MentorAgentKind.AgentSource).Should().HaveCount(2, "the tool, then the answer")
            .And.AllSatisfy(c => c.AgentName.Should().Be("OrdersDesk"));
    }

    [Fact]
    public async Task The_span_of_each_call_names_the_agent_and_the_purpose_and_never_the_user()
    {
        var source = "MentorAgent.Tests.ModelCalls." + Guid.NewGuid().ToString("N");
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == source,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => { lock (spans) spans.Add(a); },
        };
        ActivitySource.AddActivityListener(listener);

        var observer = new CallObserver();
        await using var host = Create(observer, o =>
        {
            o.EnableObservability = true;
            o.ObservabilitySourceName = source;
        });
        host.Auth.SignIn("ada");
        host.Chat!.Respond(ActionDecisionTests.Delegating(host, "where is order 7", "shipping", "get_tracking_status",
            new() { ["orderId"] = 7 }));

        await DelegatedTurnAsync(host, "where is order 7?");

        List<Activity> all;
        lock (spans) all = [.. spans];
        var chats = all.Where(s => s.GetTagItem("gen_ai.operation.name") as string == "chat").ToList();
        // The coordinator and the router of the handoff share a name: the kind tells them apart.
        chats.Select(s => (s.GetTagItem("mentoragent.agent.kind") as string, s.GetTagItem("gen_ai.agent.name") as string)).Distinct()
            .Should().BeEquivalentTo(
                [("coordinator", "main_coordinator"), ("router", "main_coordinator"), ("specialist", "ShippingAgent")],
                "the agents under the coordinator have spans of their own, each with its agent");
        chats.Should().HaveCount(observer.Calls.Count, "one span for each call");
        chats.Should().AllSatisfy(s => s.GetTagItem("mentoragent.model.purpose").Should().Be("reply"));

        all.Except(chats).Should().AllSatisfy(s => s.GetTagItem("gen_ai.agent.name").Should().BeNull(
            "only the span of a call: {0} is not one", s.OperationName));
        all.SelectMany(s => s.TagObjects).Should().NotContain(t => t.Value as string == "ada", "never the user");
    }

    [Fact]
    public async Task Each_call_of_a_delegated_turn_is_counted_once_on_the_dashboard_and_in_the_token_metrics()
    {
        // A client per agent must not be measured inside another: every call the provider answered, once, and only once.
        var source = "MentorAgent.Tests.ModelCalls." + Guid.NewGuid().ToString("N");
        var input = new ConcurrentBag<long>();
        using var meters = new System.Diagnostics.Metrics.MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == source && instrument.Name == "gen_ai.client.token.usage") l.EnableMeasurementEvents(instrument);
            },
        };
        meters.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == "gen_ai.token.type" && tag.Value as string == "input") input.Add(value);
        });
        meters.SetMeasurementEventCallback<int>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == "gen_ai.token.type" && tag.Value as string == "input") input.Add(value);
        });
        meters.Start();

        var observer = new CallObserver();
        await using var host = Create(observer, o =>
        {
            o.EnableObservability = true;
            o.ObservabilitySourceName = source;
        });
        var delegating = ActionDecisionTests.Delegating(host, "where is order 7", "shipping", "get_tracking_status",
            new() { ["orderId"] = 7 });
        host.Chat!.Respond((messages, options) =>
        {
            var response = delegating(messages, options);
            response.Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 2 };
            return response;
        });
        var metrics = host.Get<MentorMetrics>();
        var usage = host.Get<MentorTokenUsage>();
        static (long Requests, long In, long Out) Totals(MentorMetricsSnapshot s)
            => (s.Models.Sum(m => m.RequestCount), s.Models.Sum(m => m.InputTokens), s.Models.Sum(m => m.OutputTokens));
        var before = Totals(metrics.GetSnapshot());
        var (requestsBefore, inBefore) = (usage.RequestCount, usage.TotalInputTokens);

        await DelegatedTurnAsync(host, "where is order 7?");

        var served = host.Chat.Calls.Count;
        served.Should().BeGreaterThanOrEqualTo(5, "the coordinator twice, the router, the specialist twice");
        var after = Totals(metrics.GetSnapshot());
        (after.Requests - before.Requests, after.In - before.In, after.Out - before.Out)
            .Should().Be((served, 10L * served, 2L * served), "the dashboard counts each call the provider served once");
        (usage.RequestCount - requestsBefore, usage.TotalInputTokens - inBefore).Should().Be((served, 10L * served));

        // The GenAI metrics: one input measurement for each traced call — the coordinator's, the router's, the
        // specialist's — and none twice.
        meters.RecordObservableInstruments();
        input.Should().HaveCount(observer.Calls.Count).And.OnlyContain(v => v == 10);
    }

    [Fact]
    public async Task A_call_without_a_span_of_its_own_tags_no_other_span()
    {
        // A listener that takes MentorAgent's spans but not the calls': the span current during a call is then the
        // turn's or a tool's, and it is not the call's. The source's name holds no "MentorAgent": SensitiveDataTests
        // listens to every source that does, and would make the calls' spans while this test runs alongside it.
        var source = "Tests.ModelCallsWithoutSpans." + Guid.NewGuid().ToString("N");
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == source,
            Sample = (ref ActivityCreationOptions<ActivityContext> o) => o.Name.StartsWith("chat", StringComparison.Ordinal)
                ? ActivitySamplingResult.None
                : ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => { lock (spans) spans.Add(a); },
        };
        ActivitySource.AddActivityListener(listener);

        await using var host = Create(new CallObserver(), o =>
        {
            o.EnableObservability = true;
            o.ObservabilitySourceName = source;
        });
        host.Chat!.Respond(ActionDecisionTests.Delegating(host, "where is order 7", "shipping", "get_tracking_status",
            new() { ["orderId"] = 7 }));

        await DelegatedTurnAsync(host, "where is order 7?");

        List<Activity> all;
        lock (spans) all = [.. spans];
        all.Should().Contain(s => s.OperationName == "mentor.tool.get_tracking_status", "the specialist's call ran under it");
        all.Should().AllSatisfy(s =>
        {
            s.GetTagItem("gen_ai.agent.name").Should().BeNull(s.OperationName);
            s.GetTagItem("mentoragent.model.purpose").Should().BeNull(s.OperationName);
        });
    }

    // ── The hub: its user and its tenant ────────────────────────────────────────

    [Fact]
    public async Task On_the_hub_a_call_has_the_connections_user_and_tenant()
    {
        var observer = new CallObserver();
        await using var app = await ServerAppFactory.CreateAsync(
            userId: "ada",
            configure: o => o.MemoryAutoCapture = false,
            configureServices: s =>
            {
                s.AddMentorTenantResolver<TestTenantResolver>();
                s.AddMentorActionObserver(observer);
            });
        app.Chat.Say("Hello Ada.");
        await using var hub = app.CreateHubConnection();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On("StreamingCompleted", () => completed.TrySetResult());
        await hub.StartAsync();

        await hub.InvokeAsync("SendMessage", "hello", null);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await app.Services.GetRequiredService<MentorActionDecisions>().FlushAsync();

        var call = observer.Calls.Should().ContainSingle().Subject;
        call.Surface.Should().Be(MentorActionSurface.Hub);
        call.User!.Identity!.Name.Should().Be("ada");
        call.TenantId.Should().Be("contoso");
    }

    // ── The queue of the calls ───────────────────────────────────────────────────

    /// <summary>Holds every call until let go; takes the decisions at once.</summary>
    private sealed class HeldCalls : IMentorActionObserver
    {
        internal TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ConcurrentQueue<string> Decisions { get; } = new();
        internal ConcurrentQueue<string> Calls { get; } = new();
        internal int Entered => Volatile.Read(ref _entered);
        private int _entered;

        public ValueTask OnDecisionAsync(MentorActionDecision decision, CancellationToken cancellationToken)
        {
            Decisions.Enqueue(decision.ToolName);
            return ValueTask.CompletedTask;
        }

        public async ValueTask OnModelCallAsync(MentorModelCall call, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _entered);
            await Gate.Task;   // ignores the token on purpose
            Calls.Enqueue(call.TurnId!);
        }
    }

    private static MentorModelCall Call(int i) => new()
    {
        Purpose   = MentorModelCallPurpose.Reply,
        Outcome   = MentorModelCallOutcome.Completed,
        Surface   = MentorActionSurface.InProcess,
        TurnId    = $"turn_{i}",
        Timestamp = DateTimeOffset.UtcNow,
    };

    private static MentorActionDecision Decision(int i) => new()
    {
        Outcome     = MentorActionOutcome.Executed,
        ReasonCode  = MentorActionReasons.Completed,
        ToolName    = $"tool_{i}",
        DisplayName = $"Tool {i}",
        Surface     = MentorActionSurface.InProcess,
        Timestamp   = DateTimeOffset.UtcNow,
    };

    private static async Task UntilAsync(Func<bool> condition, string what)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), what);
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task A_full_queue_of_calls_drops_calls_never_a_decision_and_says_so_once()
    {
        var held = new HeldCalls();
        var services = new ServiceCollection();
        services.AddMentorActionObserver(held);
        var logs = new TestLogCollector();
        var logger = new TestLoggerProvider(logs).CreateLogger(typeof(MentorActionDecisions).FullName!);
        var decisions = new MentorActionDecisions(services.BuildServiceProvider(), logger, TimeProvider.System, 2, TimeSpan.FromSeconds(30));

        try
        {
            // The first call held by the reader, then two that fill the queue: the other five are dropped.
            decisions.Publish(Call(0));
            await UntilAsync(() => held.Entered == 1, "the reader holds the first call");
            for (var i = 1; i < 8; i++) decisions.Publish(Call(i));
            // One at a time: the decisions' own queue is as small, and must never fill up here.
            for (var i = 0; i < 8; i++)
            {
                decisions.Publish(Decision(i));
                await UntilAsync(() => held.Decisions.Count == i + 1, "every decision arrives while the calls are held");
            }

            held.Decisions.Should().Equal(Enumerable.Range(0, 8).Select(i => $"tool_{i}"));
            logs.Records.Where(r => r.Level == LogLevel.Warning).Should().ContainSingle("one Warning, not one per call dropped")
                .Which.Message.Should().Contain(nameof(HeldCalls)).And.Contain("is full (2 model calls)");
        }
        finally
        {
            held.Gate.TrySetResult();
        }

        await decisions.FlushAsync();
        decisions.Publish(Call(8));
        await decisions.FlushAsync();
        var back = logs.Records.Should().ContainSingle(r => r.Level == LogLevel.Information).Subject;
        back.Message.Should().Contain("receiving model calls again").And.Contain($"; {9 - held.Calls.Count} were dropped");
        held.Calls.Last().Should().Be("turn_8");
        await decisions.DisposeAsync();
    }
}
