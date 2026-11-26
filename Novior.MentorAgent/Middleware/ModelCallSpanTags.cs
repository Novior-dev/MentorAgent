using System.Diagnostics;
using System.Runtime.CompilerServices;
using Novior.MentorAgent.Observability;

namespace Novior.MentorAgent.Middleware;

/// <summary>
/// Story 1289 — names, on the span of a call to a model, the agent that made it (<c>gen_ai.agent.name</c>, and its kind
/// in <c>mentoragent.agent.kind</c>) and what the call was for (<c>mentoragent.model.purpose</c>). Never the user: who
/// asked is for the action observers only.
///
/// <para>
/// It sits <b>inside</b> the OpenTelemetry client, where the call's own <c>chat</c> span is the current one, and tags
/// only that span: one of <paramref name="sourceName"/>'s, for a chat. With no listener there is no such span, and the
/// one that is current then — the turn's, a tool's — is left as it is.
/// </para>
/// </summary>
internal sealed class ModelCallSpanTags(IChatClient innerClient, ModelCallAttribution who, string sourceName)
    : DelegatingChatClient(innerClient)
{
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Tag();
        return base.GetResponseAsync(messages, options, cancellationToken);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Tag();
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
            yield return update;
    }

    private void Tag()
    {
        if (Activity.Current is not { } span || span.Source.Name != sourceName
            || span.GetTagItem("gen_ai.operation.name") as string != "chat")
            return;

        if (who.Name() is { Length: > 0 } name) span.SetTag(MentorTelemetry.AgentNameTag, name);
        if (who.Kind is { } kind) span.SetTag(MentorTelemetry.AgentKindTag, KindTag(kind));
        span.SetTag(MentorTelemetry.ModelPurposeTag, PurposeTag(who.Purpose));
    }

    /// <summary>
    /// <paramref name="client"/> with its calls traced as the coordinator's are, when the host turned observability on
    /// (<see cref="MentorOptions.EnableObservability"/>): the same source, the content only when the host opted in, and
    /// each span saying who made the call and what for.
    ///
    /// <para>
    /// A client the host already traces — one with an <see cref="OpenTelemetryChatClient"/> in its chain, as the README
    /// told hosts to do before 1.5.0 — is left as it is: its span is the host's, and a second would count each call
    /// twice. The purpose goes on our spans only.
    /// </para>
    /// </summary>
    internal static IChatClient Traced(IChatClient client, ModelCallAttribution who, MentorOptions options)
        => !options.EnableObservability || client.GetService<OpenTelemetryChatClient>() is not null
            ? client
            : new ModelCallSpanTags(client, who, options.ObservabilitySourceName).AsBuilder()
                .UseOpenTelemetry(
                    sourceName: options.ObservabilitySourceName,
                    configure: c => c.EnableSensitiveData = options.ObservabilityIncludeSensitiveData)
                .Build();

    /// <summary>The kind's value on a span, in snake case.</summary>
    internal static string KindTag(MentorAgentKind kind) => kind switch
    {
        MentorAgentKind.Coordinator => "coordinator",
        MentorAgentKind.Specialist  => "specialist",
        MentorAgentKind.TeamMember  => "team_member",
        MentorAgentKind.AgentSource => "agent_source",
        MentorAgentKind.Router      => "router",
        _                           => "other",
    };

    /// <summary>The purpose's value on a span: its name in snake case, as the other values of MentorAgent's tags.</summary>
    internal static string PurposeTag(MentorModelCallPurpose purpose) => purpose switch
    {
        MentorModelCallPurpose.Reply                => "reply",
        MentorModelCallPurpose.SafetyCheck          => "safety_check",
        MentorModelCallPurpose.OutputSafetyCheck    => "output_safety_check",
        MentorModelCallPurpose.ScopeCheck           => "scope_check",
        MentorModelCallPurpose.RequestCheck         => "request_check",
        MentorModelCallPurpose.AnnouncedActionCheck => "announced_action_check",
        MentorModelCallPurpose.RefusalCheck         => "refusal_check",
        MentorModelCallPurpose.GroundingCheck       => "grounding_check",
        MentorModelCallPurpose.MemoryExtraction     => "memory_extraction",
        MentorModelCallPurpose.RoutingCheck         => "routing_check",
        MentorModelCallPurpose.WarmUp               => "warm_up",
        _                                           => "other",
    };
}
