using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Novior.MentorAgent.Core;   // HostedToolCatalog, HostedToolBudget (#15)

namespace Novior.MentorAgent.Middleware;

/// <summary>
/// <see cref="DelegatingChatClient"/> that trims the tool list to those semantically relevant to
/// the user's latest message before each LLM call (Wave 1 · #4), cutting the per-call token cost
/// of tool schemas — often the single largest contributor.
///
/// <b>Semantic only.</b> Tool name+description and the user message are embedded and compared by
/// cosine similarity, which ranks tools by meaning across languages. The absolute score is not
/// calibrated, so the threshold cannot decide on its own that a message needs no action: when nothing
/// clears it, the best-ranked actions that need no confirmation still travel (BUG-106).
/// This client is installed by the orchestrator <b>only</b> when an <c>IEmbeddingGenerator</c> is
/// configured; if tool filtering is enabled without one, no filtering happens (all tools are sent).
///
/// Safe by design: only L1 business actions + MCP tools (<c>filterableNames</c>) are ever removed;
/// core tools — navigation, memory, routing, teams, skills, and the UI-action tools added later by
/// <see cref="UIActionsMiddleware"/> — are always kept.
///
/// Must sit OUTERMOST (before <see cref="UIActionsMiddleware"/>) so it never removes UI tools.
/// </summary>
internal sealed class ToolFilterChatClient : DelegatingChatClient
{
    private readonly IReadOnlySet<string> _filterableNames;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddings;
    private readonly float _minScore;

    /// <summary>
    /// Once any action clears the threshold, at least this many best-ranked actions are sent
    /// (<see cref="Options.MentorOptions.ToolFilterMinTools"/>, BUG-099). 0 in tests that build this
    /// client by hand, which then test the threshold alone.
    /// </summary>
    private readonly int _minTools;

    /// <summary>
    /// Whether the application declares a tool as needing confirmation — its way of marking an action
    /// that changes data. When every action that cleared the threshold is such an action, the
    /// <see cref="GatedCompanions"/> best-ranked actions that are not go with them (BUG-099). Null in
    /// tests that build this client by hand, which then test the threshold alone.
    /// </summary>
    private readonly Func<string, bool>? _isConfirmationGated;

    /// <summary>
    /// How many ungated actions travel with a confirmation-gated match. Three: on the question that found
    /// BUG-099 the three actions that read stock ranked right behind <c>update_stock</c>.
    /// </summary>
    internal const int GatedCompanions = 3;

    private readonly int _maxTools;
    private readonly ILogger _logger;

    /// <summary>
    /// Which actions may travel on a turn where none cleared the threshold: those that need no
    /// confirmation and no role — and, from an MCP server, only those it marks read-only (BUG-106). Null in
    /// tests that build this client by hand, which then test the threshold alone.
    /// </summary>
    private readonly Func<string, bool>? _isZeroMatchEligible;

    /// <summary>
    /// <see cref="Options.MentorOptions.RequireLookupForRecordQuestions"/>: which actions the first call of a
    /// record question may be required to call (declared read-only, no confirmation, no role). Null when the
    /// option is off.
    /// </summary>
    private readonly Func<string, bool>? _isForcibleRead;

    /// <summary>
    /// <see cref="Options.MentorOptions.RequireActionForChangeRequests"/>: which actions the first call of a
    /// complete request for one of them may be required to call (those that will ask a person first), and the
    /// core tool a page request may be required to call (<c>navigate_to</c>, when the application has pages).
    /// Null when the option is off.
    /// </summary>
    private readonly Func<string, bool>? _isForcibleAction;
    private readonly string? _navigationTool;

    /// <summary>
    /// The classifier that says what a message asks for, among what its selection could satisfy
    /// (<see cref="Core.RequestShape"/>): null when it could not be told, and the turn then runs as without the
    /// options. Null when both options are off.
    /// </summary>
    private readonly Func<string, Core.RequestShape, CancellationToken, Task<Core.RequestKind?>>? _classify;

    /// <summary>
    /// Core tools that join a forced read or action: <c>route_to_specialist</c> and the teams, so a request
    /// meant for another assistant can still be handed over when the classifier misses that it was (BUG-082's
    /// shape).
    /// </summary>
    private readonly IReadOnlySet<string> _forcedCompanionTools;

    /// <summary>
    /// Marks the one request a turn's first call is forced on, so <see cref="UIActionsMiddleware"/> does not
    /// add its own tools to it: a UI action must not be able to satisfy a required read, action or navigation.
    /// </summary>
    internal const string ForcedCallKey = "mentoragent.forced-call";

    /// <summary>
    /// The confirmation-gated actions the current turn's first call was required to call, or null. Read by the
    /// coordinator's auto-approval rules, which answer no for them (<c>MentorOrchestrator.NeverForForcedCalls</c>):
    /// a required call is always shown to a person. Cleared by every new message, kept through the turn's
    /// follow-ups and approval rounds.
    /// </summary>
    internal IReadOnlySet<string>? ForcedActions { get; private set; }

