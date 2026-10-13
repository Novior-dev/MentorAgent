using FluentAssertions;
using Novior.MentorAgent.Middleware;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Novior.MentorAgent.Tests.Routing;

/// <summary>
/// Model routing — the cost lever. Every turn either costs a cheap-model call or a strong-model
/// one, and the decision is made here.
///
/// <para>
/// Two properties matter beyond "does it pick the right model". First, the decision must be made
/// <b>once per turn</b>: a follow-up request carrying a tool result has to go to whichever model
/// started the turn, or the provider answers <c>HTTP 400 — No tool call found for function call
/// output</c>. Second, every failure path must fall back to the <b>cheap</b> model — a routing
/// hiccup that escalated instead would quietly multiply the bill.
/// </para>
///
/// <para>
/// Tested directly against <see cref="RoutingChatClient"/> rather than through a whole turn: the
/// class is the decision, and driving the orchestrator would add a pipeline's worth of noise
/// between the input and the thing being asserted.
/// </para>
/// </summary>
public sealed class RoutingChatClientTests
{
    private const int ComplexAxis = 0;
    private const int SimpleAxis = 1;

    private static readonly ChatMessage UserHello = new(ChatRole.User, "hello");

    private sealed record Rig(
        RoutingChatClient Client,
        FakeChatClient Cheap,
        FakeChatClient Strong,
        FakeChatClient? Classifier,
        FakeEmbeddingGenerator? Embeddings,
        TestLogCollector Logs);

    private static Rig Build(
        MentorRoutingStrategy strategy,
        Func<IReadOnlyList<ChatMessage>, CancellationToken, Task<bool>>? custom = null,
        FakeEmbeddingGenerator? embeddings = null,
        IReadOnlyList<string>? exemplars = null,
        float threshold = 0.35f,
        FakeChatClient? classifier = null)
    {
        var cheap = new FakeChatClient { ModelId = "cheap-model" };
        var strong = new FakeChatClient { ModelId = "strong-model" };
        var logs = new TestLogCollector();
        var logger = new TestLoggerProvider(logs).CreateLogger(nameof(RoutingChatClient));

        var client = new RoutingChatClient(
            cheap, strong, strategy, logger,
            custom: custom,
            embeddings: embeddings,
            exemplars: exemplars,
            threshold: threshold,
            classifier: classifier);

        return new Rig(client, cheap, strong, classifier, embeddings, logs);
    }

    private static Task<ChatResponse> AskAsync(Rig rig, string text = "hello")
        => rig.Client.GetResponseAsync([new ChatMessage(ChatRole.User, text)]);

    // ── Custom ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_custom_predicate_returning_true_routes_to_the_strong_model()
    {
        var rig = Build(MentorRoutingStrategy.Custom, custom: (_, _) => Task.FromResult(true));
        rig.Strong.Say("from the strong model");

        var response = await AskAsync(rig);

        response.Text.Should().Contain("from the strong model");
        rig.Cheap.CallCount.Should().Be(0, "the cheap model must not be called as well");
    }

