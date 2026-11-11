

using System.Globalization;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Rag;
using Microsoft.AspNetCore.Components.Authorization;

namespace Novior.MentorAgent.Core;

/// <summary>
/// <see cref="AIContextProvider"/> that injects RAG (Retrieval-Augmented Generation)
/// documents into the coordinator's system prompt before each LLM call.
///
/// Attached exclusively to the <b>coordinator</b> — not to L2 / L3 agents.
/// The coordinator performs one RAG search; specialists receive context via conversation
/// history passed by the coordinator.
///
/// Flow:
/// <list type="number">
///   <item>
///     <see cref="PrepareAsync"/> is called by <see cref="MentorOrchestrator"/>
///     <b>before</b> <c>RunStreamingAsync</c>, so the source query (user message)
///     is available for the search.
///   </item>
///   <item>
///     <see cref="ProvideAIContextAsync"/> injects the retrieved documents into
///     the coordinator's system prompt via <see cref="AIContext.Instructions"/>.
///   </item>
///   <item>
///     <see cref="StoreAIContextAsync"/> fires
///     <see cref="IMentorStateService.NotifyRagSourcesReady"/> so the UI can
///     render citation chips below the AI response.
///   </item>
/// </list>
/// </summary>
internal sealed class RagContextProvider : AIContextProvider
{
    private readonly IMentorRagSource _ragSource;
    private readonly IMentorStateService _stateService;
    private readonly MentorOptions _options;
    private readonly ILogger<RagContextProvider> _logger;

    private IReadOnlyList<MentorRagResult>? _pendingSources;

    /// <summary>Said once, not once per turn — see the warning's own comment for why.</summary>
    private bool _warnedEverythingDiscarded;

    /// <summary>
    /// The conversation so far, as <see cref="MentorRagRequest.RecentMessages"/> hands it to the source:
    /// the user's messages and the finished replies, text only. This provider is scoped like the session,
    /// so it sees every turn of it; the session's own history may live on the service
    /// (<c>UseServiceManagedHistory</c>), where it cannot be read back.
    /// </summary>
    private readonly List<ChatMessage> _recent = [];

    /// <summary>Messages kept for <see cref="MentorRagRequest.RecentMessages"/>: the last three turns.</summary>
    internal const int RecentMessageLimit = 6;

    /// <summary>The page the turn is asked from, for <see cref="MentorRagRequest.PageName"/> and its data.</summary>
    internal IMentorPageContext? PageContext { get; init; }

    /// <summary>The host's provider, for <see cref="MentorRagRequest.User"/>; null when it registers none.</summary>
    internal AuthenticationStateProvider? Auth { get; init; }

