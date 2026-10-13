
using Microsoft.Extensions.AI;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Novior.MentorAgent.Middleware;

/// <summary>
/// IChatClient middleware that injects per-page UI action tools dynamically
/// and handles their function-calling loop internally.
///
/// Instead of a single dispatcher "invoke_ui_action(actionName, parameter)",
/// each UIActionRegistration becomes its own AIFunction with a proper name,
/// description, and parameter schema. The LLM calls "highlight_row(5)" directly.
///
/// Why the loop is internal: the Agent Framework resolves function calls using its
/// own static tool registry (built at coordinator startup). Injecting tools here —
/// at the IChatClient level — makes them visible to the LLM but NOT to the outer
/// function-calling infrastructure. This middleware therefore intercepts any UI action
/// calls in the LLM response, executes them itself, and returns only the final clean
/// response (text or non-UI tool calls) to the outer pipeline.
///
/// Pipeline placement (Path A only):
///   _options.ChatClient → UIActionsMiddleware → rawLlm
///   then .AsAIAgent(...)
///   then .AsBuilder().Use(MentorAgentUIMiddleware).Build()
/// </summary>
internal sealed class UIActionsMiddleware : DelegatingChatClient
{
    private const int MaxUIActionIterations = 10;

    private readonly IMentorPageContext _pageContext;
    private readonly IMentorStateService _stateService;
    private readonly ILogger<UIActionsMiddleware> _logger;

    public UIActionsMiddleware(
        IChatClient innerClient,
        IMentorPageContext pageContext,
        IMentorStateService stateService,
        ILogger<UIActionsMiddleware> logger)
        : base(innerClient)
    {
        _pageContext = pageContext;
        _stateService = stateService;
        _logger = logger;
    }

    // -- GetResponseAsync — internal UI action function-calling loop -------------

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options = TakeForcedCallMarker(options, out var forcedCall);
        var snapshot = forcedCall ? EmptyActions : _pageContext.UIActions;
        if (snapshot.Count == 0)
            return await base.GetResponseAsync(messages, options, cancellationToken);

        var workingMessages = new List<ChatMessage>(messages);
        var mergedOptions = MergeUIActionTools(options, snapshot);

        for (int i = 0; i < MaxUIActionIterations; i++)
        {
            var response = await base.GetResponseAsync(workingMessages, mergedOptions, cancellationToken);
            var uiCalls = ExtractUIActionCalls(response, snapshot);

            if (uiCalls.Count == 0)
                return response; // clean response — no UI action calls

            _logger.LogDebug("[MentorAgent] UIActionsMiddleware: {N} UI action call(s); executing internally.",
                uiCalls.Count);

            // Execute UI action calls internally
            var toolResults = new List<AIContent>();
            foreach (var call in uiCalls)
                toolResults.Add(await ExecuteUIActionAsync(snapshot[call.Name], call, cancellationToken));

            // Add assistant message + tool results so the LLM knows the actions were done
            var assistantMsg = response.Messages.LastOrDefault(m => m.Role == ChatRole.Assistant);
            if (assistantMsg is not null)
                workingMessages.Add(assistantMsg);
            workingMessages.Add(new ChatMessage(ChatRole.Tool, toolResults));

            // Re-read snapshot: the page may have changed UI actions after execution
            snapshot = _pageContext.UIActions;
            mergedOptions = MergeUIActionTools(options, snapshot);
        }