    /// <summary>
    /// The actions the previous user message selected — by the threshold, the BUG-099 companions or the
    /// zero-match fallback — carried onto the next message when that one matches nothing. The answer to the
    /// assistant's own question ("Sì, procedi" scores 0.29 against <c>cancel_order</c>) used to arrive with
    /// none of the actions the question was about, and the model, with no tool, could only say it could not,
    /// or claim it had (OSS-01). One turn back only, and never what was itself carried.
    /// </summary>
    private List<string>? _previousTurnActions;

    /// <summary>Forgets the carried selection: a new conversation, or a restored one, has no previous turn.</summary>
    internal void ForgetPreviousTurn() => _previousTurnActions = null;

    // -- Hosted tools (#15) -----------------------------------------------------
    private readonly bool              _filterHosted;
    private readonly float             _hostedMinScore;
    private readonly int               _maxHostedCalls;
    private readonly HostedToolBudget? _budget;
    private bool                       _budgetLogged;

    /// <summary>
    /// Answers "is this message about this application at all?" — a different question from "does it
    /// need this tool", and the one that stops a shop assistant from paying to draw a dog. Consulted
    /// only when a hosted tool has already passed relevance, so it costs nothing on ordinary turns.
    /// </summary>
    private readonly HostedToolDomainGate? _domainGate;

    /// <summary>
    /// Default threshold for hosted tools, deliberately below <c>ToolFilterMinScore</c>. Their scores
    /// live on a different scale — long English descriptions against a short message in the user's
    /// language — and this filter is only a coarse pre-cut now that the domain check makes the real
    /// decision. Measured: 0.04–0.08 for irrelevant tools, 0.29–0.35 for the relevant one, where a 0.35
    /// cut-off flipped between two rewordings of the same request.
    /// </summary>
    private const float DefaultHostedMinScore = 0.15f;

    // Tool embeddings are computed once (per connection) and cached by tool name.
    private readonly ConcurrentDictionary<string, ReadOnlyMemory<float>> _toolEmbeddings = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _embLock = new(1, 1);

    /// <summary>
    /// Names selected for the turn in progress, replayed on every follow-up request inside the
    /// tool-calling loop. A tool the model has already called must stay declared until the turn
    /// ends, otherwise the provider rejects its result with
    /// <c>HTTP 400 — No tool call found for function call output with call_id …</c>.
    /// Safe as instance state: the client is scoped per user and one turn runs at a time.
    /// </summary>
    private HashSet<string>? _turnToolNames;

    /// <summary>Set by <see cref="BeginUserTurn"/>; consumed by the next request.</summary>
    private bool _userTurnStarting;

    /// <summary>The tools of approval requests a stopped turn left open, declared on the next turn's first request.</summary>
    private IReadOnlyCollection<string>? _leftOpenCalls;

    /// <summary>
    /// Per-request instructions telling the model that the hosted capabilities its system prompt
    /// advertises are not available on this turn. Replayed for the whole turn alongside
    /// <see cref="_turnToolNames"/>, so a final message written after a local tool call is covered too.
    /// </summary>
    private string? _turnHostedNotice;

    /// <summary>
    /// What a tool is matched on, by name, when it is not simply "name: description": the
    /// application's actions are matched on what they DO, not on the <c>ProactiveHint</c> guidance
    /// the model also reads in their description (rc.11) — guidance about what to suggest next says
    /// nothing about whether the tool fits the question.
    /// </summary>
    private readonly IReadOnlyDictionary<string, string>? _relevanceTexts;

    private string RelevanceText(AITool tool)
        => _relevanceTexts is not null && _relevanceTexts.TryGetValue(tool.Name, out var text)
            ? text
            : $"{tool.Name}: {tool.Description}";

    /// <summary>
    /// What an action does, for the request classifier: its relevance text without the name in front — so
    /// without the <c>ProactiveHint</c> guidance, which tells the assistant what to suggest or ask, not what
    /// the action needs.
    /// </summary>
    private string Purpose(AITool tool)
    {
        var text = RelevanceText(tool);
        var prefix = tool.Name + ": ";
        return text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? text[prefix.Length..] : text;
    }

    /// <summary>Why the hosted tools were taken away — the model is told which, in plain words.</summary>
    private enum WithholdReason { None, OutOfScope, BudgetSpent }

