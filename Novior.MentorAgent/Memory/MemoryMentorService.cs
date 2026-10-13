using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.AI;
using System.Security.Claims;

namespace Novior.MentorAgent.Memory;

/// <summary>
/// Scoped service that orchestrates contextual memory.
///
/// UserId resolved in this order:
/// 1. Signed-in user → its NameIdentifier as it is, else sub, oid or the name with the claim in
///    the key ("sub:...", "name:...") — see MentorUserKey
/// 2. Otherwise → an anonymous key, per session or shared (MentorOptions.AnonymousIdentity)
/// </summary>
public sealed class MentorMemoryService : IDisposable
{
    private readonly IMentorMemoryStore _store;
    private readonly MentorOptions _options;
    private readonly AuthenticationStateProvider? _auth;
    private readonly ILogger<MentorMemoryService> _logger;
    private int _reportedAnonymous;
    private int _reportedAuthFailure;

    private string? _currentQuery;

    // Memory embeddings are computed once (per connection scope) and cached by key+value,
    // so a memory whose value changes is re-embedded. Mirrors ToolFilterChatClient.
    private readonly ConcurrentDictionary<string, ReadOnlyMemory<float>> _memoryEmbeddings = new();
    private readonly SemaphoreSlim _embLock = new(1, 1);

    public MentorMemoryService(
        IMentorMemoryStore store,
        IOptions<MentorOptions> options,
        ILogger<MentorMemoryService> logger,
        AuthenticationStateProvider? auth = null)
    {
        _store = store;
        _options = options.Value;
        _auth = auth;
        _logger = logger;
    }

    /// <summary>Set by the registration; see <see cref="Memory.AnonymousMemoryWatch"/> (BUG-064).</summary>
    internal AnonymousMemoryWatch? AnonymousWatch { get; init; }

    /// <summary>
    /// Set by the registration: the turn's query vector, already computed by whichever of routing,
    /// tool filtering or memory asked for it first.
    /// </summary>
    internal Core.TurnQueryEmbedding? QueryEmbedding { get; init; }

    /// <summary>Set by the registration: this session's own id, for an unauthenticated visitor.</summary>
    internal Core.MentorSessionIdentity? SessionIdentity { get; init; }

    /// <summary>
    /// Set by the registration: whether this scope's turn came from another agent. Such a turn has no
    /// signed-in user by design, so the provider is not asked (BUG-097).
    /// </summary>
    internal Core.MentorTurnOrigin? Origin { get; init; }

    /// <summary>
    /// Sets the current user message so <see cref="GetMemoryContextAsync"/> can rank memories
    /// by relevance when <see cref="MentorOptions.MemoryRelevanceFiltering"/> is enabled.
    /// Called by the orchestrator before each message (scoped instance, same as RAG's PrepareAsync).
    /// </summary>
    public void SetCurrentQuery(string? query) => _currentQuery = query;

    // ── UserId ────────────────────────────────────────────────────────────────

