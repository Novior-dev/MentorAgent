using System.Runtime.CompilerServices;
using Novior.MentorAgent.Options;

namespace Novior.MentorAgent.Middleware;

/// <summary>
/// <see cref="DelegatingChatClient"/> that routes each turn to a <b>cheap</b> model (the inner client)
/// or a <b>strong</b> model (roadmap #11) — a real cost lever: small model for simple turns, escalate
/// only when needed. The decision uses a pluggable <see cref="MentorRoutingStrategy"/>:
/// <list type="bullet">
/// <item><b>Custom</b> — developer predicate (async or legacy sync).</item>
/// <item><b>Semantic</b> — embedding cosine vs "complex" exemplars (multilingual, ~free, no keywords).</item>
/// <item><b>Classifier</b> — a tiny LLM call labels the turn SIMPLE/COMPLEX.</item>
/// <item><b>Cascade</b> — serve on cheap, judge completeness, re-run on strong only if it fell short.</item>
/// </list>
/// Installed innermost (it selects the actual model) so every outer wrapper — token counter,
/// observability — measures whichever model actually served the call. Only the latest user message
/// drives the decision, so tool-result rounds route consistently within a turn.
/// </summary>
internal sealed class RoutingChatClient : DelegatingChatClient
{
    // Built-in multilingual exemplars for Semantic routing (modern embedding models are cross-lingual).
    internal static readonly string[] DefaultComplexExemplars =
    [
        "Analyze this in depth and explain your reasoning step by step.",
        "Compare several options and give a detailed, justified recommendation.",
        "Write a long, well-structured document, report, or essay.",
        "Solve this complex, multi-step problem or calculation.",
        "Write, review, or debug source code and explain the fix.",
        "Summarize and synthesize a large amount of information.",
    ];

    private readonly IChatClient _strong;
    private readonly MentorRoutingStrategy _strategy;
    private readonly ILogger _logger;

    // Custom
    private readonly Func<IReadOnlyList<ChatMessage>, CancellationToken, Task<bool>>? _custom;

    // Semantic
    private readonly IEmbeddingGenerator<string, Embedding<float>>? _embeddings;
    private readonly IReadOnlyList<string> _exemplars;
    private readonly float _threshold;
    private ReadOnlyMemory<float>[]? _exemplarEmbeddings;
    private readonly SemaphoreSlim _embLock = new(1, 1);

    // Classifier / Cascade
    private readonly IChatClient _classifier;

    /// <summary>
    /// The model chosen for the turn in progress, reused for every follow-up call inside the
    /// tool-calling loop (see <see cref="TurnContinuation"/>). Splitting one turn across two
    /// deployments makes the provider reject the tool output. Safe as instance state because the
    /// client is scoped per user and MentorAgent runs one turn at a time per scope.
    /// </summary>
    private bool _turnUsesStrong;

    /// <summary>Set by <see cref="BeginUserTurn"/>; consumed by the next request.</summary>
    private bool _userTurnStarting;

    /// <summary>
    /// Tells the router that the next request opens a user turn, so it is decided on its own message even when
    /// it ends on a tool result — the answers to approval requests a stopped turn left open, which the
    /// function-invoking client appends after the new message (BUG-116).
    /// </summary>
    internal void BeginUserTurn() => _userTurnStarting = true;

    private bool ContinuesTurn(IReadOnlyList<ChatMessage> list)
    {
        var startsTurn = _userTurnStarting;
        _userTurnStarting = false;
        return !startsTurn && TurnContinuation.IsToolResult(list);
    }

    public RoutingChatClient(
        IChatClient cheap,
        IChatClient strong,
        MentorRoutingStrategy strategy,
        ILogger logger,
        Func<IReadOnlyList<ChatMessage>, CancellationToken, Task<bool>>? custom = null,
        IEmbeddingGenerator<string, Embedding<float>>? embeddings = null,
        IReadOnlyList<string>? exemplars = null,
        float threshold = 0.35f,
        IChatClient? classifier = null,
        Core.MentorEmbeddingCache? exemplarCache = null,
        Core.TurnQueryEmbedding? queryEmbedding = null,
        Core.TurnFaultLog? faults = null)
        : base(cheap)
    {
        _faults = faults;
        _strong = strong;
        _strategy = strategy;
        _logger = logger;
        _custom = custom;
        _embeddings = embeddings;
        _exemplars = exemplars is { Count: > 0 } ? exemplars : DefaultComplexExemplars;
        _threshold = threshold;
        _classifier = classifier ?? cheap;
        _exemplarCache = exemplarCache;
        _queryEmbedding = queryEmbedding;
    }

    /// <summary>Process-wide cache for the exemplars — the same handful of strings for every user.</summary>
    private readonly Core.MentorEmbeddingCache? _exemplarCache;