    public ToolFilterChatClient(
        IChatClient innerClient,
        IReadOnlySet<string> filterableNames,
        IEmbeddingGenerator<string, Embedding<float>> embeddings,
        float minScore,
        int maxTools,
        ILogger logger,
        bool filterHosted = false,
        float? hostedMinScore = null,
        int maxHostedCalls = 0,
        HostedToolBudget? budget = null,
        HostedToolDomainGate? domainGate = null,
        Core.MentorEmbeddingCache? catalogue = null,
        Core.TurnQueryEmbedding? queryEmbedding = null,
        Core.TurnFaultLog? faults = null,
        IReadOnlyDictionary<string, string>? relevanceTexts = null,
        int minTools = 0,
        Func<string, bool>? isConfirmationGated = null,
        Func<string, bool>? isZeroMatchEligible = null,
        Func<string, bool>? isForcibleRead = null,
        Func<string, bool>? isForcibleAction = null,
        string? navigationTool = null,
        Func<string, Core.RequestShape, CancellationToken, Task<Core.RequestKind?>>? classify = null,
        IReadOnlySet<string>? forcedCompanionTools = null) : base(innerClient)
    {
        _isZeroMatchEligible = isZeroMatchEligible;
        _isForcibleRead      = isForcibleRead;
        _isForcibleAction    = isForcibleAction;
        _navigationTool      = navigationTool;
        _classify            = classify;
        _forcedCompanionTools = forcedCompanionTools ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _minTools       = minTools < 0 ? 0 : minTools;
        _isConfirmationGated = isConfirmationGated;
        _faults         = faults;
        _relevanceTexts = relevanceTexts;
        _filterableNames = filterableNames;
        _embeddings = embeddings;
        _minScore = minScore;
        _maxTools = maxTools < 1 ? 1 : maxTools;
        _logger = logger;
        _filterHosted   = filterHosted;
        _hostedMinScore = hostedMinScore ?? DefaultHostedMinScore;
        _maxHostedCalls = maxHostedCalls;
        _budget         = budget;
        _domainGate     = domainGate;
        _catalogue      = catalogue;
        _queryEmbedding = queryEmbedding;
    }

    /// <summary>
    /// Process-wide cache for the tool descriptions: they are the same text for every user, and were
    /// being re-embedded once per scope. Null in tests that construct this client by hand, where the
    /// per-instance dictionary below is the whole cache.
    /// </summary>
    private readonly Core.MentorEmbeddingCache? _catalogue;

    /// <summary>The turn's query vector, shared with routing and memory instead of embedded again.</summary>
    private readonly Core.TurnQueryEmbedding? _queryEmbedding;

    /// <summary>One stack trace per cause per turn (E1); null when built by hand in a test.</summary>
    private readonly Core.TurnFaultLog? _faults;

    private void Fault(Exception ex, string message)
    {
        if (_faults is not null) _faults.Warn(_logger, ex, message);
        else _logger.LogWarning(ex, message);
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var filtered = await FilterAsync(messages, options, cancellationToken);
        return await base.GetResponseAsync(messages, filtered, cancellationToken);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var filtered = await FilterAsync(messages, options, cancellationToken);
        await foreach (var update in base.GetStreamingResponseAsync(messages, filtered, cancellationToken))
            yield return update;
    }

    /// <summary>
    /// Tells the filter that the next request opens a user turn; the orchestrator calls it before every turn.
    /// The answers to approval requests a stopped turn left open (BUG-116) are appended by the function-invoking
    /// client AFTER the new message, so that request ends on a tool result and would otherwise be taken for the
    /// continuation of the stopped turn: its selection replayed, the new message neither ranked nor forced, and
    /// the carry-over two messages back. <paramref name="leftOpenCalls"/> are those requests' tools, declared on
    /// this first request beside the new selection, as a continuation declares a tool already called.
    /// </summary>
    internal void BeginUserTurn(IReadOnlyCollection<string>? leftOpenCalls = null)
    {
        _userTurnStarting = true;
        ForcedActions = null;
        _leftOpenCalls = leftOpenCalls is { Count: > 0 } ? leftOpenCalls : null;
    }

