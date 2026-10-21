

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
        _citeInstruction = new Novior.MentorAgent.Abstractions.Core.MentorLocalizer(_options.Language).Get("rag.cite");
    }

    /// <summary>How to cite the documents, appended after the host's template.</summary>
    private readonly string _citeInstruction;

    /// <summary>
    /// Whether a real knowledge base is behind this provider, or the no-op default that
    /// <c>TryAddScoped</c> installs when the host registered none. Read once at startup so
    /// <c>UseRag</c> without a source can be reported instead of silently returning nothing.
    /// </summary>
    internal bool HasRealSource => _ragSource is not NullRagSource;

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
            };
            Remember(new ChatMessage(ChatRole.User, userMessage));

            var raw = await _ragSource.SearchAsync(request, ct);

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
            if (Faults is not null)
                Faults.Warn(_logger, ex, "[MentorAgent:RAG] PrepareAsync failed — RAG context skipped.");
            else
                _logger.LogWarning(ex, "[MentorAgent:RAG] PrepareAsync failed — RAG context skipped.");
            _pendingSources = null;
        }
    }

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
    /// Forgets the conversation this provider has seen. Called when the session is reset or replaced by a
    /// restored one: the next search must not be told about messages that are no longer the conversation.
    /// </summary>
    internal void ForgetConversation()
    {
        _recent.Clear();
        _pendingSources = null;
    }

    // ── AIContextProvider overrides ────────────────────────────────────────────

    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
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
        // In the configured language, the one the model answers in (BUG-185).
        instructions += "\n\n" + _citeInstruction;

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
