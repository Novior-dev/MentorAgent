using FluentAssertions;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Middleware;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Xunit;

namespace Novior.MentorAgent.Tests.Routing;

/// <summary>
/// Which model the dashboard says served the turn — asserted against a provider that behaves the way
/// the real one does.
///
/// <para>
/// This suite exists because of one line observed in a live run against Azure OpenAI:
/// <c>[MentorAgent] Tokens — model: , in: 3865, out: 28</c>. The model id is <b>empty</b>. Azure
/// returns no model on the chat response — the Agent Framework's own OpenTelemetry span agrees,
/// carrying <c>gen_ai.request.model: gpt-4.1</c> next to an empty <c>gen_ai.response.model</c> on
/// every single call of the run.
/// </para>
///
/// <para>
/// Nothing in 1,358 existing tests could see that, because <see cref="FakeChatClient"/> stamps
/// <c>ModelId = "fake-model"</c> on everything it returns. Every per-model assertion in the campaign
/// — B4's routing, B13's cost split, C2.6's "routing attributes cost to the model that actually
/// served" — has only ever run against a provider that answers a question the real one does not.
/// So the fake grows a <see cref="FakeChatClient.DeploymentId"/>: the client knows which deployment
/// it is pointed at, the response does not say. That is the real shape.
/// </para>
///
/// <para>
/// The property under test is the one <see cref="RoutingChatClient"/>'s own doc-comment promises:
/// it is installed innermost <em>"so every outer wrapper — token counter, observability — measures
/// whichever model actually served the call"</em>. The only channel carrying that fact outward is
/// <c>response.ModelId</c>, and against a real provider it is blank.
/// </para>
/// </summary>
public sealed class ServedModelAttributionTests
{
    private const string Cheap  = "gpt-4.1";
    private const string Strong = "o3";

    private sealed record Rig(IChatClient Pipeline, MentorMetrics Metrics, TestLogCollector Logs);

    /// <summary>
    /// The production pipeline order for these two: routing innermost (it picks the model), the token
    /// counter immediately outside it (it measures whoever served). Both fakes report an
    /// <b>empty</b> response model id — what Azure OpenAI actually does — while still knowing their
    /// own deployment name, which is also what a real client does.
    /// </summary>
    private static Rig Build(bool escalate)
    {
        var cheap  = new FakeChatClient { ModelId = "", DeploymentId = Cheap };
        var strong = new FakeChatClient { ModelId = "", DeploymentId = Strong };
        cheap.SayWithUsage("cheap answer", 100, 10);
        strong.SayWithUsage("strong answer", 200, 20);

        var logs = new TestLogCollector();
        var provider = new TestLoggerProvider(logs);

        var routing = new RoutingChatClient(
            cheap, strong, MentorRoutingStrategy.Custom,
            provider.CreateLogger(nameof(RoutingChatClient)),
            custom: (_, _) => Task.FromResult(escalate));

        var options = Microsoft.Extensions.Options.Options.Create(
            new MentorOptions { AppName = "x", ScanAssemblies = [] });
        var metrics = new MentorMetrics(new MentorTokenUsage(), options)
        {
            ModelId = Cheap,
            StrongModelId = Strong,
        };

        var pipeline = new TokenUsageChatClient(
            routing, new MentorTokenUsage(), metrics,
            provider.CreateLogger(nameof(TokenUsageChatClient)));

        return new Rig(pipeline, metrics, logs);
    }

    private static MentorModelUsage? BucketFor(MentorMetrics metrics, string modelId)
        => metrics.GetSnapshot().Models.FirstOrDefault(m => m.ModelId == modelId);

    // ── The defect ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_escalated_turn_is_billed_to_the_strong_model_even_when_the_provider_reports_no_model()
    {
        // The expensive half of the cost lever. If a strong-model call lands in the cheap model's
        // bucket, the dashboard understates spend *precisely on the turns that cost the most* — and
        // it does so silently, because the numbers still add up to a plausible total.
        var rig = Build(escalate: true);

        await rig.Pipeline.GetResponseAsync([new ChatMessage(ChatRole.User, "something hard")]);

        var strong = BucketFor(rig.Metrics, Strong);
        strong.Should().NotBeNull("the strong model served this turn and must appear in the dashboard");
        strong!.InputTokens.Should().Be(200);
        strong.OutputTokens.Should().Be(20);
        strong.RequestCount.Should().Be(1);

        BucketFor(rig.Metrics, Cheap)!.RequestCount.Should().Be(0,
            "the cheap model served nothing, so charging it this turn misattributes the whole call");
    }

