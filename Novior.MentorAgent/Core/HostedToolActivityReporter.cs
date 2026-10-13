using Novior.MentorAgent.Options;

namespace Novior.MentorAgent.Core;

/// <summary>
/// Turns the content parts a provider emits while running its own hosted tools (roadmap #15) into the
/// widget signals MentorAgent already has: the action-feedback line, the citation chips, and inline
/// images on the finished message.
///
/// <para>
/// <b>Why this exists.</b> A hosted tool runs on the provider, so nothing passes through the
/// function-calling middleware that normally drives those signals. A web search or a code-interpreter
/// run can take several seconds during which not one token is streamed — without this the widget sits
/// there looking frozen, and the pages the model actually used are never shown.
/// </para>
///
/// <para>
/// Everything here is best-effort presentation: providers differ in which of these parts they emit and
/// how much they fill in, so an unrecognised or empty part is skipped rather than guessed at. The
/// answer itself never depends on any of it.
/// </para>
/// </summary>
internal sealed class HostedToolActivityReporter
{
    private readonly IMentorStateService _state;
    private readonly MentorLocalizer     _localizer;
    private readonly MentorOptions       _options;
    private readonly ILogger             _logger;

    /// <summary>Citations collected during the turn, de-duplicated by URL.</summary>
    private readonly Dictionary<string, MentorRagResult> _citations = [];

    /// <summary>Image sources (data: URIs or remote URLs) generated during the turn.</summary>
    private readonly List<string> _images = [];

    /// <summary>Hosted calls already announced this turn, so a streamed part is not reported twice.</summary>
    private readonly HashSet<string> _announced = [];

    /// <summary>
    /// Whether file search is among the enabled hosted tools. It is the only one MEAI 10.6.0 has no
    /// content type for, so an unnamed hosted call can be attributed to it — but only when it is on.
    /// </summary>
    private readonly bool _fileSearchEnabled;

    /// <summary>Session-wide call counter, so a budget can be enforced by the tool filter.</summary>
    private readonly HostedToolBudget? _budget;

    public HostedToolActivityReporter(
        IMentorStateService state, MentorLocalizer localizer, MentorOptions options, ILogger logger,
        HostedToolBudget? budget = null)
    {
        _state             = state;
        _localizer         = localizer;
        _options           = options;
        _logger            = logger;
        _budget            = budget;
        _fileSearchEnabled = options.HostedTools.HasFlag(MentorHostedTools.FileSearch);
    }

    /// <summary>
    /// Inspects one streamed content part. Returns true when it was a hosted-tool part (handled or
    /// deliberately ignored), so the caller can skip its own logging.
    /// </summary>
    public bool Handle(AIContent content)
    {
        // Citations ride along as annotations on whichever part carries them — for file search that is
        // the answer text itself, which is not a hosted-tool part and must fall through untouched.
        // Collecting before the switch is what makes those sources reachable at all.
        CollectAnnotations(content);

        switch (content)
        {
            case WebSearchToolCallContent call:
                Announce(call.CallId, "hosted.activity.websearch", Describe(call.Queries));
                return true;

            case WebSearchToolResultContent result:
                CollectCitations(result.Outputs);
                Complete("hosted.websearch");
                return true;

            case CodeInterpreterToolCallContent call:
                Announce(call.CallId, "hosted.activity.code", null);
                return true;

            case CodeInterpreterToolResultContent result:
                LogCodeOutputs(result.Outputs);
                Complete("hosted.code");
                return true;

            case ImageGenerationToolCallContent call:
                Announce(call.CallId, "hosted.activity.image", null);
                return true;

            case ImageGenerationToolResultContent result:
                CollectImages(result.Outputs);
                Complete("hosted.imagegen");
                return true;

            case McpServerToolCallContent call:
                // The provider is the MCP client here, so this is the only visibility available:
                // which server, which tool. Arguments are not surfaced — they can carry user data.
                Announce(call.CallId, "hosted.activity.mcp", $"{call.ServerName} · {call.Name}");
                _logger.LogDebug("[MentorAgent] Hosted MCP call: {Server}/{Tool}", call.ServerName, call.Name);
                return true;

            case McpServerToolResultContent:
                Complete("hosted.mcp");
                return true;

            // MEAI 10.6.0 ships no content type for hosted file search: its call and result arrive as
            // the plain base types, so this is what a file search looks like on the wire.
            //
            // ⚠️ FunctionCallContent and FunctionResultContent DERIVE from these bases. Excluding them
            // is not defensive coding — without it every local tool call would be swallowed here and
            // function calling would stop working.
            case ToolCallContent call when call is not FunctionCallContent:
                AnnounceUnnamedCall(call);
                return true;

            case ToolResultContent result when result is not FunctionResultContent:
                if (_fileSearchEnabled) Complete("hosted.filesearch");
                return true;

            default:
                return false;
        }
    }

    // -- Feedback line ---------------------------------------------------------

    private void Announce(string callId, string localizationKey, string? detail)
    {
        // Streaming repeats the same call part across updates; announce each call id once.
        if (!_announced.Add(callId)) return;

        // Same de-duplication makes this the right place to meter the session budget: one increment
        // per actual provider-side call, not per streamed fragment.
        _budget?.Record();

        // Logged even when the feedback line is off: this is the only trace that a provider-side
        // tool ran at all — it never reaches the function-calling middleware or the metrics.
        _logger.LogDebug("[MentorAgent] Hosted tool running: {Key}{Detail}",
            localizationKey, string.IsNullOrWhiteSpace(detail) ? "" : $" ({detail})");

        if (!_options.ShowHostedToolActivity) return;

        var label = _localizer.Get(localizationKey);
        _state.NotifyActionExecuting(
            localizationKey,
            string.IsNullOrWhiteSpace(detail) ? label : $"{label} · {detail}");
    }