    public async Task<string> GetUserIdAsync(CancellationToken ct = default)
    {
        if (_auth is not null)
        {
            try
            {
                var state = await _auth.GetAuthenticationStateAsync().WaitAsync(ct);
                var user = state.User;

                // NameIdentifier, then sub, oid and the name (BUG-142). A signed-in user with none of them
                // is keyed like an anonymous one below, never into the shared "anonymous" bucket.
                if (Core.MentorUserKey.Of(user) is { } key)
                    return key;
            }
            catch (Exception ex) when (Origin?.FromAnotherAgent == true)
            {
                // A task from another agent, on a host whose provider only works inside a circuit
                // (Blazor Server: "Do not call GetAuthenticationStateAsync outside of the DI scope for
                // a Razor component"). No signed-in user is the expected answer there, not a fault:
                // the warning below told the operator to fix the provider on every A2A task (BUG-097).
                // A provider that CAN be read — a headless host's, from HttpContext.User — is still
                // asked above, so an authenticated peer keeps its identity.
                _logger.LogDebug(
                    "[MentorAgent:A2A] No signed-in user on this task ({Type}); keyed as anonymous.",
                    ex.GetType().Name);
            }
            catch (Exception ex)
            {
                // A provider is registered and could not answer — Blazor Server outside a circuit
                // context, prerendering, a host's own provider throwing. "Nobody" is still the safe
                // answer, but since rc.5 "nobody" is a key per session: a signed-in user who lands
                // here gets a different memory in every circuit, and until BUG-072 nothing said so.
                // Once per session, type and message only — never a stack trace per visitor.
                if (Interlocked.Exchange(ref _reportedAuthFailure, 1) == 0)
                    _logger.LogWarning(
                        "[MentorAgent] The AuthenticationStateProvider threw while resolving the current " +
                        "user ({Type}: {Message}); this session is keyed as anonymous. A signed-in user " +
                        "would get a different memory in every circuit — check the provider can be " +
                        "read from a scoped service.",
                        ex.GetType().Name, ex.Message);
            }
        }

        // Nobody signed in. Two ways to answer, and the default is the safe one (BUG-064): give this
        // session its own key, so one visitor's facts are never recalled for the next and one
        // visitor cannot spend everybody's rate-limit allowance. A single-user host opts back into
        // the shared bucket, which is what makes its memory survive a restart.
        if (_options.AnonymousIdentity == Options.MentorAnonymousIdentity.PerSession
            && SessionIdentity is { } session)
            return $"anonymous:{session.Id}";

        // Shared: reported once per session — see AnonymousMemoryWatch.
        if (_options.UseMemoryContext && Interlocked.Exchange(ref _reportedAnonymous, 1) == 0)
            AnonymousWatch?.SessionSeen();

        return "anonymous";
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    public async Task<string> GetMemoryContextAsync(int count = 10, CancellationToken ct = default)
    {
        var userId = await GetUserIdAsync(ct);

        IReadOnlyList<MemoryEntry> entries;
        var embeddings = _options.EmbeddingGenerator;
        if (_options.MemoryRelevanceFiltering
            && embeddings is not null
            && !string.IsNullOrWhiteSpace(_currentQuery))
        {
            // Inject only the memories semantically relevant to the current message (plus sticky
            // identity/preference facts), instead of the last N, to save tokens.
            var pool = Injectable(await _store.GetRecentAsync(userId, Math.Max(count * 4, count) + 1, ct));
            entries = await RankByRelevanceAsync(pool, _currentQuery!, count, embeddings, ct);
        }
        else
        {
            // Relevance filtering off, or no EmbeddingGenerator configured → last N by recency.
            // One extra is fetched so dropping the bookkeeping entry cannot cost a real memory.
            entries = Injectable(await _store.GetRecentAsync(userId, count + 1, ct)).Take(count).ToList();
        }

        if (entries.Count == 0) return string.Empty;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## USER CONTEXTUAL MEMORY");
        sb.AppendLine("Memories from previous sessions with this user:");

        foreach (var entry in entries)
            sb.AppendLine($"- [{entry.SavedAt:dd/MM/yyyy HH:mm}] {entry.Key}: {entry.Value}");

        return sb.ToString();
    }

    /// <summary>
    /// The key <c>AppContextProvider</c> writes after every successful turn, purely as bookkeeping.
    /// </summary>
    internal const string LastSessionKey = "last_session";

    /// <summary>
    /// Drops MentorAgent's own bookkeeping from the memories offered to the model.
    ///
    /// <para>
    /// <see cref="LastSessionKey"/> is rewritten at the end of every turn, so it is always the most
    /// recent entry and would permanently occupy one of the <c>MemoryContextCount</c> slots — the
    /// entire block at a count of one, which silently turns memory off while it still looks enabled.
    /// It is also nothing the user said: showing the model a raw round-trip timestamp next to the
    /// current date it already has in context buys nothing. It stays in the store (a host's own
    /// implementation may want a last-seen), it is simply not injected.
    /// </para>
    /// </summary>
    private static List<MemoryEntry> Injectable(IEnumerable<MemoryEntry> entries) =>
        entries.Where(e => !string.Equals(e.Key, LastSessionKey, StringComparison.Ordinal)).ToList();

    // ── Semantic relevance ranking (embedding cosine similarity) ───────────────

    /// <summary>
    /// Keeps sticky identity/preference facts, then fills the remaining slots (up to
    /// <paramref name="count"/>) with the memories whose embedding is most similar to the
    /// current message. Fully semantic (multilingual), no keyword lists — consistent with
    /// semantic tool filtering. Falls back to recency if embedding fails.
    /// </summary>
    private async Task<IReadOnlyList<MemoryEntry>> RankByRelevanceAsync(
        IReadOnlyList<MemoryEntry> pool, string query, int count,
        IEmbeddingGenerator<string, Embedding<float>> embeddings, CancellationToken ct)
    {
        var sticky     = pool.Where(e => IsSticky(e.Key)).ToList();
        var candidates = pool.Where(e => !IsSticky(e.Key)).ToList();

        var remaining = count - sticky.Count;
        if (candidates.Count == 0 || remaining <= 0)
            return sticky.Take(count).OrderBy(e => e.SavedAt).ToList();

        try
        {
            await EnsureMemoryEmbeddingsAsync(candidates, embeddings, ct);
            var queryVec = QueryEmbedding is not null
                ? await QueryEmbedding.GetAsync(query, embeddings, ct)
                : (await embeddings.GenerateAsync([query], cancellationToken: ct))[0].Vector;

            var top = candidates
                .Select(e => (Entry: e,
                              Score: _memoryEmbeddings.TryGetValue(CacheKey(e), out var v)
                                         ? Cosine(queryVec.Span, v.Span) : 0f))
                .OrderByDescending(x => x.Score)
                .Take(remaining)
                .Select(x => x.Entry)
                .ToList();

            _logger.LogDebug(
                "[MentorAgent:Memory] Semantic relevance: {Kept}/{Pool} memories injected " +
                "({Sticky} sticky + {Matched} by embedding).",
                sticky.Count + top.Count, pool.Count, sticky.Count, top.Count);

            // Sticky facts + top matches, in chronological order for natural reading.
            return sticky.Concat(top).OrderBy(e => e.SavedAt).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[MentorAgent] Memory relevance embedding failed — falling back to recency.");
            return pool.OrderByDescending(e => e.SavedAt).Take(count)
                       .OrderBy(e => e.SavedAt).ToList();
        }
    }

    private static bool IsSticky(string key) =>
        key.StartsWith("user_", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("pref",  StringComparison.OrdinalIgnoreCase);

    private static string CacheKey(MemoryEntry e) => $"{e.Key}\0{e.Value}";

    private async Task EnsureMemoryEmbeddingsAsync(
        List<MemoryEntry> candidates,
        IEmbeddingGenerator<string, Embedding<float>> embeddings,
        CancellationToken ct)
    {
        var missing = candidates.Where(e => !_memoryEmbeddings.ContainsKey(CacheKey(e))).ToList();
        if (missing.Count == 0) return;

        await _embLock.WaitAsync(ct);
        try
        {
            missing = candidates.Where(e => !_memoryEmbeddings.ContainsKey(CacheKey(e))).ToList();
            if (missing.Count == 0) return;

            var texts = missing.Select(e => $"{e.Key}: {e.Value}").ToList();
            var vecs  = await embeddings.GenerateAsync(texts, cancellationToken: ct);
            for (int i = 0; i < missing.Count; i++)
                _memoryEmbeddings[CacheKey(missing[i])] = vecs[i].Vector;
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

    // ── Write ─────────────────────────────────────────────────────────────────

    public async Task RememberAsync(string key, string value, CancellationToken ct = default)
    {
        var userId = await GetUserIdAsync(ct);
        await _store.SaveAsync(userId, key, value, ct);
        _logger.LogDebug("[MentorAgent] Memory saved for '{User}': {Key} = {Value}",
            userId, key, value);
    }

    public async Task RememberObjectAsync(string key, object value, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(value);
        await RememberAsync(key, json, ct);
    }

    public async Task ForgetAllAsync(CancellationToken ct = default)
    {
        var userId = await GetUserIdAsync(ct);
        await _store.ClearAsync(userId, ct);
        _logger.LogDebug("[MentorAgent] Memory cleared for '{User}'.", userId);
    }

    public async Task<string?> RecallAsync(string key, CancellationToken ct = default)
    {
        var userId = await GetUserIdAsync(ct);
        return await _store.GetAsync(userId, key, ct);
    }

    public void Dispose() => _embLock.Dispose();
}