        _logger.LogWarning("[MentorAgent] UIActionsMiddleware: reached max iterations ({Max}).",
            MaxUIActionIterations);
        return await base.GetResponseAsync(workingMessages, options, cancellationToken);
    }

    // -- GetStreamingResponseAsync — loop then yield final response --------------

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options = TakeForcedCallMarker(options, out var forcedCall);
        var snapshot = forcedCall ? EmptyActions : _pageContext.UIActions;
        if (snapshot.Count == 0)
        {
            // True pass-through streaming when no UI actions are registered
            await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
                yield return update;
            yield break;
        }

        // When UI actions exist the loop still has to run here — the outer pipeline has never heard
        // of these tools — but it runs *streamed*. The earlier implementation called
        // GetResponseAsync and replayed the finished reply as updates, which was simpler and
        // switched token streaming off for every page that registered an action (BUG-052): a
        // twenty-second reasoning turn became twenty seconds of an unchanging typing indicator and
        // then the whole answer at once, on the ordinary turns that never call an action at all.
        //
        // Text is forwarded as it arrives; UI-action calls are withheld, because they are this
        // middleware's own business and nothing outside can resolve them.
        var workingMessages = new List<ChatMessage>(messages);
        var mergedOptions = MergeUIActionTools(options, snapshot);

        for (int i = 0; i < MaxUIActionIterations; i++)
        {
            var collected = new List<ChatResponseUpdate>();

            await foreach (var update in base.GetStreamingResponseAsync(
                               workingMessages, mergedOptions, cancellationToken))
            {
                collected.Add(update);

                if (WithoutOwnCalls(update, snapshot) is { } forward)
                    yield return forward;
            }

            var response = collected.ToChatResponse();
            var uiCalls = ExtractUIActionCalls(response, snapshot);

            if (uiCalls.Count == 0)
            {
                // Nothing to execute, and the text has already reached the caller as it arrived.
                yield break;
            }

            _logger.LogDebug(
                "[MentorAgent] UIActionsMiddleware (streaming): {N} UI action call(s); executing internally.",
                uiCalls.Count);

            var toolResults = new List<AIContent>();
            foreach (var call in uiCalls)
                toolResults.Add(await ExecuteUIActionAsync(snapshot[call.Name], call, cancellationToken));

            var assistantMsg = response.Messages.LastOrDefault(m => m.Role == ChatRole.Assistant);
            if (assistantMsg is not null)
                workingMessages.Add(assistantMsg);
            workingMessages.Add(new ChatMessage(ChatRole.Tool, toolResults));

            snapshot = _pageContext.UIActions;
            mergedOptions = MergeUIActionTools(options, snapshot);
        }

        // Max iterations fallback. It streams the same way, and it filters the same way: the
        // guarantee that a UI-action call never reaches the caller has to hold on the exhaustion
        // path too. It did not — the loop is driven with `options` here rather than `mergedOptions`
        // so our tools are no longer offered, but a model that had already decided to call one
        // still emits it, and an unfiltered fallback handed it straight out.
        _logger.LogWarning("[MentorAgent] UIActionsMiddleware (streaming): max iterations reached.");
        await foreach (var update in base.GetStreamingResponseAsync(workingMessages, options, cancellationToken))
            if (WithoutOwnCalls(update, snapshot) is { } forward)
                yield return forward;
    }

    /// <summary>
    /// The update as the caller should see it, or <see langword="null"/> when nothing of it
    /// survives — that is, when it carried only calls to <i>this middleware's own</i> registered
    /// actions.
    ///
    /// <para>
    /// Filtering is by registered name rather than by content type, so a genuine tool call
    /// belonging to the outer pipeline passes through untouched. When the update carried nothing
    /// else the original object is returned unchanged; only a mixed update is rebuilt, and then
    /// <b>every</b> settable property is carried over. The first version copied seven of them and
    /// silently dropped <c>FinishReason</c>, <c>AdditionalProperties</c>, <c>AuthorName</c> and
    /// <c>RawRepresentation</c> — invisible with a fake client, which emits one content per update
    /// and so never reaches this branch at all.
    /// </para>
    /// </summary>
    private static ChatResponseUpdate? WithoutOwnCalls(
        ChatResponseUpdate update,
        IReadOnlyDictionary<string, UIActionRegistration> snapshot)
    {
        var passthrough = update.Contents
            .Where(c => c is not FunctionCallContent fc || !snapshot.ContainsKey(fc.Name))
            .ToList();

        if (passthrough.Count == 0) return null;
        if (passthrough.Count == update.Contents.Count) return update;

        return new ChatResponseUpdate
        {
            Role                 = update.Role,
            Contents             = passthrough,
            ModelId              = update.ModelId,
            ResponseId           = update.ResponseId,
            MessageId            = update.MessageId,
            ConversationId       = update.ConversationId,
            CreatedAt            = update.CreatedAt,
            AuthorName           = update.AuthorName,
            FinishReason         = update.FinishReason,
            AdditionalProperties = update.AdditionalProperties,
            RawRepresentation    = update.RawRepresentation,
        };
    }

    // -- Core helpers -------------------------------------------------------------

    /// <summary>
    /// Finds FunctionCallContent items in the LLM response whose names match
    /// a currently registered UI action.
    /// </summary>
    private static List<FunctionCallContent> ExtractUIActionCalls(
        ChatResponse response,
        IReadOnlyDictionary<string, UIActionRegistration> snapshot)
    {
        return response.Messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionCallContent>()
            .Where(fc => snapshot.ContainsKey(fc.Name))
            .ToList();
    }

    /// <summary>
    /// Executes a single UI action, notifying the state service for UI feedback.
    ///
    /// <para>
    /// The registration is resolved <b>at execution time</b>, not from the snapshot the tools were
    /// built from. Seconds pass between offering the tool and the model calling it, and a user
    /// navigating in that window is entirely ordinary — <c>Clear()</c> runs in the page's
    /// <c>Dispose</c>. Running the snapshot's handler would fire a delegate closing over a component
    /// that no longer exists: at best an exception from inside disposed UI, at worst a state change
    /// applied to a page the user has left. A re-render that re-registers the same action name is
    /// the same problem in miniature, and the live lookup fixes both.
    /// </para>
    ///
    /// <para>
    /// Card buttons of kind <c>UIAction</c> already resolve at click time for exactly this reason
    /// ("logged and skipped rather than firing a handler from a disposed component"). This is the
    /// same rule on the path the model drives.
    /// </para>
    /// </summary>
    private async Task<FunctionResultContent> ExecuteUIActionAsync(
        UIActionRegistration reg,
        FunctionCallContent call,
        CancellationToken ct)
    {
        if (!_pageContext.UIActions.TryGetValue(reg.ActionName, out var live))
        {
            _logger.LogWarning(
                "[MentorAgent] UI action '{Name}' was requested after the page that registered it " +
                "went away — skipped rather than run against a disposed component.", reg.ActionName);

            return new FunctionResultContent(call.CallId,
                MentorUtils.ToolError($"The page that offers '{reg.ActionName}' is no longer open."));
        }

        reg = live;

        try
        {
            object? param = ExtractParam(call);

            _stateService.NotifyUIActionExecuting(reg.ActionName);

            if (reg.HandlerAsync is not null)
                await reg.HandlerAsync(param);
            else
                reg.Handler(param);

            _stateService.NotifyUIActionCompleted(reg.ActionName);

            _logger.LogDebug("[MentorAgent] UI action '{Name}' executed successfully.", reg.ActionName);

            return new FunctionResultContent(call.CallId,
                MentorUtils.ToolSuccessAction(reg.ActionName));
        }
        catch (Exception ex)
        {
            // The type only: the handler is the host's page code, and its message can carry personal data.
            _logger.LogWarning("[MentorAgent] UI action '{Name}' threw an exception ({Type}).",
                reg.ActionName, ex.GetType().Name);
            _logger.LogDebug(ex, "[MentorAgent] UI action '{Name}' threw an exception.", reg.ActionName);
            return new FunctionResultContent(call.CallId,
                MentorUtils.ToolError(ex.Message));
        }
    }

    /// <summary>
    /// Extracts the value of the "parameter" argument from a function call.
    ///
    /// Handles three calling conventions the LLM may use:
    ///   1. {"parameter": {...}}          → wrapped in the canonical "parameter" key
    ///   2. {"someKey": value}            → single anonymous argument
    ///   3. {"customerId":3,"items":"x"}  → multiple flat fields (the LLM unpacked the DTO)
    ///
    /// In case 3 the entire argument dictionary is re-serialised to a JsonElement so that
    /// <c>MentorPageContext.DeserializeParam&lt;T&gt;</c> can deserialise it as the DTO.
    /// </summary>
    private static object? ExtractParam(FunctionCallContent call)
    {
        var args = call.Arguments;
        if (args is null || args.Count == 0) return null;

        // Primary: named parameter "parameter" (matches our AIFunctionFactory signature)
        if (args.TryGetValue("parameter", out var p)) return p;

        // Fallback 1: single anonymous argument
        if (args.Count == 1) return args.Values.First();

        // Fallback 2: multiple flat fields — re-wrap as a single JSON object so the
        // registered DeserializeParam<TParam> can map them to the DTO properties.
        try
        {
            var json = JsonSerializer.Serialize(args);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone(); // Clone() owns its memory — safe after doc disposal
        }
        catch
        {
            return null;
        }
    }

    private static readonly IReadOnlyDictionary<string, UIActionRegistration> EmptyActions =
        new Dictionary<string, UIActionRegistration>();

    /// <summary>
    /// A forced call (RequireLookupForRecordQuestions, RequireActionForChangeRequests): the first call must be a
    /// read, one of the requested actions or navigate_to, so no UI action is added that could satisfy the
    /// requirement. The marker the tool filter set is removed here, its only reader, so it never reaches a
    /// provider client that sends every additional property in the request body.
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

    /// <summary>
    /// Clones <paramref name="original"/> and merges UI action tools into Tools list.
    /// If no UI actions are registered, returns a clone with the original tools unchanged.
    /// </summary>
    private ChatOptions MergeUIActionTools(
        ChatOptions? original,
        IReadOnlyDictionary<string, UIActionRegistration> snapshot)
    {
        var cloned = original is null ? new ChatOptions() : CloneChatOptions(original);
        var existingTools = cloned.Tools is not null ? new List<AITool>(cloned.Tools) : [];

        var existingNames = existingTools
            .OfType<AIFunction>()
            .Select(f => f.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (_, reg) in snapshot)
        {
            if (existingNames.Contains(reg.ActionName))
            {
                _logger.LogWarning(
                    "[MentorAgent] UIActionsMiddleware: action '{Name}' conflicts with an existing tool — skipped.",
                    reg.ActionName);
                continue;
            }

            var description = string.IsNullOrWhiteSpace(reg.ParameterHint)
                ? reg.Description
                : $"{reg.Description} (parameter: {reg.ParameterHint})";

            // The lambda here provides only the JSON schema for the LLM tool definition.
            // The ACTUAL execution happens in ExecuteUIActionAsync — not here.
            existingTools.Add(AIFunctionFactory.Create(
#pragma warning disable CS1998
                async (
                    [Description("Optional parameter: string, number, or JSON object")]
                    JsonElement? parameter = null,
                    CancellationToken ct = default) => string.Empty,
#pragma warning restore CS1998
                name: reg.ActionName,
                description: description));

            existingNames.Add(reg.ActionName);
        }

        cloned.Tools = existingTools;
        return cloned;
    }

    /// <summary>
    /// Converts a completed <see cref="ChatResponse"/> into streaming-compatible updates
    /// so the UI receives text chunks even when the loop ran non-streaming.
    /// </summary>
    private static IEnumerable<ChatResponseUpdate> AsStreamingUpdates(ChatResponse response)
    {
        foreach (var message in response.Messages.Where(m => m.Role == ChatRole.Assistant))
        {
            foreach (var content in message.Contents)
            {
                yield return new ChatResponseUpdate
                {
                    Role     = ChatRole.Assistant,
                    Contents = [content],
                };
            }
        }
    }

    // -- ChatOptions shallow clone ------------------------------------------------

    /// <summary>
    /// Copies the options so the UI action tools can be merged in without mutating the agent's own
    /// <see cref="ChatOptions"/>.
    ///
    /// <para>
    /// Uses <see cref="ChatOptions.Clone"/> rather than copying properties by hand. A hand-written
    /// copy quietly drops whatever it does not know about, and dropping
    /// <see cref="ChatOptions.ConversationId"/> breaks service-managed history: the follow-up request
    /// loses <c>previous_response_id</c> and the provider rejects the tool result with
    /// <c>HTTP 400 — No tool call found for function call output with call_id …</c>.
    /// </para>
    /// </summary>
    private static ChatOptions CloneChatOptions(ChatOptions src) => src.Clone();
}
