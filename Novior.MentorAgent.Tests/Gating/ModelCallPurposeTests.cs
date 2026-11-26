using System.Diagnostics;
using FluentAssertions;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using CallObserver = Novior.MentorAgent.Tests.Gating.ModelCallObserverTests.CallObserver;

namespace Novior.MentorAgent.Tests.Gating;

/// <summary>
/// Story 1289, slice C — every call MentorAgent makes on its own, the checks around a reply, reaches the observers with
/// what it was for (<see cref="MentorModelCall.Purpose"/>), attributed to no agent, in the turn it served.
/// </summary>
public sealed class ModelCallPurposeTests
{
    /// <summary>A reply to the coordinator's requests, the ones with tools; a word to everything else.</summary>
    private static ChatResponse Answer(MentorHostFixture host, ChatOptions? options) =>
        new(new ChatMessage(ChatRole.Assistant, options?.Tools is { Count: > 0 } ? "Here you are." : "SAFE"))
        {
            ModelId = host.Chat!.ModelId,
            Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 2 },
        };

    public static TheoryData<MentorModelCallPurpose> TurnChecks =>
    [
        MentorModelCallPurpose.SafetyCheck,
        MentorModelCallPurpose.RefusalCheck,
        MentorModelCallPurpose.OutputSafetyCheck,
        MentorModelCallPurpose.AnnouncedActionCheck,
        MentorModelCallPurpose.MemoryExtraction,
        MentorModelCallPurpose.RoutingCheck,
    ];

    private static void Configure(MentorOptions o, MentorModelCallPurpose purpose)
    {
        o.MemoryAutoCapture = purpose == MentorModelCallPurpose.MemoryExtraction;
        switch (purpose)
        {
            case MentorModelCallPurpose.SafetyCheck: o.EnableSafetyCheck = true; break;
            case MentorModelCallPurpose.RefusalCheck: o.RefuseOutOfScope = true; break;
            case MentorModelCallPurpose.OutputSafetyCheck: o.EnableOutputSafetyCheck = true; break;
            case MentorModelCallPurpose.AnnouncedActionCheck: o.CheckAnnouncedActions = true; break;
            case MentorModelCallPurpose.MemoryExtraction: o.UseMemoryContext = true; break;
            case MentorModelCallPurpose.RoutingCheck:
                var strong = new FakeChatClient { ModelId = "strong-model" };
                strong.SayRepeatedly("Here you are.", times: 4);
                o.StrongChatClient = strong;
                o.RoutingStrategy = MentorRoutingStrategy.Classifier;
                break;
        }
    }

    [Theory]
    [MemberData(nameof(TurnChecks))]
    public async Task Each_check_of_a_turn_is_a_call_with_its_purpose_attributed_to_no_agent(MentorModelCallPurpose purpose)
    {
        var observer = new CallObserver();
        await using var host = MentorHostFixture.Create(o => Configure(o, purpose), s => s.AddMentorActionObserver(observer));
        host.Auth.SignIn("ada");
        host.Chat!.Respond((_, options) => Answer(host, options));

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await host.RunTurnAsync("how many open orders?", deadline.Token);
        await host.Get<MentorActionDecisions>().FlushAsync();

        var checks = observer.Calls.Where(c => c.Purpose == purpose).ToList();
        checks.Should().NotBeEmpty("{0} asks the model: {1}", purpose,
            string.Join(", ", observer.Calls.Select(c => c.Purpose)));
        // The check switched on, and the reply; the input check also asks the question of the scope in its call.
        observer.Calls.Select(c => c.Purpose)
            .Where(p => p != purpose && p is not MentorModelCallPurpose.Reply and not MentorModelCallPurpose.RefusalCheck)
            .Should().BeEmpty("only {0} was switched on", purpose);
        var reply = observer.Calls.FirstOrDefault(c => c.Purpose == MentorModelCallPurpose.Reply);
        checks.Should().AllSatisfy(c =>
        {
            c.AgentKind.Should().BeNull("a check is MentorAgent's own, no agent's");
            c.AgentName.Should().BeNull();
            c.User!.Identity!.Name.Should().Be("ada");
            c.SessionId.Should().NotBeNullOrEmpty();
            if (reply is not null)
            {
                c.SessionId.Should().Be(reply.SessionId, "the turn it served");
                c.TurnId.Should().Be(reply.TurnId, "the turn it served");
            }
            (c.InputTokens, c.OutputTokens).Should().Be((10L, 2L));
        });
    }

    [Fact]
    public async Task The_check_of_a_reply_to_another_agent_is_a_grounding_check()
    {
        var observer = new CallObserver();
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false, s => s.AddMentorActionObserver(observer));
        host.Chat!.Respond((_, options) => Answer(host, options));

        await host.Get<MentorOrchestrator>().ReplyStatesApplicationDataAsync("how many orders?", "There are 7.", default);
        await host.Get<MentorActionDecisions>().FlushAsync();

        observer.Calls.Should().ContainSingle().Which.Purpose.Should().Be(MentorModelCallPurpose.GroundingCheck);
    }

    [Fact]
    public async Task The_warm_up_is_counted_on_the_dashboard_and_reaches_the_observers_outside_any_session()
    {
        var observer = new CallObserver();
        await using var host = MentorHostFixture.Create(o =>
        {
            o.WarmUpAtStartup = true;
            o.WarmUpChatModel = true;
            o.MemoryAutoCapture = false;
        }, s => s.AddMentorActionObserver(observer));
        host.Chat!.Respond((_, options) => Answer(host, options));
        var usage = host.Get<MentorTokenUsage>();
        var session = host.Get<MentorSessionTokenUsage>();
        var (requestsBefore, sessionBefore) = (usage.RequestCount, session.RequestCount);

        var service = new MentorWarmUpService(
            host.Scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>().CreateScope().ServiceProvider,
            host.Get<Microsoft.Extensions.Options.IOptions<MentorOptions>>(),
            host.Get<ILogger<MentorWarmUpService>>());
        await service.StartAsync(default);
        await service.Completed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await host.Get<MentorActionDecisions>().FlushAsync();

        (usage.RequestCount - requestsBefore).Should().Be(1, "the warm-up is billed, and counted under its model");
        session.RequestCount.Should().Be(sessionBefore, "it serves no session");
        var call = observer.Calls.Should().ContainSingle().Subject;
        call.Purpose.Should().Be(MentorModelCallPurpose.WarmUp);
        call.SessionId.Should().BeNull("the warm-up serves no session");
        call.TurnId.Should().BeNull();
        call.User.Should().BeNull();
        call.AgentKind.Should().BeNull();
        (call.InputTokens, call.OutputTokens).Should().Be((10L, 2L));
    }

    [Theory]
    [InlineData(MentorModelCallPurpose.Reply, "reply")]
    [InlineData(MentorModelCallPurpose.SafetyCheck, "safety_check")]
    [InlineData(MentorModelCallPurpose.OutputSafetyCheck, "output_safety_check")]
    [InlineData(MentorModelCallPurpose.ScopeCheck, "scope_check")]
    [InlineData(MentorModelCallPurpose.RequestCheck, "request_check")]
    [InlineData(MentorModelCallPurpose.AnnouncedActionCheck, "announced_action_check")]
    [InlineData(MentorModelCallPurpose.RefusalCheck, "refusal_check")]
    [InlineData(MentorModelCallPurpose.GroundingCheck, "grounding_check")]
    [InlineData(MentorModelCallPurpose.MemoryExtraction, "memory_extraction")]
    [InlineData(MentorModelCallPurpose.Other, "other")]
    [InlineData(MentorModelCallPurpose.RoutingCheck, "routing_check")]
    [InlineData(MentorModelCallPurpose.WarmUp, "warm_up")]
    public void Every_purpose_has_its_own_value_on_a_span(MentorModelCallPurpose purpose, string tag)
        => Novior.MentorAgent.Middleware.ModelCallSpanTags.PurposeTag(purpose).Should().Be(tag);

    [Fact]
    public void Every_purpose_is_in_the_list_of_its_values_on_a_span()
    {
        // A purpose added without its value would go on the spans as "other", and the README lists them.
        var listed = typeof(ModelCallPurposeTests).GetMethod(nameof(Every_purpose_has_its_own_value_on_a_span))!
            .GetCustomAttributes(typeof(InlineDataAttribute), false).Cast<InlineDataAttribute>()
            .Select(a => (MentorModelCallPurpose)a.GetData(null!).First()[0]);
        listed.Should().BeEquivalentTo(Enum.GetValues<MentorModelCallPurpose>());
    }

    [Fact]
    public async Task A_classifier_the_host_already_traces_is_not_traced_twice()
    {
        // What the README told hosts before 1.5.0: wrap ClassifierChatClient with UseOpenTelemetry() to trace it.
        var source = "MentorAgent.Tests.Purposes." + Guid.NewGuid().ToString("N");
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == source,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => { lock (spans) spans.Add(a); },
        };
        ActivitySource.AddActivityListener(listener);

        var classifier = new FakeChatClient { ModelId = "classifier-model" };
        classifier.SayRepeatedly("SAFE", 4);
        await using var host = MentorHostFixture.Create(o =>
        {
            o.MemoryAutoCapture = false;
            o.EnableSafetyCheck = true;
            o.EnableObservability = true;
            o.ObservabilitySourceName = source;
            o.ClassifierChatClient = classifier.AsBuilder().UseOpenTelemetry(sourceName: source).Build();
        });
        host.Chat!.Respond((_, options) => Answer(host, options));

        await host.RunTurnAsync("how many open orders?");

        List<Activity> chats;
        lock (spans) chats = [.. spans.Where(s => s.GetTagItem("gen_ai.operation.name") as string == "chat")];
        classifier.CallCount.Should().BeGreaterThan(0);
        chats.Should().HaveCount(host.Chat.Calls.Count + classifier.CallCount, "one span for each call, never two");
        chats.Where(s => s.GetTagItem("gen_ai.request.model") as string == "classifier-model")
            .Should().HaveCount(classifier.CallCount)
            .And.AllSatisfy(s => s.GetTagItem("mentoragent.model.purpose").Should().BeNull("the span is the host's"));
    }

    [Fact]
    public async Task With_observability_each_check_has_its_span_with_its_purpose()
    {
        var source = "MentorAgent.Tests.Purposes." + Guid.NewGuid().ToString("N");
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == source,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => { lock (spans) spans.Add(a); },
        };
        ActivitySource.AddActivityListener(listener);

        await using var host = MentorHostFixture.Create(o =>
        {
            o.MemoryAutoCapture = false;
            o.EnableSafetyCheck = true;
            o.EnableOutputSafetyCheck = true;
            o.EnableObservability = true;
            o.ObservabilitySourceName = source;
        });
        host.Chat!.Respond((_, options) => Answer(host, options));

        await host.RunTurnAsync("how many open orders?");

        List<Activity> chats;
        lock (spans) chats = [.. spans.Where(s => s.GetTagItem("gen_ai.operation.name") as string == "chat")];
        chats.Select(s => s.GetTagItem("mentoragent.model.purpose") as string).Distinct()
            .Should().BeEquivalentTo(["reply", "safety_check", "output_safety_check"]);
        chats.Should().HaveCount(host.Chat.Calls.Count, "one span for each call, the checks' included");
        chats.Where(s => s.GetTagItem("mentoragent.model.purpose") as string != "reply").Should().AllSatisfy(s =>
        {
            s.GetTagItem("gen_ai.agent.name").Should().BeNull("a check is no agent's");
            s.GetTagItem("mentoragent.agent.kind").Should().BeNull();
        });
    }
}