    private async Task<ChatOptions?> FilterAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options, CancellationToken ct)
    {
        // Consumed first, so no early return below leaves it set for a request that does continue the turn.
        var startsTurn = _userTurnStarting;
        var leftOpen   = _leftOpenCalls;
        _userTurnStarting = false;
        _leftOpenCalls    = null;

        var tools = options?.Tools;
        if (tools is null || tools.Count == 0)
            return options;

        // Partition into always-keep (core), removable function candidates (L1 + MCP) and, when
        // enabled, provider-hosted tools — the expensive ones, historically the only group that
        // slipped through untouched because they are not AIFunctions and carry no description.
        var alwaysKeep = new List<AITool>();
        var candidates = new List<AIFunction>();
        var hosted     = new List<(AITool Tool, string Key, string Text)>();

        foreach (var tool in tools)
        {
            if (tool is AIFunction f && _filterableNames.Contains(f.Name))
            {
                candidates.Add(f);
            }
            // Hosted tools are set aside when EITHER cost control is active. Gating this on
            // FilterHostedTools alone would leave the domain check and the session budget silently
            // inert whenever relevance filtering is off — a switch that turns itself off is exactly
            // the kind of false confidence these controls exist to remove.
            else if ((_filterHosted || _domainGate is not null || _maxHostedCalls > 0)
                     && HostedToolCatalog.Describe(tool) is { } d)
            {
                hosted.Add((tool, d.Key, d.Text));
            }
            else
            {
                alwaysKeep.Add(tool);
            }
        }

        var total = alwaysKeep.Count + candidates.Count + hosted.Count;
        var list  = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();

        // Nothing to trim: no application action and no hosted tool. A page request can still be required to
        // navigate — an application whose actions all live in specialists or teams has pages too.
        if (candidates.Count == 0 && hosted.Count == 0)
            return await PageRequestAlone(list, options!, alwaysKeep, startsTurn, leftOpen, ct) ?? options;

        // Mid-turn: the model is being handed the result of a tool it already called. Re-filtering
        // here would drop that very tool — the follow-up carries no user message to match against —
        // and the provider would then reject the result. Replay the turn's selection instead.
        if (!startsTurn && TurnContinuation.IsToolResult(list) && _turnToolNames is { } turnNames)
        {
            var replayed = new List<AITool>(alwaysKeep.Count + turnNames.Count);
            replayed.AddRange(alwaysKeep);
            replayed.AddRange(candidates.Where(f => turnNames.Contains(f.Name)));
            replayed.AddRange(hosted.Where(h => turnNames.Contains(h.Key)).Select(h => h.Tool));

            _logger.LogDebug(
                "[MentorAgent] Tool filtering: {Kept}/{Total} tools sent (turn continuation — selection replayed).",
                replayed.Count, total);

            // The notice travels with the replay: the model writes its final answer on one of these
            // follow-up requests, and that is where it would otherwise invent the missing result.
            return CloneWithTools(options!, replayed, _turnHostedNotice);
        }

        var message = list.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;
        ForcedActions = null;   // a new message: whatever a previous one required is over

        if (string.IsNullOrWhiteSpace(message))
        {
            _turnToolNames    = [];
            _turnHostedNotice = null;
            _previousTurnActions = [];
            _logger.LogDebug("[MentorAgent] Tool filtering: {Kept}/{Total} tools sent (empty message → core only).",
                alwaysKeep.Count, total);
            return CloneWithTools(options!, alwaysKeep, null);
        }

        try
        {
            await EnsureToolEmbeddingsAsync(
                [.. candidates.Select(f => (Key: f.Name, Text: RelevanceText(f))),
                 .. _filterHosted ? hosted.Select(h => (h.Key, h.Text)) : []],
                ct);

            var query = _queryEmbedding is not null
                ? await _queryEmbedding.GetAsync(message, _embeddings, ct)
                : (await _embeddings.GenerateAsync([message], cancellationToken: ct))[0].Vector;

            // Ranked first, cut second. The threshold says whether this message needs an action at
            // all; the ranking says which ones travel. A single action above the line used to be sent
            // alone — and when it was update_stock, for a question about how many units were in
            // stock, the model called it with 0 (BUG-099). The next best-ranked actions go with it.
            var ranked = candidates
                .Select(f => (Tool: f, Score: Score(query.Span, f.Name)))
                .OrderByDescending(x => x.Score)
                .ToList();
            var aboveLine = ranked.Count(x => x.Score >= _minScore);
            var keep = aboveLine == 0 ? 0 : Math.Min(_maxTools, Math.Max(aboveLine, _minTools));
            var matched = ranked.Take(keep).Select(x => x.Tool).ToList();
            if (keep > aboveLine)
                _logger.LogDebug(
                    "[MentorAgent] Tool filtering: {Above} action(s) above minScore={Min}; the next {Extra} by rank " +
                    "sent with them ({Names}).",
                    aboveLine, _minScore, keep - aboveLine,
                    string.Join(", ", ranked.Skip(aboveLine).Take(keep - aboveLine).Select(x => x.Tool.Name + " " + x.Score.ToString("F3", System.Globalization.CultureInfo.InvariantCulture))));

            // BUG-099, the structural half. Confirmation is how an application marks an action that
            // changes data. Sent alone, such an action was all the model had for a read question — asked
            // how many units were in stock, it called update_stock with 0. So when every action that
            // cleared the line needs confirmation, the best-ranked actions that do not travel with them.
            // A lone match that needs no confirmation is left alone: that was the right search for
            // "how much has Anna Ferrari spent?", and company for it cost the right answer (rc.12 floor).
            if (_isConfirmationGated is not null && matched.Count > 0 && matched.All(t => _isConfirmationGated(t.Name)))
            {
                var companions = ranked
                    .Where(x => !_isConfirmationGated(x.Tool.Name) && !matched.Contains(x.Tool))
                    .Take(Math.Max(0, Math.Min(GatedCompanions, _maxTools - matched.Count)))
                    .ToList();
                if (companions.Count > 0)
                {
                    matched.AddRange(companions.Select(x => x.Tool));
                    _logger.LogDebug(
                        "[MentorAgent] Tool filtering: every action above minScore={Min} needs confirmation ({Gated}); " +
                        "the {Count} best-ranked that do not were sent with them ({Names}).",
                        _minScore,
                        string.Join(", ", matched.Take(matched.Count - companions.Count).Select(t => t.Name)),
                        companions.Count,
                        string.Join(", ", companions.Select(x => x.Tool.Name + " " + x.Score.ToString("F3", System.Globalization.CultureInfo.InvariantCulture))));
                }
            }

            // BUG-106, the zero-match side. The threshold is an absolute cosine line, and cosine is not
            // calibrated: the read that answers "Quanto costa il Dell XPS 15?" scored 0.209, below "Ciao" at
            // 0.280 — no line separates a question that needs data from a greeting. With no action sent the
            // model invented a price range (2 of 12) or said the catalogue did not have it. So when nothing
            // clears the line, the best-ranked actions that need no confirmation travel anyway: ranking only,
            // which is what cosine is good for, and never a gated action (BUG-099's write-on-read). About 80
            // input tokens, on greetings too.
            if (aboveLine == 0 && ranked.Count > 0)
            {
                var fallback = _isZeroMatchEligible is { } eligible
                    ? ranked.Where(x => eligible(x.Tool.Name)).Take(Math.Min(GatedCompanions, _maxTools)).ToList()
                    : [];
                matched.AddRange(fallback.Select(x => x.Tool));

                _logger.LogDebug(
                    "[MentorAgent] Tool filtering: no action cleared minScore={Min} (best: {Best}); {Sent}.",
                    _minScore,
                    ranked[0].Tool.Name + " " + ranked[0].Score.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                    _isZeroMatchEligible is null
                        ? "core tools only"
                        : fallback.Count == 0
                            ? "of the ranked actions none needs no confirmation — core tools only"
                            : $"the {fallback.Count} best-ranked that need no confirmation were sent: " +
                              string.Join(", ", fallback.Select(x => x.Tool.Name + " " +
                                  x.Score.ToString("F3", System.Globalization.CultureInfo.InvariantCulture))));
            }

            // What THIS message selected is what the next one may carry — not what it carried itself, so a
            // write the user asked for travels one turn further at most.
            var selectedByThisMessage = matched.Select(f => f.Name).ToList();

            // OSS-01: the answer to the assistant's own question ("Sì, procedi", the reason it asked for)
            // matches no action, and arrived with none of the actions the question was about. On such a
            // message the previous message's selection travels again: ranking was done on that message, the
            // model sees both in the history. A confirmation-gated action still never travels alone (BUG-099).
            if (aboveLine == 0 && _previousTurnActions is { Count: > 0 } previous)
            {
                var carried = previous
                    .Select(n => candidates.FirstOrDefault(f => string.Equals(f.Name, n, StringComparison.OrdinalIgnoreCase)))
                    .OfType<AIFunction>()
                    .Where(f => !matched.Contains(f))
                    .Take(Math.Max(0, _maxTools - matched.Count))
                    .ToList();
                if (_isConfirmationGated is not null && carried.Count > 0
                    && !matched.Concat(carried).Any(f => !_isConfirmationGated(f.Name)))
                    carried.Clear();
                if (carried.Count > 0)
                {
                    matched.AddRange(carried);
                    _logger.LogDebug(
                        "[MentorAgent] Tool filtering: carried from the previous message: {Names}.",
                        string.Join(", ", carried.Select(f => f.Name)));
                }
            }
            _previousTurnActions = selectedByThisMessage;

            // RequireLookupForRecordQuestions (BUG-106 residue): with read actions on the turn the model still
            // offered to look the price up about one time in three — and, with a read that cleared the threshold
            // on its own, wrote "price not available" without calling it. Stronger wording made it invent
            // figures. When the classifier says this message asks for a value of the records, the first call
            // must be one of the declared reads THIS message selected: the one that cleared the line, the BUG-099
            // companions, or the zero-match fallback. Never reads it did not select — with a lone right match,
            // company was what cost "Anna Ferrari" her answer (rc.12) — and never what it only carried.
            //
            // RequireActionForChangeRequests (BUG-118): "Stock aggiornato" with no tool called, about one time in
            // five, and "Ecco la pagina Prodotti" without navigating. When the classifier says this message asks
            // for one of the confirmation-gated actions it selected and gives every input that action needs, the
            // first call must be one of them, so the banner comes before any sentence; when it asks for a page,
            // navigate_to. The classifier is shown those actions with their inputs: forced on a request that left
            // one out, the call would carry a value nobody gave — and an action whose inputs cannot be shown
            // faithfully is not offered at all. Same selection rule as the reads: never an action this message did
            // not select, never what it only carried.
            //
            // Never on a turn that answers what a stopped turn left open (BUG-116): its requests are declared for
            // their answers, and a required call could be satisfied by calling the stopped action again — the
            // banner the user stopped would come back. That turn runs as it would without the options.
            List<AITool>? forced = null;
            if (_classify is not null && leftOpen is null)
            {
                List<AIFunction> reads = _isForcibleRead is null
                    ? []
                    : [.. matched
                          .Where(f => selectedByThisMessage.Contains(f.Name) && _isForcibleRead(f.Name))
                          .Take(GatedCompanions)];
                var actions = new List<AIFunction>();
                var offered = new List<string>();
                if (_isForcibleAction is not null)
                    foreach (var f in matched.Where(f => selectedByThisMessage.Contains(f.Name) && _isForcibleAction(f.Name)))
                    {
                        if (Core.RequestClassifier.Describe(f.Name, Purpose(f), f.JsonSchema) is { } line)
                        {
                            actions.Add(f);
                            offered.Add(line);
                        }
                        else
                            _logger.LogDebug(
                                "[MentorAgent] Tool filtering: {Name} is never a required call — its input schema cannot be " +
                                "shown to the request classifier, and a request could not be told complete.", f.Name);
                    }
                var navigation = _navigationTool is null
                    ? null
                    : alwaysKeep.FirstOrDefault(t => string.Equals(t.Name, _navigationTool, StringComparison.OrdinalIgnoreCase));

                var shape = new Core.RequestShape(reads.Count > 0, offered, navigation is not null);

                if (!shape.IsEmpty)
                {
                    var companions = alwaysKeep.Where(t => _forcedCompanionTools.Contains(t.Name)).ToList();
                    var kind = await _classify(message, shape, ct);
                    string? what = null;
                    if (kind == Core.RequestKind.Lookup && reads.Count > 0)
                    {
                        forced = [.. reads, .. companions];
                        what = "asks for a record value — the first call must read";
                    }
                    else if (kind == Core.RequestKind.Action && actions.Count > 0)
                    {
                        forced = [.. actions, .. companions];
                        ForcedActions = new HashSet<string>(actions.Select(a => a.Name), StringComparer.OrdinalIgnoreCase);
                        what = "asks for an action and gives everything it needs — the first call must be one of them";
                    }
                    else if (kind == Core.RequestKind.Navigate && navigation is not null)
                    {
                        forced = [navigation];
                        what = "asks to go to a page — the first call must navigate";
                    }

                    if (forced is not null)
                        _logger.LogDebug("[MentorAgent] Tool filtering: the message {What} ({Names}).",
                            what, string.Join(", ", forced.Select(t => t.Name)));
                }
            }

            // BUG-116: the request a stopped turn left open is answered on this request, so its tool stays
            // declared, as on any continuation — never carried, and this request is never forced (above).
            if (leftOpen is not null)
            {
                var open = candidates.Where(f => leftOpen.Contains(f.Name) && !matched.Contains(f)).ToList();
                if (open.Count > 0)
                {
                    matched.AddRange(open);
                    _logger.LogDebug(
                        "[MentorAgent] Tool filtering: declared for the request left open by the previous turn: {Names}.",
                        string.Join(", ", open.Select(f => f.Name)));
                }
            }

            // Hosted tools are scored the same way but not subject to ToolFilterMaxTools: that budget
            // exists to cap a long list of application actions, while these are a handful of distinct
            // capabilities — dropping one because five business tools ranked higher makes no sense.
            var hostedMatched = new List<(AITool Tool, string Key)>();
            foreach (var h in hosted)
            {
                // Relevance filtering off: the tool passes straight to the domain check and the
                // budget, which are the reasons it was set aside in the first place.
                if (!_filterHosted)
                {
                    hostedMatched.Add((h.Tool, h.Key));
                    continue;
                }

                var score = Score(query.Span, h.Key);
                if (score >= _hostedMinScore)
                    hostedMatched.Add((h.Tool, h.Key));

                // Logged either way: a hosted tool that never fires is otherwise indistinguishable
                // from one the model chose not to use, and this is the number you tune against.
                _logger.LogDebug("[MentorAgent] Hosted tool relevance: {Key} scored {Score:F3} (min {Min}) — {Verdict}.",
                    h.Key, score, _hostedMinScore, score >= _hostedMinScore ? "declared" : "skipped");
            }

            // Budget and domain gate both run here, after scoring rather than before it, so they only
            // fire when the user actually asked for something a hosted tool would have served. Judging
            // them earlier cost nothing extra (the query embedding is paid for the application tools
            // anyway) but made it impossible to tell "withheld a capability the user wanted" from
            // "declared nothing, as on every ordinary turn" — and that distinction is what the notice
            // below is built on. Budget first: an exhausted budget must not pay for a classifier call.
            var reason = WithholdReason.None;

            if (hostedMatched.Count > 0 && _budget is not null && _budget.IsExhausted(_maxHostedCalls))
            {
                reason = WithholdReason.BudgetSpent;
                if (!_budgetLogged)
                {
                    _budgetLogged = true;
                    _logger.LogInformation(
                        "[MentorAgent] Hosted-tool budget spent for this session ({Used}/{Max}) — they are no " +
                        "longer declared. Raise options.MaxHostedToolCallsPerSession to allow more.",
                        _budget.Used, _maxHostedCalls);
                }
                _logger.LogInformation(
                    "[MentorAgent] Hosted tools withheld: the session budget is spent. {Tools} would have run.",
                    string.Join(", ", hostedMatched.Select(h => h.Key)));
                hostedMatched.Clear();
            }
            // Domain gate — deliberately last, and only when something costly actually survived the
            // scoring. Asking "is this about my application?" is a model call, so it is paid for on the
            // handful of turns where a paid tool is about to run, never on ordinary conversation.
            else if (hostedMatched.Count > 0 && _domainGate is not null
                     && !await _domainGate.IsInScopeAsync(message, ct))
            {
                reason = WithholdReason.OutOfScope;
                _logger.LogInformation(
                    "[MentorAgent] Hosted tools withheld: the request is outside this application's scope. " +
                    "{Tools} would have run.", string.Join(", ", hostedMatched.Select(h => h.Key)));
                hostedMatched.Clear();
            }

            // Taking the tool away is only half the job. The system prompt has already told the model
            // it can generate images and search the web, and that text is static — so a model that
            // wants the capability and cannot find it resolves the contradiction by pretending: the
            // reported turn answered "genera immagine di un cane" with an invented URL presented as
            // an image it had just created. It has to be told, in the same request, that the
            // capability is gone and why.
            _turnHostedNotice = BuildWithheldNotice(reason);

            var filtered = new List<AITool>(alwaysKeep.Count + matched.Count + hostedMatched.Count);
            filtered.AddRange(alwaysKeep);
            filtered.AddRange(matched);
            filtered.AddRange(hostedMatched.Select(h => h.Tool));

            // Remember the selection so the rest of the turn keeps declaring the same tools. Hosted
            // keys carry a prefix, so they can share this set with function names without colliding.
            _turnToolNames = [.. matched.Select(f => f.Name), .. hostedMatched.Select(h => h.Key)];

            // candidates/hosted are logged alongside the totals: a tool missing from filterableNames
            // lands in alwaysKeep and is then sent on every single turn, which is invisible unless the
            // three groups are printed next to each other and made to add up.
            _logger.LogDebug(
                "[MentorAgent] Tool filtering: {Kept}/{Total} tools sent ({Core} core + {Matched}/{Candidates} " +
                "matched + {Hosted}/{HostedTotal} hosted, minScore={Min}).",
                filtered.Count, total, alwaysKeep.Count, matched.Count, candidates.Count,
                hostedMatched.Count, hosted.Count, _minScore);

            return forced is not null
                ? Forced(options!, forced, _turnHostedNotice)
                : CloneWithTools(options!, filtered, _turnHostedNotice);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Embedding failure (rate limit, network) → keep all tools, hosted included. Fail-open:
            // a transient embedding outage must not silently strip capabilities from the assistant.
            // No notice either: nothing was withheld, so there is nothing to warn the model about.
            Fault(ex, "[MentorAgent] Tool filtering failed to embed — sending all tools.");
            _turnToolNames    = [.. candidates.Select(f => f.Name), .. hosted.Select(h => h.Key)];
            _turnHostedNotice = null;
            _previousTurnActions = null;
            return options;
        }
    }

    /// <summary>
    /// The first request of a turn with only <paramref name="tools"/> declared and a call to one of them required.
    /// The marker tells <see cref="UIActionsMiddleware"/> to add none of its own, and is removed there.
    /// </summary>
    private static ChatOptions Forced(ChatOptions options, List<AITool> tools, string? notice)
    {
        var first = CloneWithTools(options, tools, notice);
        first.ToolMode = ChatToolMode.RequireAny;
        first.AdditionalProperties ??= [];
        first.AdditionalProperties[ForcedCallKey] = true;
        return first;
    }

    /// <summary>
    /// A request with nothing to trim — no application action, no hosted tool — on an application with pages:
    /// a page request is still required to navigate (<see cref="Options.MentorOptions.RequireActionForChangeRequests"/>).
    /// Null when it is not one, or cannot be: no classifier or no page, a follow-up of the turn, an empty message,
    /// or a turn that answers what a stopped turn left open.
    /// </summary>
    private async Task<ChatOptions?> PageRequestAlone(
        IReadOnlyList<ChatMessage> list, ChatOptions options, List<AITool> alwaysKeep,
        bool startsTurn, IReadOnlyCollection<string>? leftOpen, CancellationToken ct)
    {
        if (_classify is null || _navigationTool is null || leftOpen is not null) return null;
        if (!startsTurn && TurnContinuation.IsToolResult(list)) return null;

        var navigation = alwaysKeep.FirstOrDefault(t => string.Equals(t.Name, _navigationTool, StringComparison.OrdinalIgnoreCase));
        var message = list.LastOrDefault(m => m.Role == ChatRole.User)?.Text;
        if (navigation is null || string.IsNullOrWhiteSpace(message)) return null;

        if (await _classify(message, new Core.RequestShape(false, [], true), ct) != Core.RequestKind.Navigate) return null;

        _logger.LogDebug("[MentorAgent] Tool filtering: the message asks to go to a page — the first call must navigate ({Name}).",
            navigation.Name);
        return Forced(options, [navigation], null);
    }

    /// <summary>
    /// The per-request instructions that replace a withheld capability with an honest refusal.
    ///
    /// <para>
    /// Delivered through <see cref="ChatOptions.Instructions"/> rather than as an extra system
    /// message on purpose: under service-managed history a message is <i>stored</i> in the
    /// conversation, so "image generation is unavailable" would silently follow the user into every
    /// later turn. Per-request instructions apply to this response only.
    /// </para>
    /// </summary>
    private static string? BuildWithheldNotice(WithholdReason reason)
    {
        if (reason == WithholdReason.None) return null;

        var (why, redirect) = reason switch
        {
            // The cost gate's verdict is about the CAPABILITIES, not the request (BUG-105/106): it is strict
            // on purpose, and it said OUT on catalogue price questions the refusal had answered and the read
            // actions could — the notice then told the model the request was outside the application.
            WithholdReason.OutOfScope => (
                "they are not for this request",
                " — and if the request is about this application, answer it with this application's own actions"),
            _ => (
                "the limit set for this session has been reached",
                " and suggest trying again later"),
        };

        return $"""
            ## Capability status for THIS request — MANDATORY, overrides the capability list above
            The built-in capabilities (web search, code interpreter, file search, image generation,
            remote tools) are NOT available on this turn: {why}. You have no way to produce that kind
            of result now, and none will become available later in this turn.
            If the user asked for one, reply with ONE short sentence saying plainly that you cannot do
            it{redirect}. Do not describe a plan, do not say you are about to do it, do not promise it.
            NEVER substitute a result you did not receive from a tool in this conversation: no invented
            or remembered URL, no Markdown image, no stock photo, no made-up search result, no guessed
            number. Answering with a fabricated result is a worse failure than refusing.
            """;
    }

    private float Score(ReadOnlySpan<float> query, string key)
        => _toolEmbeddings.TryGetValue(key, out var e) ? Cosine(query, e.Span) : 0f;

    private async Task EnsureToolEmbeddingsAsync(
        IReadOnlyList<(string Key, string Text)> entries, CancellationToken ct)
    {
        var missing = entries.Where(e => !_toolEmbeddings.ContainsKey(e.Key)).ToList();
        if (missing.Count == 0) return;

        await _embLock.WaitAsync(ct);
        try
        {
            missing = entries.Where(e => !_toolEmbeddings.ContainsKey(e.Key)).ToList();
            if (missing.Count == 0) return;

            // The catalogue cache answers for every text this process has already embedded — and the
            // catalogue is identical for every user, so from the second scope onwards there is no
            // provider call at all. Without it (tests that build this client by hand) the generator
            // is used directly, exactly as before.
            var texts = missing.Select(m => m.Text).ToList();
            var vectors = _catalogue is not null
                ? await _catalogue.GetOrAddAsync(texts, _embeddings, ct)
                : [.. (await _embeddings.GenerateAsync(texts, cancellationToken: ct)).Select(e => e.Vector)];

            for (int i = 0; i < missing.Count; i++)
                _toolEmbeddings[missing[i].Key] = vectors[i];
        }
        finally
        {
            _embLock.Release();
        }
    }

    private static float Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0f;
        float dot = 0f, na = 0f, nb = 0f;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na  += a[i] * a[i];
            nb  += b[i] * b[i];
        }
        if (na == 0f || nb == 0f) return 0f;
        return dot / (MathF.Sqrt(na) * MathF.Sqrt(nb));
    }

    /// <summary>
    /// Copies the options and swaps the tool list.
    ///
    /// <para>
    /// Uses <see cref="ChatOptions.Clone"/> rather than copying properties by hand: a hand-written
    /// copy silently loses whatever the abstraction adds next, and losing
    /// <see cref="ChatOptions.ConversationId"/> in particular breaks service-managed history — the
    /// follow-up request drops <c>previous_response_id</c>, so the provider cannot match the tool
    /// result to its call and fails the turn with
    /// <c>HTTP 400 — No tool call found for function call output with call_id …</c>.
    /// </para>
    /// </summary>
    /// <param name="notice">
    /// Appended to the agent's own instructions rather than replacing them — the coordinator prompt
    /// arrives here in <see cref="ChatOptions.Instructions"/> (set by <c>ChatClientAgentOptions</c>),
    /// and overwriting it would drop the entire persona, security block and page list for the turn.
    /// </param>
    /// <param name="src">The options of the request being filtered; left unchanged.</param>
    /// <param name="tools">The tools this request carries.</param>
    private static ChatOptions CloneWithTools(ChatOptions src, List<AITool> tools, string? notice)
    {
        var clone = src.Clone();
        clone.Tools = tools;

        if (!string.IsNullOrEmpty(notice))
            clone.Instructions = string.IsNullOrEmpty(clone.Instructions)
                ? notice
                : clone.Instructions + "\n\n" + notice;

        return clone;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _embLock.Dispose();
        base.Dispose(disposing);
    }
}
