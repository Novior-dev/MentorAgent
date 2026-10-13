using Novior.MentorAgent.Core;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Novior.MentorAgent.Middleware;

/// <summary>
/// <see cref="DelegatingChatClient"/> that records the token usage, model id and latency of every
/// model round-trip into <see cref="MentorMetrics"/> (per model, roadmap #9) and <see cref="MentorTokenUsage"/>
/// (global totals, Wave 1 · #1), logging a concise per-call line.
///
/// It is placed <b>outside</b> the routing client but <b>inside</b> everything else, so it measures the
/// actual prompt/completion tokens of whichever model served — cheap or strong — via
/// <c>response.ModelId</c>, including the extra round-trips performed by
/// <see cref="UIActionsMiddleware"/>'s internal loop.
/// </summary>
internal sealed class TokenUsageChatClient : DelegatingChatClient
{
    private readonly MentorTokenUsage _usage;
    private readonly MentorMetrics _metrics;
    private readonly ILogger _logger;

    /// <summary>The inner client's own deployment, read once, for responses that name no model.</summary>
    private string? _deploymentId;

    public TokenUsageChatClient(IChatClient innerClient, MentorTokenUsage usage, MentorMetrics metrics, ILogger logger)
        : base(innerClient)
    {
        _usage = usage;
        _metrics = metrics;
        _logger = logger;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var response = await base.GetResponseAsync(messages, options, cancellationToken);
        sw.Stop();

        var inTok  = response.Usage?.InputTokenCount  ?? 0;
        var outTok = response.Usage?.OutputTokenCount ?? 0;
        Record(response.ModelId, inTok, outTok, sw.Elapsed.TotalMilliseconds, ttfbMs: null);
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        double? ttfb = null;
        long inTok = 0, outTok = 0;
        string? modelId = null;
        var sawUsage = false;
        var started = false;

        // try/finally rather than a plain trailing call: the user pressing Stop cancels the token,
        // which throws out of the enumerator, and a consumer can also simply stop enumerating. Both
        // used to skip the recording entirely, so a stopped turn left *no* trace in the dashboard —
        // not the tokens, not the latency, not even the request count — while the provider billed it,
        // input prompt and all (BUG-032).
        try
        {
            await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
            {
                started = true;
                ttfb ??= sw.Elapsed.TotalMilliseconds; // first update = time to first byte
                // First NON-EMPTY id, not first non-null: Azure's opening chunk carries "" and `??=`
                // would keep it for the whole stream.
                if (string.IsNullOrEmpty(modelId)) modelId = update.ModelId;

                // Providers emit token usage as a UsageContent in the stream (usually the last update).
                foreach (var content in update.Contents)
                    if (content is UsageContent uc)
                    {
                        sawUsage = true;
                        inTok  = uc.Details.InputTokenCount  ?? inTok;
                        outTok = uc.Details.OutputTokenCount ?? outTok;
                    }

                yield return update;
            }
        }
        finally
        {
            sw.Stop();

            // Nothing to report if the call never reached the provider at all — a token cancelled
            // before the first update means no request was served and none was billed.
            if (started)
            {
                // No usage chunk means the counts are unknown, not zero. They are deliberately left
                // out of the totals and the call is disclosed as unmeasured instead; guessing would
                // understate every per-request average, which is the failure this was found by.
                Record(modelId, sawUsage ? inTok : 0, sawUsage ? outTok : 0,
                       sw.Elapsed.TotalMilliseconds, ttfb, usageMeasured: sawUsage);
            }
        }
    }

    private void Record(
        string? modelId, long inTok, long outTok, double latencyMs, double? ttfbMs,
        bool usageMeasured = true)
    {
        // The provider's own answer wins. When it gives none — Azure often does not — the client
        // still knows which deployment it is pointed at, and that is a better attribution than a
        // blank: the specialists and the classifier sit on this wrapper with no router in front of
        // them to fill the id in (BUG-030 did that for the coordinator only).
        if (string.IsNullOrWhiteSpace(modelId))
            modelId = _deploymentId ??= InnerClient.GetService<ChatClientMetadata>()?.DefaultModelId;

        // Per-model attribution + latency for the dashboard; this also updates the global token totals.
        _metrics.RecordModelUsage(modelId, inTok, outTok, latencyMs, ttfbMs, usageMeasured);

        _logger.LogInformation(
            "[MentorAgent] Tokens — model: {Model}, in: {In}, out: {Out}, call total: {CallTotal}, {Latency:0}ms | " +
            "session: {SessIn}+{SessOut}={SessTotal} over {Reqs} call(s)",
            modelId ?? "?", inTok, outTok, inTok + outTok, latencyMs,
            _usage.TotalInputTokens, _usage.TotalOutputTokens, _usage.TotalTokens, _usage.RequestCount);
    }
}
