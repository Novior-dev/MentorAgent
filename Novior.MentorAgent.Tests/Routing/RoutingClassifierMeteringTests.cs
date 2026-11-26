using FluentAssertions;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.AI;
using Xunit;

namespace Novior.MentorAgent.Tests.Routing;

/// <summary>
/// The routing classifier's calls are billed like any other: the dashboard and the session's counter count them.
/// </summary>
public sealed class RoutingClassifierMeteringTests
{
    [Theory]
    [InlineData(MentorRoutingStrategy.Classifier)]
    [InlineData(MentorRoutingStrategy.Cascade)]
    public async Task Every_call_of_a_routed_turn_is_counted_the_classifiers_included(MentorRoutingStrategy strategy)
    {
        var strong = new FakeChatClient { ModelId = "strong-model" };
        strong.Respond((_, _) => Reply("ok"));
        await using var host = MentorHostFixture.Create(o =>
        {
            o.MemoryAutoCapture = false;
            o.StrongChatClient = strong;
            o.RoutingStrategy = strategy;
        });
        // The classifier's calls have no tools: SIMPLE to the router's question, COMPLETE to the judge's.
        host.Chat!.Respond((messages, options) => options?.Tools is { Count: > 0 }
            ? Reply("ok")
            : Reply(messages.Any(m => m.Text.Contains("COMPLETE or INCOMPLETE", StringComparison.Ordinal)) ? "COMPLETE" : "SIMPLE"));
        var usage = host.Get<MentorTokenUsage>();
        var metrics = host.Get<MentorMetrics>();
        static long Requests(MentorMetricsSnapshot s) => s.Models.Sum(m => m.RequestCount);
        var before = (usage.RequestCount, usage.TotalInputTokens);
        var dashboardBefore = Requests(metrics.GetSnapshot());

        (await host.RunTurnAsync("hello")).Should().BeNull();

        var served = host.Chat.Calls.Count + strong.Calls.Count;
        served.Should().BeGreaterThanOrEqualTo(2, "the routing classifier and the reply");
        (usage.RequestCount - before.RequestCount, usage.TotalInputTokens - before.TotalInputTokens)
            .Should().Be((served, 10L * served), "every call the provider served is billed, and counted once");
        (Requests(metrics.GetSnapshot()) - dashboardBefore).Should().Be(served, "the dashboard's rows count them too");
    }

    [Fact]
    public async Task A_turn_with_Cascade_asks_the_classifier_once_and_never_runs_the_cheap_model_to_judge_it()
    {
        // A turn streams, and a stream cannot be taken back: Cascade decides before, with the classifier's question.
        // The cheap answer judged after the fact, then dropped, is for a call that does not stream — none in a turn.
        var strong = new FakeChatClient { ModelId = "strong-model" };
        strong.Respond((_, _) => Reply("ok"));
        await using var host = MentorHostFixture.Create(o =>
        {
            o.MemoryAutoCapture = false;
            o.StrongChatClient = strong;
            o.RoutingStrategy = MentorRoutingStrategy.Cascade;
        });
        host.Chat!.Respond((_, options) => Reply(options?.Tools is { Count: > 0 } ? "ok" : "SIMPLE"));

        (await host.RunTurnAsync("hello")).Should().BeNull();

        var questions = host.Chat.Calls.Concat(strong.Calls)
            .Where(c => c.ToolNames.Count == 0)
            .Select(c => string.Join(" ", c.Messages.Select(m => m.Text)))
            .ToList();
        questions.Should().ContainSingle("one question a turn").Which.Should().Contain("COMPLEX or SIMPLE");
        questions.Should().NotContain(q => q.Contains("COMPLETE or INCOMPLETE"), "no answer is judged, then dropped");
    }

    [Fact]
    public async Task With_observability_the_routing_classifiers_call_has_its_span()
    {
        var source = "MentorAgent.Tests.Routing." + Guid.NewGuid().ToString("N");
        var spans = new List<System.Diagnostics.Activity>();
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = s => s.Name == source,
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _)
                => System.Diagnostics.ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => { lock (spans) spans.Add(a); },
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);

        var strong = new FakeChatClient { ModelId = "strong-model" };
        strong.Respond((_, _) => Reply("ok"));
        await using var host = MentorHostFixture.Create(o =>
        {
            o.MemoryAutoCapture = false;
            o.StrongChatClient = strong;
            o.RoutingStrategy = MentorRoutingStrategy.Classifier;
            o.EnableObservability = true;
            o.ObservabilitySourceName = source;
        });
        host.Chat!.Respond((_, options) => Reply(options?.Tools is { Count: > 0 } ? "ok" : "SIMPLE"));

        (await host.RunTurnAsync("hello")).Should().BeNull();

        int chats;
        lock (spans) chats = spans.Count(s => s.GetTagItem("gen_ai.operation.name") as string == "chat");
        chats.Should().Be(host.Chat.Calls.Count + strong.Calls.Count, "one span for each call, the classifier's included");
    }

    private static ChatResponse Reply(string text) => new(new ChatMessage(ChatRole.Assistant, text))
    {
        ModelId = "fake-model",
        Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 2 },
    };
}