    /// <summary>The turn's query vector, shared with tool filtering and memory.</summary>
    private readonly Core.TurnQueryEmbedding? _queryEmbedding;

    /// <summary>One stack trace per cause per turn (E1); null when built by hand in a test.</summary>
    private readonly Core.TurnFaultLog? _faults;

    private void Fault(Exception ex, string message)
    {
        if (_faults is not null) _faults.Warn(_logger, ex, message);
        else _logger.LogWarning(ex, message);
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();

        if (ContinuesTurn(list))
            return await ServeAsync(_turnUsesStrong, list, options, cancellationToken);

        // Cascade is the only strategy that must run a model before deciding: serve cheap, then judge.
        if (_strategy == MentorRoutingStrategy.Cascade)
        {
            var cheapResponse = await ServeAsync(strong: false, list, options, cancellationToken);
            var lastUser = LastUser(list);
            var incomplete = await IsIncompleteAsync(lastUser, cheapResponse.Text, cancellationToken);
            if (!incomplete)
            {
                _logger.LogDebug("[MentorAgent] Cascade routing → cheap model served (answer judged complete).");
                return cheapResponse;
            }
            _logger.LogDebug("[MentorAgent] Cascade routing → escalating to strong model (cheap answer judged incomplete).");
            return await ServeAsync(strong: true, list, options, cancellationToken);
        }

        var strong = _turnUsesStrong = await DecideAsync(list, cancellationToken);
        return await ServeAsync(strong, list, options, cancellationToken);
    }

    /// <summary>
    /// Runs the turn on the chosen model and guarantees the answer says <b>which</b> model ran it.
    ///
    /// <para>
    /// This class is installed innermost so that every outer wrapper — the token counter, the
    /// dashboard, observability — can attribute the call to whichever model actually served. The only
    /// channel carrying that outward is <c>ChatResponse.ModelId</c>, and <b>Azure OpenAI leaves it
    /// empty</b>: a live run showed <c>Tokens — model: </c> on every call, with the Agent Framework's
    /// own span agreeing (<c>gen_ai.request.model: gpt-4.1</c> beside a blank
    /// <c>gen_ai.response.model</c>). Downstream, a blank id falls back to the configured <i>cheap</i>
    /// model — so every escalation was billed to the cheap one, understating spend on exactly the
    /// turns that cost the most (BUG-030).
    /// </para>
    ///
    /// <para>
    /// A provider that does report a model is believed: its answer is usually more precise than the
    /// deployment name (<c>gpt-4.1-2025-04-14</c> for a deployment called <c>gpt-4.1</c>). The
    /// deployment name is a fallback, never an override.
    /// </para>
    /// </summary>
    private async Task<ChatResponse> ServeAsync(
        bool strong, IReadOnlyList<ChatMessage> list, ChatOptions? options, CancellationToken ct)
    {
        var client = strong ? _strong : InnerClient;
        var response = await client.GetResponseAsync(list, options, ct);

        if (string.IsNullOrWhiteSpace(response.ModelId))
            response.ModelId = DeploymentIdOf(client);

        return response;
    }

    /// <summary>The deployment this client is pointed at, as the client itself reports it.</summary>
    private static string? DeploymentIdOf(IChatClient client)
        => client.GetService(typeof(ChatClientMetadata)) is ChatClientMetadata m ? m.DefaultModelId : null;

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var list = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();

        bool strong;
        if (ContinuesTurn(list))
        {
            strong = _turnUsesStrong;
        }
        else
        {
            // Streaming can't retroactively escalate, so Cascade pre-decides on the input like Classifier.
            strong = _turnUsesStrong = _strategy == MentorRoutingStrategy.Cascade
                ? await SafeClassifyComplexAsync(LastUser(list), cancellationToken)
                : await DecideAsync(list, cancellationToken);
        }

        var client = strong ? _strong : InnerClient;
        // Resolved once: the deployment cannot change mid-stream, and asking per update would call
        // GetService for every token.
        var deployment = DeploymentIdOf(client);

