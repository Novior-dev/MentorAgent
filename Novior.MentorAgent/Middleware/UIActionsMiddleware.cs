
using Microsoft.Extensions.AI;
using System.Runtime.CompilerServices;

namespace Novior.MentorAgent.Middleware;

/// <summary>
/// The forced first call of a turn, on Path A: what is left of the client that ran the page's UI actions.
///
/// <para>
/// Up to 1.3.0 this client added the page's UI actions to every request and ran their calls in a loop of its own,
/// because the agent's function-invoking client did not know them; the gate never saw those calls. From story 1288
/// they are tools of the run (<see cref="UIActionTools"/>), called by the agent and gated like every other action, and
/// this client neither adds nor runs anything.
/// </para>
///
/// <para>
/// It still reads the forced-call marker (<c>RequireLookupForRecordQuestions</c>, <c>RequireActionForChangeRequests</c>):
/// the marked request declares only the read, the requested actions or <c>navigate_to</c>, so no UI action can satisfy
/// the requirement (<see cref="ToolFilterChatClient.Forced"/>). It takes the marker off, and says, once per provider
/// client, that a provider answered such a request without a call (story 167).
/// </para>
///
/// Pipeline placement (Path A only): inside the tool filter, below the agent's function-invoking client.
/// </summary>
internal sealed class UIActionsMiddleware : DelegatingChatClient
{
    private readonly ILogger<UIActionsMiddleware> _logger;

    /// <summary>The host's provider client, which every session's pipeline shares: the warning is said once for it.</summary>
    private readonly object _provider;

    /// <summary>The providers already reported for answering a required call without one, for the process.</summary>
    private static readonly ConditionalWeakTable<object, object> s_ignoredTheRequiredCall = new();

    public UIActionsMiddleware(
        IChatClient innerClient,
        IMentorPageContext pageContext,
        IMentorStateService stateService,
        ILogger<UIActionsMiddleware> logger,
        object? provider = null)
        : base(innerClient)
    {
        _logger = logger;
        _provider = provider ?? this;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options = TakeForcedCallMarker(options, out var forcedCall);
        var response = await base.GetResponseAsync(messages, options, cancellationToken);
        if (forcedCall && !response.Messages.SelectMany(m => m.Contents).Any(c => c is ToolCallContent))
            SayTheRequiredCallWasIgnored();
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options = TakeForcedCallMarker(options, out var forcedCall);

        // A required call is looked for in the updates as they go by, nothing held back; a stream that fails or is
        // left early says nothing.
        var called = false;
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            if (forcedCall && !called && update.Contents.Any(c => c is ToolCallContent)) called = true;
            yield return update;
        }

        if (forcedCall && !called) SayTheRequiredCallWasIgnored();
    }

    /// <summary>
    /// A request that required a call (<c>tool_choice: required</c>) came back with none: a provider that honours the
    /// requirement cannot answer so — a call to a tool it was not offered is still a call — so this one ignores it, and
    /// the two options that force a call do nothing with it (Ollama does). Said once per provider client in the process,
    /// since every session's pipeline shares it.
    /// </summary>
    private void SayTheRequiredCallWasIgnored()
    {
        if (!s_ignoredTheRequiredCall.TryAdd(_provider, _provider)) return;
        _logger.LogWarning(
            "[MentorAgent] The provider answered a request that required a tool call (tool_choice: required) without " +
            "calling one: RequireLookupForRecordQuestions and RequireActionForChangeRequests have no effect with it. " +
            "Ollama ignores tool_choice; see the README for llama-server and vLLM.");
    }

    /// <summary>
    /// A forced call: the marker the tool filter set — or <see cref="ForcedReadChatClient"/>, without the filter — is
    /// removed here, its only reader, so it never reaches a provider client that sends every additional property in the
    /// request body.
    /// </summary>
    private static ChatOptions? TakeForcedCallMarker(ChatOptions? options, out bool forcedCall)
    {
        forcedCall = options?.AdditionalProperties?.ContainsKey(ToolFilterChatClient.ForcedCallKey) == true;
        if (!forcedCall) return options;

        var clone = options!.Clone();
        clone.AdditionalProperties = new AdditionalPropertiesDictionary(
            options.AdditionalProperties!.Where(p => p.Key != ToolFilterChatClient.ForcedCallKey));
        if (clone.AdditionalProperties.Count == 0) clone.AdditionalProperties = null;
        return clone;
    }
}
