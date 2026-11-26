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
/// <c>response.ModelId</c>, including every round trip of the agent's function-invoking client —
/// the page's UI actions among them (story 1288).
/// </summary>
internal sealed class TokenUsageChatClient : DelegatingChatClient
{
    private readonly MentorTokenUsage _usage;
    private readonly MentorMetrics _metrics;
    private readonly ILogger _logger;

    /// <summary>
    /// Story 171 — the counter of the session this client serves, next to the process-wide one: an evaluation reads a
    /// turn's tokens from it, exactly, whatever else the process is doing. <see langword="null"/> for a client built
    /// outside a session's scope.
    /// </summary>
    private readonly MentorSessionTokenUsage? _session;

    /// <summary>The inner client's own deployment, read once, for responses that name no model.</summary>
    private string? _deploymentId;

    /// <summary>Story 1289 — who the calls are attributed to, and the observers they go to; none outside a session.</summary>
    private readonly ModelCallReporter? _reporter;
    private readonly ModelCallAttribution? _attribution;

    public TokenUsageChatClient(
        IChatClient innerClient, MentorTokenUsage usage, MentorMetrics metrics, ILogger logger,
        MentorSessionTokenUsage? session = null, ModelCallReporter? reporter = null, ModelCallAttribution? attribution = null)
        : base(innerClient)
    {
        _usage = usage;
        _metrics = metrics;
        _logger = logger;
        _session = session;
        _reporter = attribution is null ? null : reporter;
        _attribution = attribution;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var start = _reporter is null ? null : await _reporter.BeginAsync(_attribution!, cancellationToken);
        var sw = Stopwatch.StartNew();
        ChatResponse response;
        try
        {
            response = await base.GetResponseAsync(messages, options, cancellationToken);
        }
        catch (Exception ex)
        {
            // Not for the dashboard, which counts what was served: for the observers, the call and how it ended.
            Report(start, ex is OperationCanceledException ? MentorModelCallOutcome.Cancelled : MentorModelCallOutcome.Failed,
                null, 0, 0, null, null, usageMeasured: false, sw.Elapsed);
            throw;
        }
        sw.Stop();

        var inTok  = response.Usage?.InputTokenCount  ?? 0;
        var outTok = response.Usage?.OutputTokenCount ?? 0;
        var modelId = Record(response.ModelId, inTok, outTok, sw.Elapsed.TotalMilliseconds, ttfbMs: null);
        Report(start, MentorModelCallOutcome.Completed, modelId, inTok, outTok,
            response.Usage?.CachedInputTokenCount, response.Usage?.ReasoningTokenCount, usageMeasured: true, sw.Elapsed);
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var start = _reporter is null ? null : await _reporter.BeginAsync(_attribution!, cancellationToken);
        var sw = Stopwatch.StartNew();
        double? ttfb = null;
        long inTok = 0, outTok = 0;
        long? cached = null, reasoning = null;
        string? modelId = null;
        var sawUsage = false;
        var started = false;
        // A reader that stops reading leaves this as it is: the call was stopped.
        var outcome = MentorModelCallOutcome.Cancelled;

        // try/finally rather than a plain trailing call: the user pressing Stop cancels the token,
        // which throws out of the enumerator, and a consumer can also simply stop enumerating. Both
        // used to skip the recording entirely, so a stopped turn left *no* trace in the dashboard —
        // not the tokens, not the latency, not even the request count — while the provider billed it,
        // input prompt and all (BUG-032).
        try
        {
            await using var updates = base.GetStreamingResponseAsync(messages, options, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                // The next update in a try of its own, with no yield in it: how the call ended is known here.
                try
                {
                    if (!await updates.MoveNextAsync())
                    {
                        outcome = MentorModelCallOutcome.Completed;
                        break;
                    }
                }
                catch (Exception ex)
                {
                    outcome = ex is OperationCanceledException ? MentorModelCallOutcome.Cancelled : MentorModelCallOutcome.Failed;
                    throw;
                }

                var update = updates.Current;
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
                        cached = uc.Details.CachedInputTokenCount ?? cached;
                        reasoning = uc.Details.ReasoningTokenCount ?? reasoning;
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
                modelId = Record(modelId, sawUsage ? inTok : 0, sawUsage ? outTok : 0,
                       sw.Elapsed.TotalMilliseconds, ttfb, usageMeasured: sawUsage);
            }

            // The observers hear of a call that failed before its first update too; one cancelled before it, never served,
            // is no call.
            if (started || outcome == MentorModelCallOutcome.Failed)
                Report(start, outcome, modelId, inTok, outTok, cached, reasoning, usageMeasured: sawUsage, sw.Elapsed);
        }
    }

    private void Report(ModelCallReporter.Start? start, MentorModelCallOutcome outcome, string? modelId, long inTok, long outTok,
        long? cached, long? reasoning, bool usageMeasured, TimeSpan duration)
    {
        if (start is null || _reporter is null || _attribution is null) return;
        try
        {
            _reporter.Publish(start, _attribution, outcome,
                string.IsNullOrWhiteSpace(modelId) ? _deploymentId ??= InnerClient.GetService<ChatClientMetadata>()?.DefaultModelId : modelId,
                inTok, outTok, cached, reasoning, usageMeasured, duration);
        }
        catch (Exception ex)
        {
            // Never into the call: the record is for the observers, and the reply is the user's.
            _logger.LogDebug("[MentorAgent] A model call could not be handed to the observers: {Type}.", ex.GetType().Name);
        }
    }

    /// <returns>The model id recorded.</returns>
    private string? Record(
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
        _session?.Record(inTok, outTok);

        _logger.LogInformation(
            "[MentorAgent] Tokens — model: {Model}, in: {In}, out: {Out}, call total: {CallTotal}, {Latency:0}ms | " +
            "session: {SessIn}+{SessOut}={SessTotal} over {Reqs} call(s)",
            modelId ?? "?", inTok, outTok, inTok + outTok, latencyMs,
            _usage.TotalInputTokens, _usage.TotalOutputTokens, _usage.TotalTokens, _usage.RequestCount);
        return modelId;
    }
}