        await foreach (var update in client.GetStreamingResponseAsync(list, options, cancellationToken))
        {
            // Same reason as ServeAsync: streaming is how every real turn is served, so a fix that
            // only covered the non-streaming path would fix nothing anyone sees (BUG-030).
            if (string.IsNullOrWhiteSpace(update.ModelId))
                update.ModelId = deployment;

            yield return update;
        }
    }

    private async Task<bool> DecideAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct)
    {
        try
        {
            switch (_strategy)
            {
                case MentorRoutingStrategy.Custom:
                    var strong = _custom is not null && await _custom(messages, ct);
                    _logger.LogDebug("[MentorAgent] Custom routing → {Model} model.", strong ? "strong" : "cheap");
                    return strong;

                case MentorRoutingStrategy.Semantic:
                    return await SemanticAsync(LastUser(messages), ct);

                case MentorRoutingStrategy.Classifier:
                    return await ClassifyComplexAsync(LastUser(messages), ct);

                default:
                    return false;
            }
        }
        catch (Exception ex)
        {
            // Any routing failure (embedding/classifier hiccup) must never break the turn — use cheap.
            Fault(ex, "[MentorAgent] Model routing failed — using cheap model.");
            return false;
        }
    }

    // ── Semantic ────────────────────────────────────────────────────────────
    private async Task<bool> SemanticAsync(string lastUser, CancellationToken ct)
    {
        if (_embeddings is null || string.IsNullOrWhiteSpace(lastUser))
            return false;

        await EnsureExemplarEmbeddingsAsync(ct);
        if (_exemplarEmbeddings is null || _exemplarEmbeddings.Length == 0)
            return false;

        var query = _queryEmbedding is not null
            ? await _queryEmbedding.GetAsync(lastUser, _embeddings, ct)
            : (await _embeddings.GenerateAsync([lastUser], cancellationToken: ct))[0].Vector;
        var best = 0f;
        foreach (var e in _exemplarEmbeddings)
        {
            var s = Cosine(query.Span, e.Span);
            if (s > best) best = s;
        }

        var strong = best >= _threshold;
        _logger.LogDebug("[MentorAgent] Semantic routing → {Model} model (score {Score:0.000} vs threshold {Threshold:0.000}).",
            strong ? "strong" : "cheap", best, _threshold);
        return strong;
    }

    private async Task EnsureExemplarEmbeddingsAsync(CancellationToken ct)
    {
        if (_exemplarEmbeddings is not null) return;
        await _embLock.WaitAsync(ct);
        try
        {
            if (_exemplarEmbeddings is not null) return;

            // Same strings for every session: the process cache embeds them once, on the first
            // circuit, and every later one starts warm.
            _exemplarEmbeddings = _exemplarCache is not null
                ? [.. await _exemplarCache.GetOrAddAsync(_exemplars, _embeddings!, ct)]
                : [.. (await _embeddings!.GenerateAsync(_exemplars, cancellationToken: ct)).Select(e => e.Vector)];
        }
        finally { _embLock.Release(); }
    }

    // ── Classifier ──────────────────────────────────────────────────────────
    private async Task<bool> SafeClassifyComplexAsync(string lastUser, CancellationToken ct)
    {
        try { return await ClassifyComplexAsync(lastUser, ct); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MentorAgent] Routing classifier failed — using cheap model.");
            return false;
        }
    }

    private async Task<bool> ClassifyComplexAsync(string lastUser, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(lastUser)) return false;

        var resp = await _classifier.GetResponseAsync(
            $"""
            You are a routing classifier for an AI assistant. Decide whether the USER MESSAGE needs a
            powerful (expensive) model or a small (cheap) one is enough.
            Reply with ONE word only: COMPLEX or SIMPLE.
            COMPLEX = multi-step reasoning, analysis, comparison, math, coding, or long-form writing.
            SIMPLE = greetings, small talk, short factual lookups, or trivial requests.
            USER MESSAGE: {lastUser}
            """, cancellationToken: ct);

        // Parse the classifier's own constrained label (not user text) — same pattern as the eval judge.
        var strong = (resp.Text?.TrimStart() ?? string.Empty).StartsWith("COMPLEX", StringComparison.OrdinalIgnoreCase);
        _logger.LogDebug("[MentorAgent] Classifier routing → {Model} model.", strong ? "strong" : "cheap");
        return strong;
    }

    // ── Cascade completeness judge ────────────────────────────────────────────
    private async Task<bool> IsIncompleteAsync(string input, string? output, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(output))
            return false; // nothing to escalate on

        try
        {
            var resp = await _classifier.GetResponseAsync(
                $"""
                Judge whether the ASSISTANT ANSWER fully and confidently resolves the USER MESSAGE.
                Reply with ONE word only: COMPLETE or INCOMPLETE.
                INCOMPLETE = it hedged, refused, said it was unsure, asked to clarify something it could
                have answered, or only partially addressed the request.
                USER MESSAGE: {input}
                ASSISTANT ANSWER: {output}
                """, cancellationToken: ct);

            return (resp.Text?.TrimStart() ?? string.Empty).StartsWith("INCOMPLETE", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MentorAgent] Cascade completeness judge failed — keeping cheap answer.");
            return false;
        }
    }

    private static string LastUser(IReadOnlyList<ChatMessage> messages)
    {
        for (int i = messages.Count - 1; i >= 0; i--)
            if (messages[i].Role == ChatRole.User)
                return messages[i].Text ?? string.Empty;
        return string.Empty;
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

    protected override void Dispose(bool disposing)
    {
        if (disposing) _embLock.Dispose();
        base.Dispose(disposing);
    }
}