    private void Complete(string toolKey)
    {
        if (_options.ShowHostedToolActivity)
            _state.NotifyActionCompleted(toolKey);
    }

    /// <summary>
    /// Reports a hosted call the provider sent without a dedicated content type. It is labelled as file
    /// search only when file search is actually enabled — otherwise we do not know what ran, and a wrong
    /// label is worse than none, so it is logged and left off the feedback line.
    /// </summary>
    private void AnnounceUnnamedCall(ToolCallContent call)
    {
        if (_fileSearchEnabled)
        {
            Announce(call.CallId, "hosted.activity.filesearch", null);
            return;
        }

        if (!_announced.Add(call.CallId)) return;

        // Still a provider-side call, so it still spends the session budget even though we cannot
        // label it.
        _budget?.Record();

        _logger.LogDebug(
            "[MentorAgent] Hosted tool ran with no dedicated content type (call {CallId}, provider type " +
            "{Raw}) — not shown, because no hosted tool known to produce one is enabled.",
            call.CallId, call.RawRepresentation?.GetType().Name ?? "?");
    }

    private static string? Describe(IList<string>? queries)
        => queries is { Count: > 0 } ? string.Join(" · ", queries.Take(2)) : null;

    // -- Collectors ------------------------------------------------------------

    // Both collectors publish as soon as they have something new: the widget attaches whatever is
    // pending when streaming completes, and completion is fired from inside the stream we are reading.
    // Waiting until the end of the turn would miss the message these belong to.

    private void CollectCitations(IList<AIContent>? outputs)
    {
        if (outputs is null) return;

        var added = false;

        foreach (var output in outputs)
        {
            if (output is not UriContent uri) continue;

            var url = uri.Uri.ToString();
            if (_citations.ContainsKey(url)) continue;

            // AF puts the page title in AdditionalProperties when the provider supplies one; the
            // panel falls back to a shortened URL when it is missing.
            var title = output.AdditionalProperties?.TryGetValue("title", out var raw) == true
                ? raw as string
                : null;

            // Score 0: these are provider-ranked results with no comparable score of their own, and
            // the citation panel only uses it for ordering within a single source list.
            _citations[url] = new MentorRagResult(title ?? url, url, title, 0f);
            added = true;
        }

        if (!added) return;

        _state.NotifyRagSourcesReady([.. _citations.Values]);
        _logger.LogDebug("[MentorAgent] Hosted web search contributed {Count} citation(s).", _citations.Count);
    }

    /// <summary>
    /// Harvests <c>CitationAnnotation</c>s from any content part. This is the only route file-search
    /// sources ever take — the provider attaches them to the answer text rather than emitting a result
    /// part — and web search uses it too on providers that annotate instead of returning outputs.
    /// </summary>
    private void CollectAnnotations(AIContent content)
    {
        if (content.Annotations is not { Count: > 0 }) return;

        var added = false;

        foreach (var annotation in content.Annotations)
        {
            if (annotation is not CitationAnnotation citation) continue;

            // A web citation identifies its source by URL, a file citation by file id. Key on whichever
            // is present so the same page or document is never listed twice.
            var key = citation.Url?.ToString() ?? citation.FileId;
            if (string.IsNullOrWhiteSpace(key) || _citations.ContainsKey(key)) continue;

            var title = citation.Title ?? citation.FileId ?? key;

            // A document cited from a vector store has no URL to open, so the chip carries the snippet
            // as its content and no link — the panel already renders a titled chip without one.
            _citations[key] = new MentorRagResult(
                citation.Snippet ?? title,
                citation.Url?.ToString(),
                title,
                0f);
            added = true;
        }

        if (!added) return;

        _state.NotifyRagSourcesReady([.. _citations.Values]);
        _logger.LogDebug(
            "[MentorAgent] Hosted tool cited {Count} source(s) via annotations.", _citations.Count);
    }

    private void CollectImages(IList<AIContent>? outputs)
    {
        if (outputs is null) return;

        var added = false;

        foreach (var output in outputs)
        {
            switch (output)
            {
                // DataContent.Uri is already a data: URI, so it drops straight into <img src>.
                case DataContent data when data.HasTopLevelMediaType("image"):
                    _images.Add(data.Uri);
                    added = true;
                    break;
                case UriContent uri when uri.HasTopLevelMediaType("image"):
                    _images.Add(uri.Uri.ToString());
                    added = true;
                    break;
            }
        }

        if (!added) return;

        _state.NotifyGeneratedImages([.. _images]);
        _logger.LogDebug("[MentorAgent] Hosted image generation produced {Count} image(s).", _images.Count);
    }

    private void LogCodeOutputs(IList<AIContent>? outputs)
    {
        if (outputs is null || !_logger.IsEnabled(LogLevel.Debug)) return;

        foreach (var output in outputs)
        {
            switch (output)
            {
                case TextContent text when !string.IsNullOrWhiteSpace(text.Text):
                    _logger.LogDebug("[MentorAgent] Code interpreter output: {Output}", Truncate(text.Text));
                    break;
                // A chart or a generated file stays on the provider; downloading it needs the
                // provider's file client, which is outside MentorAgent's scope.
                case HostedFileContent file:
                    _logger.LogDebug(
                        "[MentorAgent] Code interpreter produced hosted file '{Name}' (id {Id}, {Type}) — " +
                        "download it with the provider's file client if you need it.",
                        file.Name ?? "?", file.FileId, file.MediaType ?? "?");
                    break;
            }
        }
    }

    private static string Truncate(string value)
        => value.Length <= 500 ? value : value[..500] + "…";
}