    [Fact]
    public async Task A_custom_predicate_returning_false_routes_to_the_cheap_model()
    {
        var rig = Build(MentorRoutingStrategy.Custom, custom: (_, _) => Task.FromResult(false));
        rig.Cheap.Say("from the cheap model");

        var response = await AskAsync(rig);

        response.Text.Should().Contain("from the cheap model");
        rig.Strong.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Custom_with_no_predicate_stays_on_the_cheap_model()
    {
        // The constraint suite covers the warning; this covers the behaviour it warns about — a
        // routing strategy that cannot decide must not default to the expensive side.
        var rig = Build(MentorRoutingStrategy.Custom);
        rig.Cheap.Say("cheap");

        await AskAsync(rig);

        rig.Strong.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task The_custom_predicate_sees_the_whole_conversation()
    {
        // The documented advantage of the async predicate over the legacy sync one.
        IReadOnlyList<ChatMessage>? seen = null;
        var rig = Build(MentorRoutingStrategy.Custom, custom: (messages, _) =>
        {
            seen = messages;
            return Task.FromResult(false);
        });
        rig.Cheap.Say("ok");

        await rig.Client.GetResponseAsync([
            new ChatMessage(ChatRole.User, "first"),
            new ChatMessage(ChatRole.Assistant, "reply"),
            new ChatMessage(ChatRole.User, "second"),
        ]);

        seen.Should().HaveCount(3);
    }

    [Fact]
    public async Task A_throwing_custom_predicate_falls_back_to_cheap_and_warns()
    {
        var rig = Build(MentorRoutingStrategy.Custom,
            custom: (_, _) => throw new InvalidOperationException("predicate blew up"));
        rig.Cheap.Say("cheap");

        var response = await AskAsync(rig);

        response.Text.Should().Contain("cheap");
        rig.Strong.CallCount.Should().Be(0, "a broken router must never escalate by accident");
        rig.Logs.HasWarning("Model routing failed").Should().BeTrue();
    }

    // ── Semantic ────────────────────────────────────────────────────────────────

    private static Rig SemanticRig(float threshold = 0.35f)
    {
        var embeddings = new FakeEmbeddingGenerator();
        foreach (var exemplar in Exemplars) embeddings.OnAxis(exemplar, ComplexAxis);

        return Build(MentorRoutingStrategy.Semantic,
            embeddings: embeddings, exemplars: Exemplars, threshold: threshold);
    }

    private static readonly string[] Exemplars =
    [
        "Analyze this in depth and explain your reasoning step by step.",
        "Write, review, or debug source code and explain the fix.",
    ];

    [Fact]
    public async Task A_message_scoring_at_or_above_the_threshold_escalates()
    {
        var rig = SemanticRig(threshold: 0.35f);
        rig.Embeddings!.AtSimilarity("a hard question", ComplexAxis, 0.40f);
        rig.Strong.Say("strong");

        await AskAsync(rig, "a hard question");

        rig.Strong.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task A_message_scoring_below_the_threshold_stays_cheap()
    {
        var rig = SemanticRig(threshold: 0.35f);
        rig.Embeddings!.AtSimilarity("hi there", ComplexAxis, 0.20f);
        rig.Cheap.Say("cheap");

        await AskAsync(rig, "hi there");

        rig.Strong.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task The_threshold_is_inclusive()
    {
        // `>=`, documented as "reaches the threshold". Exactly-at-threshold is the one value a
        // strict comparison gets wrong, and it is also the value a developer tunes to.
        var rig = SemanticRig(threshold: 0.50f);
        rig.Embeddings!.AtSimilarity("borderline", ComplexAxis, 0.50f);
        rig.Strong.Say("strong");

        await AskAsync(rig, "borderline");

        rig.Strong.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task Semantic_without_an_embedding_generator_stays_cheap()
    {
        // The constraint suite asserts the warning at configuration time; this is the runtime half.
        var rig = Build(MentorRoutingStrategy.Semantic, embeddings: null);
        rig.Cheap.Say("cheap");

        await AskAsync(rig, "anything");

        rig.Strong.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task An_empty_user_message_stays_cheap_without_embedding_anything()
    {
        var rig = SemanticRig();
        rig.Cheap.Say("cheap");

        await AskAsync(rig, "   ");

        rig.Strong.CallCount.Should().Be(0);
        rig.Embeddings!.CallCount.Should().Be(0, "there is nothing to embed");
    }

    [Fact]
    public async Task The_exemplars_are_embedded_once_and_reused()
    {
        // They never change, so re-embedding them per turn would make "~free" routing cost an
        // embedding call per exemplar per message.
        var rig = SemanticRig();
        rig.Embeddings!.AtSimilarity("q1", ComplexAxis, 0.10f).AtSimilarity("q2", ComplexAxis, 0.10f);
        rig.Cheap.SayRepeatedly("cheap", times: 4);

        await AskAsync(rig, "q1");
        var afterFirst = rig.Embeddings.CallCount;
        await AskAsync(rig, "q2");

        (rig.Embeddings.CallCount - afterFirst).Should().Be(1,
            "the second turn embeds only the query, not the exemplars again");
    }

    [Fact]
    public async Task Custom_exemplars_replace_the_built_in_ones()
    {
        // A caller supplying their own must not also be scored against the defaults — the defaults
        // are generic, and a domain-specific set is the whole reason to override them.
        var embeddings = new FakeEmbeddingGenerator();
        embeddings.OnAxis("only this one counts", ComplexAxis);
        embeddings.AtSimilarity("a hard question", ComplexAxis, 0.90f);

        var rig = Build(MentorRoutingStrategy.Semantic,
            embeddings: embeddings, exemplars: ["only this one counts"], threshold: 0.50f);
        rig.Strong.Say("strong");

        await AskAsync(rig, "a hard question");

        rig.Strong.CallCount.Should().Be(1);
        embeddings.InputCount.Should().BeLessThan(6, "only the one supplied exemplar was embedded");
    }

    [Fact]
    public async Task A_failing_embedder_falls_back_to_cheap_and_warns()
    {
        var rig = SemanticRig();
        rig.Embeddings!.ThrowOnNextCall = new InvalidOperationException("429");
        rig.Cheap.Say("cheap");

        await AskAsync(rig, "a hard question");

        rig.Strong.CallCount.Should().Be(0);
        rig.Logs.HasWarning("Model routing failed").Should().BeTrue();
    }

    // ── Classifier ──────────────────────────────────────────────────────────────

    private static Rig ClassifierRig(string label)
    {
        var classifier = new FakeChatClient { ModelId = "classifier" };
        classifier.SayRepeatedly(label, times: 8);
        return Build(MentorRoutingStrategy.Classifier, classifier: classifier);
    }

    [Fact]
    public async Task The_label_COMPLEX_escalates()
    {
        var rig = ClassifierRig("COMPLEX");
        rig.Strong.Say("strong");

        await AskAsync(rig, "compare three architectures");

        rig.Strong.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task The_label_SIMPLE_stays_cheap()
    {
        var rig = ClassifierRig("SIMPLE");
        rig.Cheap.Say("cheap");

        await AskAsync(rig, "hi");

        rig.Strong.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task The_label_is_matched_case_insensitively_and_ignores_leading_whitespace()
    {
        var rig = ClassifierRig("  complex");
        rig.Strong.Say("strong");

        await AskAsync(rig, "something");

        rig.Strong.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task A_rambling_answer_that_merely_mentions_the_label_does_not_escalate()
    {
        // The label is parsed with StartsWith, not Contains. That distinction is the anti-keyword
        // rule applied to the classifier's own output: "This is not COMPLEX at all" must not
        // escalate, and a Contains check would read it backwards.
        var rig = ClassifierRig("This request is not COMPLEX, it is simple.");
        rig.Cheap.Say("cheap");

        await AskAsync(rig, "hi");

        rig.Strong.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task An_unparseable_label_stays_cheap()
    {
        var rig = ClassifierRig("I'm not sure how to answer that.");
        rig.Cheap.Say("cheap");

        await AskAsync(rig, "hi");

        rig.Strong.CallCount.Should().Be(0, "an ambiguous verdict must not cost the expensive model");
    }

    [Fact]
    public async Task An_empty_message_skips_the_classifier_call_entirely()
    {
        var rig = ClassifierRig("COMPLEX");
        rig.Cheap.Say("cheap");

        await AskAsync(rig, "   ");

        rig.Classifier!.CallCount.Should().Be(0, "classifying nothing costs a call and decides nothing");
        rig.Strong.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task The_classification_call_goes_to_the_classifier_client_not_the_cheap_one()
    {
        // When a dedicated RoutingClassifierClient is configured, the routing call must not land on
        // the cheap client — otherwise it is metered as a conversation turn and the dashboard
        // reports more turns than the user had.
        var rig = ClassifierRig("SIMPLE");
        rig.Cheap.Say("cheap");

        await AskAsync(rig, "hi");

        rig.Classifier!.CallCount.Should().Be(1);
        rig.Cheap.CallCount.Should().Be(1, "one conversation turn, not two");
    }

    [Fact]
    public async Task Without_a_dedicated_classifier_the_cheap_client_does_the_classifying()
    {
        // Documented default. Both calls land on the cheap client, which is the trade-off for not
        // having to configure a third model.
        var rig = Build(MentorRoutingStrategy.Classifier);
        rig.Cheap.SayRepeatedly("SIMPLE", times: 4);

        await AskAsync(rig, "hi");

        rig.Cheap.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task A_throwing_classifier_falls_back_to_cheap_and_warns()
    {
        var classifier = new FakeChatClient { ThrowOnNextCall = new InvalidOperationException("down") };
        var rig = Build(MentorRoutingStrategy.Classifier, classifier: classifier);
        rig.Cheap.Say("cheap");

        await AskAsync(rig, "something");

        rig.Strong.CallCount.Should().Be(0);
        rig.Logs.HasWarning("Model routing failed").Should().BeTrue();
    }

    // ── Cascade ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cascade_keeps_the_cheap_answer_when_the_judge_says_it_is_complete()
    {
        var classifier = new FakeChatClient();
        classifier.Say("COMPLETE");
        var rig = Build(MentorRoutingStrategy.Cascade, classifier: classifier);
        rig.Cheap.Say("a perfectly good answer");

        var response = await AskAsync(rig, "what time do you open?");

        response.Text.Should().Contain("a perfectly good answer");
        rig.Strong.CallCount.Should().Be(0, "the whole point is not paying for the strong model");
    }

    [Fact]
    public async Task Cascade_re_runs_on_the_strong_model_when_the_judge_says_incomplete()
    {
        var classifier = new FakeChatClient();
        classifier.Say("INCOMPLETE");
        var rig = Build(MentorRoutingStrategy.Cascade, classifier: classifier);
        rig.Cheap.Say("I'm not sure, could you clarify?");
        rig.Strong.Say("a full answer");

        var response = await AskAsync(rig, "compare these three options");

        response.Text.Should().Contain("a full answer");
        rig.Cheap.CallCount.Should().Be(1, "the cheap attempt is made exactly once");
        rig.Strong.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task Cascade_keeps_the_cheap_answer_when_the_judge_fails()
    {
        // Escalating on a judge error would turn a classifier outage into a bill multiplier.
        var classifier = new FakeChatClient { ThrowOnNextCall = new InvalidOperationException("down") };
        var rig = Build(MentorRoutingStrategy.Cascade, classifier: classifier);
        rig.Cheap.Say("cheap answer");

        var response = await AskAsync(rig, "a question");

        response.Text.Should().Contain("cheap answer");
        rig.Strong.CallCount.Should().Be(0);
        rig.Logs.HasWarning("completeness judge failed").Should().BeTrue();
    }

    [Fact]
    public async Task Cascade_on_a_streaming_call_pre_decides_instead_of_re_running()
    {
        // A stream cannot be retracted once tokens have been sent, so Cascade degrades to the
        // Classifier behaviour on the input. The cheap model must not be called at all when the
        // pre-decision escalates — that would be paying for both.
        var classifier = new FakeChatClient();
        classifier.SayRepeatedly("COMPLEX", times: 4);
        var rig = Build(MentorRoutingStrategy.Cascade, classifier: classifier);
        rig.Strong.Say("streamed from strong");

        var chunks = new List<string>();
        await foreach (var update in rig.Client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hard")]))
            chunks.Add(update.Text ?? "");

        string.Concat(chunks).Should().Contain("streamed from strong");
        rig.Cheap.CallCount.Should().Be(0);
    }

    // ── One decision per turn ───────────────────────────────────────────────────

    [Fact]
    public async Task A_tool_result_follow_up_reuses_the_model_that_started_the_turn()
    {
        // The failure this prevents: HTTP 400 "No tool call found for function call output". It
        // only surfaces under service-managed history, where the follow-up carries just the tool
        // result — with local history the same decision happens to come out again by accident.
        var rig = Build(MentorRoutingStrategy.Custom, custom: (_, _) => Task.FromResult(true));
        rig.Strong.SayRepeatedly("strong", times: 4);

        await AskAsync(rig, "do the thing");
        rig.Strong.CallCount.Should().Be(1);

        await rig.Client.GetResponseAsync([
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", "done")]),
        ]);

        rig.Strong.CallCount.Should().Be(2, "the follow-up must go to the same model");
        rig.Cheap.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task A_tool_result_follow_up_does_not_re_run_the_router()
    {
        // Not just the same answer — the decision must not be recomputed. A classifier call per
        // tool round would triple the routing cost of any tool-using turn.
        var rig = ClassifierRig("SIMPLE");
        rig.Cheap.SayRepeatedly("cheap", times: 4);

        await AskAsync(rig, "do the thing");
        var afterFirst = rig.Classifier!.CallCount;

        await rig.Client.GetResponseAsync([
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", "done")]),
        ]);

        rig.Classifier.CallCount.Should().Be(afterFirst, "the router runs once per turn");
    }

    [Fact]
    public async Task A_streaming_tool_result_follow_up_also_reuses_the_decision()
    {
        var rig = Build(MentorRoutingStrategy.Custom, custom: (_, _) => Task.FromResult(true));
        rig.Strong.SayRepeatedly("strong", times: 4);

        await foreach (var _ in rig.Client.GetStreamingResponseAsync([UserHello])) { }
        await foreach (var _ in rig.Client.GetStreamingResponseAsync([
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", "done")])])) { }

        rig.Cheap.CallCount.Should().Be(0);
        rig.Strong.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task A_new_user_message_decides_again()
    {
        // The selection is per turn, not per connection: a simple follow-up question after a hard
        // one must be allowed back down to the cheap model.
        var custom = new Queue<bool>([true, false]);
        var rig = Build(MentorRoutingStrategy.Custom, custom: (_, _) => Task.FromResult(custom.Dequeue()));
        rig.Strong.SayRepeatedly("strong", times: 2);
        rig.Cheap.SayRepeatedly("cheap", times: 2);

        await AskAsync(rig, "a hard question");
        await AskAsync(rig, "thanks");

        rig.Strong.CallCount.Should().Be(1);
        rig.Cheap.CallCount.Should().Be(1);
    }

    // ── The options travel untouched ────────────────────────────────────────────

    [Fact]
    public async Task The_conversation_id_reaches_whichever_model_serves_the_turn()
    {
        // Routing selects a client; it must not rebuild ChatOptions. Hand-copying them drops
        // ConversationId, and under service-managed history that breaks every tool call.
        var rig = Build(MentorRoutingStrategy.Custom, custom: (_, _) => Task.FromResult(true));
        rig.Strong.Say("strong");

        await rig.Client.GetResponseAsync([UserHello], new ChatOptions { ConversationId = "conv-42" });

        rig.Strong.LastCall!.ConversationId.Should().Be("conv-42");
    }

    [Fact]
    public async Task The_tool_list_reaches_whichever_model_serves_the_turn()
    {
        var rig = Build(MentorRoutingStrategy.Custom, custom: (_, _) => Task.FromResult(true));
        rig.Strong.Say("strong");

        var tool = AIFunctionFactory.Create(() => "ok", "a_tool", "A tool");
        await rig.Client.GetResponseAsync([UserHello], new ChatOptions { Tools = [tool] });

        rig.Strong.LastCall!.ToolNames.Should().Contain("a_tool");
    }

    [Fact]
    public async Task The_cheap_model_is_the_inner_client_so_outer_middleware_measures_the_real_one()
    {
        // Documented placement: routing is installed innermost, so the token counter and
        // observability wrappers see whichever model actually served the call.
        var rig = Build(MentorRoutingStrategy.Custom, custom: (_, _) => Task.FromResult(true));
        rig.Strong.Say("strong");

        var response = await AskAsync(rig);

        response.ModelId.Should().Be("strong-model",
            "the response has to carry the model that really ran, not the configured default");
    }
}