    [Fact]
    public async Task The_streaming_path_attributes_the_served_model_too()
    {
        // The widget streams. The non-streaming path is the one unit tests reach for and the one
        // production almost never takes, so a fix that only covers it fixes nothing a user sees.
        var rig = Build(escalate: true);

        await foreach (var _ in rig.Pipeline.GetStreamingResponseAsync(
                           [new ChatMessage(ChatRole.User, "something hard")])) { }

        var strong = BucketFor(rig.Metrics, Strong);
        strong.Should().NotBeNull("streaming is how every real turn is served");
        strong!.RequestCount.Should().Be(1);
        BucketFor(rig.Metrics, Cheap)!.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task A_cheap_turn_is_still_billed_to_the_cheap_model()
    {
        // The control. A fix that stamps "strong" everywhere would pass the test above and be worse
        // than the bug, so the unescalated case is asserted in the same shape.
        var rig = Build(escalate: false);

        await rig.Pipeline.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

        BucketFor(rig.Metrics, Cheap)!.InputTokens.Should().Be(100);
        BucketFor(rig.Metrics, Strong)!.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task The_per_call_log_line_names_the_model_that_served()
    {
        // `Tokens — model: ` with nothing after the colon is what sent me looking. The line is the
        // only per-call record an operator reads while watching a deployment, and a blank there is
        // indistinguishable from "no routing configured".
        var rig = Build(escalate: true);

        await rig.Pipeline.GetResponseAsync([new ChatMessage(ChatRole.User, "something hard")]);

        var line = string.Join(" | ", rig.Logs.Records.Select(r => r.Message).Where(m => m.Contains("Tokens")));
        line.Should().Contain($"model: {Strong}");
        line.Should().NotContain("model: ,", "an empty model id reads as though nothing was configured");
    }

    // ── What must not regress ───────────────────────────────────────────────────

    [Fact]
    public async Task A_provider_that_does_report_a_model_is_believed_over_the_deployment_name()
    {
        // The provider answers with the resolved model, often more specific than the deployment name
        // (`gpt-4.1-2025-04-14` for a deployment called `gpt-4.1`). The router must not overwrite
        // that: the deployment name is a fallback, never an override.
        //
        // This test originally went further and asserted the dated id kept its **own metrics row**.
        // BUG-034 showed that was wrong — a second row for the same model splits the request count,
        // loses the cheap/strong classification, misses the pricing key and totals a real session at
        // €0,00. So the two properties are now asserted separately and at the level each belongs to:
        // the router preserves what the provider said, and the metrics fold it onto the configured
        // model.
        var cheap = new FakeChatClient { ModelId = "gpt-4.1-2025-04-14", DeploymentId = Cheap };
        cheap.SayWithUsage("hi", 5, 5);
        var strong = new FakeChatClient { ModelId = "", DeploymentId = Strong };

        var logs = new TestLogCollector();
        var provider = new TestLoggerProvider(logs);
        var routing = new RoutingChatClient(
            cheap, strong, MentorRoutingStrategy.Custom,
            provider.CreateLogger(nameof(RoutingChatClient)),
            custom: (_, _) => Task.FromResult(false));

        var metrics = new MentorMetrics(
            new MentorTokenUsage(),
            Microsoft.Extensions.Options.Options.Create(new MentorOptions { AppName = "x", ScanAssemblies = [] }))
        { ModelId = Cheap, StrongModelId = Strong };

        var pipeline = new TokenUsageChatClient(
            routing, new MentorTokenUsage(), metrics,
            provider.CreateLogger(nameof(TokenUsageChatClient)));

        var response = await pipeline.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

        response.ModelId.Should().Be("gpt-4.1-2025-04-14",
            "the router only fills the model id in when the provider left it blank");

        BucketFor(metrics, Cheap)!.RequestCount.Should().Be(1,
            "and the metrics fold the dated build onto the configured model, so one model is one row");
    }

    [Fact]
    public async Task Without_routing_a_blank_model_id_still_falls_back_to_the_configured_chat_model()
    {
        // The pre-existing safety net, pinned so the routing fix does not become the only thing
        // holding attribution up. With no router in the pipeline there is nothing to disambiguate,
        // and `MentorMetrics` already resolves a blank id to the configured chat model.
        var only = new FakeChatClient { ModelId = "", DeploymentId = Cheap };
        only.SayWithUsage("hi", 7, 3);

        var logs = new TestLogCollector();
        var metrics = new MentorMetrics(
            new MentorTokenUsage(),
            Microsoft.Extensions.Options.Options.Create(new MentorOptions { AppName = "x", ScanAssemblies = [] }))
        { ModelId = Cheap };

        var pipeline = new TokenUsageChatClient(
            only, new MentorTokenUsage(), metrics,
            new TestLoggerProvider(logs).CreateLogger(nameof(TokenUsageChatClient)));

        await pipeline.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

        BucketFor(metrics, Cheap)!.InputTokens.Should().Be(7);
    }
}