    public RagContextProvider(
        IMentorRagSource ragSource,
        IMentorStateService stateService,
        IOptions<MentorOptions> options,
        ILogger<RagContextProvider> logger)
    {
        _ragSource = ragSource;
        _stateService = stateService;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>The language of the turn, for the citation rule; without it, <c>MentorOptions.Language</c>.</summary>
    internal MentorTurnLanguage? TurnLanguage { get; init; }

    /// <summary>The heading of the notice that the search kept nothing (story 194), English in every language as the other headings.</summary>
    internal const string NothingFoundHeading = "## Knowledge base — nothing found for THIS message\n";

    /// <summary>The notice itself, in the language of the turn; without it, <c>MentorOptions.Language</c>.</summary>
    private string NothingFound => MentorPromptTexts.For(TurnLanguage?.Current ?? _options.Language).RagNothingFound;

    /// <summary>How to cite the documents, appended after the host's template: in the language of the turn.</summary>
    private string CiteInstruction
        => (TurnLanguage?.Localizer ?? new Novior.MentorAgent.Abstractions.Core.MentorLocalizer(_options.Language)).Get("rag.cite");

    /// <summary>
    /// Whether a real knowledge base is behind this provider, or the no-op default that
    /// <c>TryAddScoped</c> installs when the host registered none. Read once at startup so
    /// <c>UseRag</c> without a source can be reported instead of silently returning nothing.
    /// </summary>
    internal bool HasRealSource => _ragSource is not NullRagSource;

    /// <summary>The types of source already checked for the request overload, once each in the process.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, byte> s_sourcesChecked = new();

    private static readonly MethodInfo s_requestOverload = typeof(IMentorRagSource).GetMethod(
        nameof(IMentorRagSource.SearchAsync), [typeof(MentorRagRequest), typeof(CancellationToken)])!;

    /// <summary>
    /// Story 157 — a source that implements only the text overload is never given the tenant: it serves every tenant the
    /// same documents. Said at its first search with a tenant, once per type of source in the process, since each session
    /// has a provider of its own; the source may be scoped, so it is not resolved at startup to be checked.
    /// </summary>
    private void WarnIfTheSourceIsNeverGivenTheTenant()
    {
        var type = _ragSource.GetType();
        if (!HasRealSource || !s_sourcesChecked.TryAdd(type, 0) || ReadsTheRequest(type)) return;
        _logger.LogWarning(
            "[MentorAgent:RAG] {Source} implements only SearchAsync(string, int, CancellationToken), and an " +
            "IMentorTenantResolver is registered: it is never given the tenant, so it serves every tenant the same " +
            "documents. Implement SearchAsync(MentorRagRequest, CancellationToken) and filter by MentorRagRequest.TenantId.",
            type.FullName);
    }

    /// <summary>
    /// Whether <paramref name="type"/> implements <see cref="IMentorRagSource.SearchAsync(MentorRagRequest, CancellationToken)"/>
    /// rather than keeping the interface's default, which calls the text overload. A type that cannot be read counts as one
    /// that does: no warning on a guess.
    /// </summary>
    internal static bool ReadsTheRequest(Type type)
    {
        try
        {
            var map = type.GetInterfaceMap(typeof(IMentorRagSource));
            var at = Array.IndexOf(map.InterfaceMethods, s_requestOverload);
            return at < 0 || map.TargetMethods[at].DeclaringType != typeof(IMentorRagSource);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return true;
        }
    }

    // ── Prepare (called BEFORE RunStreamingAsync) ──────────────────────────────

    /// <summary>
    /// Performs the vector search and caches results for the upcoming
    /// <see cref="ProvideAIContextAsync"/> call.
    ///
    /// Called by <c>MentorOrchestrator.SendMessageAsync</c> before each
    /// <c>RunStreamingAsync</c> invocation. Safe to call when
    /// <see cref="MentorOptions.UseRag"/> is <c>false</c>.
    /// </summary>
    public async Task PrepareAsync(string userMessage, CancellationToken ct = default)
    {
        if (!_options.UseRag)
        {
            _pendingSources = null;
            return;
        }

        // No keyword pre-gate: whether anything is injected is decided by the source's scores and
        // RagMinScore, not by word lists. With a vector source and a threshold on its own scale
        // (cosine, 0.5–0.75), a pure command like "update the price of X" retrieves nothing above
        // the threshold and injects nothing. That holds only when the threshold is on the source's
        // scale: against the default 2 a cosine never clears it (warned about below), and a keyword
        // source scores on another scale altogether. Either way the search itself runs on every
        // message, commands included: for a vector source an embedding call, plus the reranker's
        // time if it has one.
        try
        {
            var request = new MentorRagRequest
            {
                Query          = userMessage,
                MaxResults     = _options.RagResultCount,
                RecentMessages = _recent.ToList(),
                User           = await CurrentUserAsync(ct),
                PageName       = PageContext?.PageName,
                PageData       = PageContext?.ContextData ?? new Dictionary<string, object?>(),
                // Story 157 — the turn's tenant, entered before this search; null for a single tenant.
                TenantId       = MentorTenantContext.Ambient,
            };
            Remember(new ChatMessage(ChatRole.User, userMessage));
            if (request.TenantId is not null) WarnIfTheSourceIsNeverGivenTheTenant();

            var raw = await SearchAsync(request, ct);

            // Apply minimum score threshold — discard low-relevance documents
            // so they are neither injected into the prompt nor shown as source chips.
            var (minScore, minScoreFrom) = EffectiveMinScore();
            _pendingSources = minScore > 0
                ? raw.Where(r => r.Score >= minScore).ToList()
                : raw;

            _logger.LogDebug(
                "[MentorAgent:RAG] Prepared {Count}/{Total} sources for query (minScore={Min}, from {From}).",
                _pendingSources.Count, raw.Count, minScore, minScoreFrom);

            // The source found documents and the threshold discarded every one of them. That is a
            // legitimate outcome for an off-topic question, so it is said once per provider and not
            // once per turn — but it is also exactly what a scale mismatch looks like, and a scale
            // mismatch never fixes itself.
            //
            // RagMinScore defaults to 2, which suits the integer-ish scores of a keyword source. A
            // vector store returns cosine similarity in [0,1], so on the commonest RAG setup of all
            // *nothing can ever clear the default* — the search runs, is paid for, and every
            // document is dropped. Without this line the only trace is a Debug message, and the
            // symptom is an assistant that answers fluently having read nothing (BUG-016's lesson).
            //
            // The top score is the part that makes it actionable: 0.82 against a threshold of 2
            // says "wrong scale" at a glance, where a bare count would not.
            //
            // Only the scale mismatch is warned about: every score below 1 against a threshold of 1
            // or more. A threshold on the source's own scale that discards an off-topic query is the
            // filter doing its job — warning there fired once per visitor and blamed "a threshold
            // above 1" on a threshold of 0.35 (BUG-065).
            //
            // The threshold may come from the host, from the source's DefaultMinScore or from the
            // option's default; the warning names which, because each is fixed in a different place.
            if (!_warnedEverythingDiscarded
                && minScore >= 1 && raw.Count > 0 && _pendingSources.Count == 0
                && raw.Max(r => r.Score) < 1)
            {
                _warnedEverythingDiscarded = true;
                _logger.LogWarning(
                    "[MentorAgent:RAG] The relevance threshold {Min} ({From}) discarded all {Total} document(s) " +
                    "the source returned (highest score seen: {Top}). If your IMentorRagSource is a vector " +
                    "store, its scores are cosine similarity in [0,1] and can never reach a threshold above 1 — " +
                    "the documented range for that case is 0.5 (loose) to 0.75 (strict): declare it on the " +
                    "source as DefaultMinScore, or set RagMinScore. Set RagMinScore to 0 to disable filtering " +
                    "entirely.",
                    minScore.ToString("0.####", CultureInfo.InvariantCulture), minScoreFrom, raw.Count,
                    raw.Max(r => r.Score).ToString("0.####", CultureInfo.InvariantCulture));
            }
        }
        catch (Exception ex)
        {
            // Story 195 — the line names the source, as the metric does.
            if (Faults is not null)
                Faults.Warn(_logger, ex, "[MentorAgent:RAG] The search of {Source} failed — RAG context skipped.", SourceName);
            else
                _logger.LogWarning(ex, "[MentorAgent:RAG] The search of {Source} failed — RAG context skipped.", SourceName);
            _pendingSources = null;
        }
    }

    /// <summary>The source's name, for the log, the metric and the span: its type's full name.</summary>
    private string SourceName => _ragSource.GetType().FullName ?? _ragSource.GetType().Name;

    /// <summary>
    /// Story 195 — the search itself, in its <c>mentor.rag.search</c> span, and a failure counted for monitoring with the
    /// source's name and the exception's type: a failed search used to leave a Warning line only. A source that answers
    /// with no list fails here, by name, rather than later on a null. A Stop during the search is no failure of the source.
    /// </summary>
    private async Task<IReadOnlyList<MentorRagResult>> SearchAsync(MentorRagRequest request, CancellationToken ct)
    {
        var source = SourceName;
        using var span = Telemetry?.StartActivity("mentor.rag.search");
        span?.SetTag(Observability.MentorTelemetry.RagSourceTag, source);
        try
        {
            return await _ragSource.SearchAsync(request, ct)
                   ?? throw new InvalidOperationException(
                       $"{source} answered with no list; a source that finds nothing returns an empty one.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var type = ex.GetType().FullName ?? ex.GetType().Name;
            span?.SetTag(Observability.MentorTelemetry.ErrorTypeTag, type);
            span?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, type);
            if (Telemetry is { } telemetry)
            {
                var tags = Observability.MentorTelemetry.Tags(Observability.MentorTelemetry.RagSourceTag, source);
                tags.Add(Observability.MentorTelemetry.ErrorTypeTag, type);
                telemetry.RagFailures.Add(1, tags);
            }
            throw;
        }
    }

    /// <summary>MentorAgent's instruments, when <c>EnableObservability</c> is on (story 195).</summary>
    internal Observability.MentorTelemetry? Telemetry { get; init; }

    /// <summary>
    /// The threshold this turn applies, and where it came from: <see cref="MentorOptions.RagMinScore"/>
    /// when the host set it, else the source's <see cref="IMentorRagSource.DefaultMinScore"/>, else the
    /// option's default. The host's value wins because it is the one decision made knowing both the
    /// source and the application; a source's is a better default than one number for every scale.
    /// </summary>
    internal (float Value, string From) EffectiveMinScore()
    {
        if (_options.RagMinScoreSetByHost)
            return (_options.RagMinScore, "RagMinScore");
        if (_ragSource.DefaultMinScore is { } declared)
            return (declared, "the source's DefaultMinScore");
        return (_options.RagMinScore, "the RagMinScore default");
    }

    /// <summary>
    /// The signed-in principal, or null. Read the way the prompt's context reads it, through
    /// MentorUserAuthentication (BUG-131); a provider that still throws means there is no user.
    /// </summary>
    private async Task<System.Security.Claims.ClaimsPrincipal?> CurrentUserAsync(CancellationToken ct)
    {
        if (Auth is null) return null;
        try
        {
            return (await Auth.GetAuthenticationStateAsync().WaitAsync(ct).ConfigureAwait(false)).User;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private void Remember(ChatMessage message)
    {
        _recent.Add(message);
        if (_recent.Count > RecentMessageLimit)
            _recent.RemoveRange(0, _recent.Count - RecentMessageLimit);
    }

    /// <summary>
    /// Forgets the conversation this provider has seen, and tells the source (story 196). Called when the session is
    /// reset, when a restored conversation takes its place, and when one the service no longer knows is replaced
    /// (BUG-110): the next search must not be told about messages that are no longer the conversation, and a source that
    /// keeps state for it clears it. A source that throws, or does not answer within <c>SafetyCheckTimeout</c>, is logged
    /// and changes nothing else: the conversation has changed already. A Stop is a Stop.
    /// </summary>
    internal async Task ForgetConversationAsync(
        MentorConversationChangeReason reason, string? sessionId, string? tenantId, CancellationToken ct = default)
    {
        _recent.Clear();
        _pendingSources = null;
        if (!_options.UseRag || !HasRealSource) return;

        var change = new MentorConversationChange { Reason = reason, SessionId = sessionId, TenantId = tenantId };
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(_options.SafetyCheckTimeout);
        try
        {
            await _ragSource.OnConversationChangedAsync(change, bounded.Token).WaitAsync(bounded.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[MentorAgent:RAG] {Source} failed when told the conversation changed ({Reason}) — the conversation " +
                "changed anyway.", SourceName, reason);
        }
    }

    // ── AIContextProvider overrides ────────────────────────────────────────────

    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        // Story 194 — a search that ran to the end and kept nothing, none returned or every one below the threshold, is
        // said to the model with RagNoResultsNotice; a search that failed (null) or the no-op source says nothing.
        if (_pendingSources is { Count: 0 })
            return ValueTask.FromResult(_options.RagNoResultsNotice && HasRealSource
                ? new AIContext { Instructions = NothingFoundHeading + NothingFound }
                : new AIContext());

        if (_pendingSources is not { Count: > 0 })
            return ValueTask.FromResult(new AIContext());

        // Numbered, so the model can point at one. The title stays in the heading because it is
        // what the model reads when deciding whether a document is relevant at all.
        var docs = string.Join("\n\n", _pendingSources.Select((r, i) =>
        {
            var title = r.Title ?? r.SourceUrl ?? "Document";
            return $"[{i + 1}] {title}{MetadataLine(r)}\n{r.Content}";
        }));

        var instructions = _options.RagSystemPromptTemplate
            .Replace("{documents}", docs, StringComparison.OrdinalIgnoreCase);

        // Appended rather than folded into RagSystemPromptTemplate: that template belongs to the
        // host, and a host who replaced it would otherwise silently lose the citations. Measured
        // live on rc.4: the chips showed whatever the retriever returned, so an answer that came
        // from a skill or from the model's own knowledge still carried three "sources" under it.
        // Sources the answer did not use are not sources.
        // In the language of the turn, the one the model answers in (BUG-185).
        instructions += "\n\n" + CiteInstruction;

        return ValueTask.FromResult(new AIContext { Instructions = instructions });
    }

    /// <summary>
    /// The document's <see cref="MentorRagResult.Metadata"/> as one line under its heading, or nothing.
    /// One line whatever the source put in a value: a line break in it would read as the start of the
    /// document's content.
    /// </summary>
    private static string MetadataLine(MentorRagResult result)
    {
        if (result.Metadata is not { Count: > 0 } metadata) return "";

        static string OneLine(string s) =>
            new([.. s.Select(c => char.IsControl(c) ? ' ' : c)]);

        return "\n(" + string.Join("; ", metadata.Select(kv => $"{OneLine(kv.Key)}: {OneLine(kv.Value)}")) + ")";
    }

    protected override ValueTask StoreAIContextAsync(
        InvokedContext context,
        CancellationToken cancellationToken = default)
    {
        // The chips are not raised here any more. This hook runs after the *first* model call of
        // the turn, which on a tool-calling turn is long before the answer exists — and the answer
        // is what says which documents were used. MentorAgentUIMiddleware calls EmitCitedSources
        // once, with the finished text, just before it completes the turn.
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Raises the citation chips for the documents the finished answer actually cited, then forgets
    /// this turn's retrieval. Called once per turn by <c>MentorAgentUIMiddleware</c>, with the
    /// complete reply.
    ///
    /// <para>
    /// No citation, no chips. A retriever returns its best matches for every question, relevant or
    /// not, and cosine similarity has no calibrated zero — so "what the search returned" was never a
    /// truthful answer to "where this came from".
    /// </para>
    /// </summary>
    /// <summary>Set by the registration; see <see cref="TurnFaultLog"/> (E1).</summary>
    internal TurnFaultLog? Faults { get; init; }

    internal void EmitCitedSources(string? answer)
    {
        // The reply joins the conversation the next search is told about — only when RAG is on, as the
        // user's message does (PrepareAsync returns before recording it otherwise).
        if (_options.UseRag && !string.IsNullOrWhiteSpace(answer))
            Remember(new ChatMessage(ChatRole.Assistant, answer));

        var pending = _pendingSources;
        _pendingSources = null;
        if (pending is not { Count: > 0 }) return;

        var cited = new List<MentorRagResult>();
        if (!string.IsNullOrEmpty(answer))
        {
            foreach (System.Text.RegularExpressions.Match m in CitationPattern.Matches(answer))
            {
                foreach (var n in CitedNumbers(m.Groups[1].Value, pending.Count))
                {
                    var doc = pending[n - 1];
                    if (!cited.Contains(doc)) cited.Add(doc);
                }
            }
        }

        if (cited.Count == 0)
        {
            _logger.LogDebug(
                "[MentorAgent:RAG] {Count} document(s) retrieved, none cited — no chips shown.",
                pending.Count);
            return;
        }

        _stateService.NotifyRagSourcesReady(cited);
    }

    /// <summary>
    /// The documents one citation marker names, by number: one ("2"), a list ("1, 3") or a range ("1-3").
    /// A number that names no document cites nothing, and neither does a range with such an end: "[0-9]"
    /// in a code sample is not a citation of every document.
    /// </summary>
    private static IEnumerable<int> CitedNumbers(string marker, int documents)
    {
        foreach (System.Text.RegularExpressions.Match part in CitationPart.Matches(marker))
        {
            var from = int.Parse(part.Groups[1].Value, CultureInfo.InvariantCulture);
            var to = part.Groups[2].Success ? int.Parse(part.Groups[2].Value, CultureInfo.InvariantCulture) : from;
            if (Math.Min(from, to) < 1 || Math.Max(from, to) > documents) continue;   // an invented number cites nothing
            for (var n = Math.Min(from, to); n <= Math.Max(from, to); n++)
                yield return n;
        }
    }

    /// <summary>
    /// Inline citation markers: [1], [2] … — the form the instruction asks for — and the lists and ranges
    /// models also write, [1, 2] and [1-2] (BUG-184).
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex CitationPattern =
        new(@"\[\s*(\d{1,2}(?:\s*[-\u2013]\s*\d{1,2})?(?:\s*,\s*\d{1,2}(?:\s*[-\u2013]\s*\d{1,2})?)*)\s*\]",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>One number or one range inside a marker.</summary>
    private static readonly System.Text.RegularExpressions.Regex CitationPart =
        new(@"(\d{1,2})(?:\s*[-\u2013]\s*(\d{1,2}))?", System.Text.RegularExpressions.RegexOptions.Compiled);
}
