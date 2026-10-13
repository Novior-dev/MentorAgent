using System.Text.Json;


namespace Novior.MentorAgent.Core;

/// <summary>
/// Implementation of <see cref="IMentorSessionManager"/>.
///
/// Manages the lifecycle of <see cref="AgentSession"/> (Agent Framework).
/// AgentSession is the container for the conversation history ï¿½ we do not manage it
/// manually; Agent Framework handles that internally.
///
/// API source (sections 3.2, 3.5 of the Word document):
/// <code>
/// AgentSession session = await agent.CreateSessionAsync();
/// await agent.RunAsync("message", session);
///
/// JsonElement saved   = agent.SerializeSession(session);
/// AgentSession resume = await agent.DeserializeSessionAsync(saved);
/// </code>
///
/// Lifetime:
/// - Blazor Server: Scoped (one session per circuit)
/// - Blazor WASM:   Singleton (one session per tab)
/// </summary>
internal sealed class MentorSessionManager : IMentorSessionManager
{
    private AgentSession? _session;

    // SemaphoreSlim(1,1) is the single async-compatible mutex that guards ALL
    // reads and writes of _session across GetOrCreate, Reset, Serialize and Restore.
    // A plain lock{} cannot be used for async methods; Volatile.Read/Write alone
    // is not sufficient because multi-step read-then-create must be atomic.
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public event Action? OnSessionChanged;

    // -- API -------------------------------------------------------------------

    public async Task<AgentSession> GetOrCreateSessionAsync(
        AIAgent agent,
        CancellationToken ct = default)
    {
        // Cheap unsynchronised fast path: once initialised the field is never
        // written again until ResetAsync, so this is safe to read lock-free.
        if (_session is not null) return _session;

        await _semaphore.WaitAsync(ct);
        try
        {
            // Double-check: another caller may have initialised while we waited.
            if (_session is not null) return _session;

            _session = await agent.CreateSessionAsync(ct);
            return _session;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task ResetAsync(AIAgent agent, CancellationToken ct = default)
    {
        var newSession = await agent.CreateSessionAsync(ct);

        await _semaphore.WaitAsync(ct);
        try
        {
            _session = newSession;
            _unconfirmedRestore = null;
        }
        finally
        {
            _semaphore.Release();
        }

        OnSessionChanged?.Invoke();
    }

    // ── Snapshot envelope ──────────────────────────────────────────────────────
    //
    // A snapshot is stamped rather than handed out raw. The Agent Framework's
    // DeserializeSessionAsync does NOT reject JSON that is not one of its sessions — given
    // {"not":"a session"} it returns a perfectly valid *empty* session — so without a marker of
    // our own there is no way to tell a real snapshot from anything else the caller happened to
    // have. Restoring the empty result then replaced a live conversation and raised
    // OnSessionChanged: the host's own subscribers (a widget clearing its transcript) reacted, the
    // user's conversation disappeared, and nothing was logged. Snapshots come back from
    // localStorage, a cookie or a database, all of which routinely hold something written by an
    // older version. The marker is not a namespace: saved snapshots carry it, so it stays when the
    // namespace changes (ContractNameTests).
    private const string MarkerProperty = "mentorAgentSession";
    private const string PayloadProperty = "session";
    private const string HistoryProperty = "history";
    private const string HistoryKeysProperty = "historyKeys";
    private const int FormatVersion = 1;

    private const string ServiceHistory = "service";
    private const string LocalHistory = "local";

    /// <summary>
    /// How this host keeps its history — on the service (<see langword="true"/>), locally
    /// (<see langword="false"/>), or unknown (<see langword="null"/>: a host-built agent, Path B) — and,
    /// when local, the state keys its history provider stores the transcript under. Set by the
    /// orchestrator once the coordinator is built (BUG-110).
    /// </summary>
    private bool? _serviceManaged;
    private IReadOnlyList<string>? _historyKeys;

    /// <summary>
    /// The session a restore installed with a service conversation id, until a turn succeeds on it: until
    /// then nobody knows whether the service still has that conversation. The INSTANCE, not a flag — a
    /// restore can arrive over HTTP while a turn is still running on the previous session, and that turn
    /// must neither confirm nor discard a session it never used.
    /// </summary>
    private AgentSession? _unconfirmedRestore;

    /// <summary>Tells the manager how the host keeps its history, so a snapshot it cannot use is refused.</summary>
    internal void ConfigureHistory(bool? serviceManaged, IReadOnlyList<string>? historyKeys)
    {
        _serviceManaged = serviceManaged;
        _historyKeys = serviceManaged == false ? historyKeys : null;
    }

    /// <summary>Whether <paramref name="session"/> was restored with a service conversation id no turn has confirmed yet.</summary>
    internal bool IsUnconfirmedRestore(AgentSession session)
        => ReferenceEquals(Volatile.Read(ref _unconfirmedRestore), session);

    /// <summary>A turn succeeded on <paramref name="session"/>: if it was the restored one, the service knows it.</summary>
    internal void ConfirmRestored(AgentSession session)
        => Interlocked.CompareExchange(ref _unconfirmedRestore, null, session);

    /// <summary>
    /// Replaces the conversation with a new one, but only if <paramref name="session"/> is still both the
    /// live session and the unconfirmed restore — a restore or a reset that happened since wins.
    /// </summary>
    internal async Task<bool> ReplaceUnconfirmedRestoreAsync(AIAgent agent, AgentSession session, CancellationToken ct = default)
    {
        var replacement = await agent.CreateSessionAsync(ct);

        await _semaphore.WaitAsync(ct);
        try
        {
            if (!ReferenceEquals(_session, session) || !ReferenceEquals(_unconfirmedRestore, session))
                return false;

            _session = replacement;
            _unconfirmedRestore = null;
        }
        finally { _semaphore.Release(); }

        OnSessionChanged?.Invoke();
        return true;
    }

    public async Task<JsonElement?> SerializeCurrentSessionAsync(AIAgent agent)
    {
        // Guard under the same semaphore used by GetOrCreate and Reset so that
        // a concurrent Reset cannot replace _session while we are serialising it.
        AgentSession? sessionToSerialize;
        await _semaphore.WaitAsync();
        try { sessionToSerialize = _session; }
        finally { _semaphore.Release(); }

        if (sessionToSerialize is null) return null;

        var payload = await agent.SerializeSessionAsync(sessionToSerialize);

        var envelope = new Dictionary<string, object>
        {
            [MarkerProperty] = FormatVersion,
            [PayloadProperty] = payload,
        };

        // How the history is kept travels with it, so a host that keeps it another way refuses the
        // snapshot instead of failing every turn after it, or restoring an empty history (BUG-110).
        // Readers that predate the stamp ignore it.
        if (_serviceManaged is { } serviceManaged)
        {
            envelope[HistoryProperty] = serviceManaged ? ServiceHistory : LocalHistory;
            if (!serviceManaged && _historyKeys is { Count: > 0 } keys)
                envelope[HistoryKeysProperty] = keys;
        }

        return JsonSerializer.SerializeToElement(envelope);
    }

    public async Task RestoreSessionAsync(
        AIAgent agent,
        JsonElement serialized,
        CancellationToken ct = default)
    {
        // Fail closed, and fail before touching _session: a snapshot that cannot be read must
        // leave the conversation the user is having exactly where it was.
        if (serialized.ValueKind != JsonValueKind.Object
            || !serialized.TryGetProperty(MarkerProperty, out var marker)
            || !serialized.TryGetProperty(PayloadProperty, out var payload))
        {
            throw new ArgumentException(
                $"This is not a MentorAgent session snapshot (no '{MarkerProperty}' marker). " +
                "Pass back exactly what SerializeCurrentSessionAsync returned. The current " +
                "conversation has been left untouched.",
                nameof(serialized));
        }

        // TryGetInt32, not GetInt32: 1.5, 1e20 or 2147483648 threw FormatException — a 500 over HTTP
        // where the contract says 400 (BUG-110).
        if (marker.ValueKind != JsonValueKind.Number || !marker.TryGetInt32(out var version) || version != FormatVersion)
        {
            throw new ArgumentException(
                $"Session snapshot format {marker} cannot be read by this version " +
                $"(expected {FormatVersion}). The current conversation has been left untouched.",
                nameof(serialized));
        }

        AgentSession restored;
        try
        {
            restored = await agent.DeserializeSessionAsync(payload, cancellationToken: ct);
        }
        catch (Exception ex) when (ex is not ArgumentException and not OperationCanceledException)
        {
            throw new ArgumentException(
                "The snapshot's session could not be read. The current conversation has been left untouched.",
                nameof(serialized), ex);
        }

        CheckHistoryCompatibility(serialized, restored);

        // Guard under the same semaphore so that GetOrCreate / Reset cannot
        // run concurrently while we are replacing _session here.
        await _semaphore.WaitAsync(ct);
        try
        {
            _session = restored;
            _unconfirmedRestore = ConversationIdOf(restored) is not null ? restored : null;
        }
        finally { _semaphore.Release(); }

        OnSessionChanged?.Invoke();
    }

    /// <summary>
    /// Refuses a snapshot this host cannot use (BUG-110). A service-managed snapshot is a pointer to a
    /// conversation the service keeps: on a host with a local history every turn then failed ("Service
    /// did not return a valid conversation id…"). A local snapshot on a service-managed host restored an
    /// empty history, silently; so did one whose history provider keeps the transcript under another key.
    /// A snapshot saved before the stamp existed is judged by whether it carries a conversation id.
    /// </summary>
    private void CheckHistoryCompatibility(JsonElement serialized, AgentSession restored)
    {
        if (_serviceManaged is not { } hostService) return;   // a host-built agent: nothing to compare

        var conversationId = ConversationIdOf(restored);
        var stamp = serialized.TryGetProperty(HistoryProperty, out var h) && h.ValueKind == JsonValueKind.String
            ? h.GetString()
            : null;

        if (stamp == LocalHistory && conversationId is not null)
            throw new ArgumentException(
                "The snapshot says its history is local but carries a service conversation id. The current " +
                "conversation has been left untouched.", nameof(serialized));

        var snapshotService = stamp is null ? conversationId is not null : stamp == ServiceHistory;

        if (snapshotService && !hostService)
            throw new ArgumentException(
                "The snapshot was saved on a host whose history the service keeps (a conversation id, e.g. the " +
                "Responses API), and this host keeps its history locally. The current conversation has been " +
                "left untouched.", nameof(serialized));

        if (!snapshotService && hostService)
            throw new ArgumentException(
                "The snapshot was saved on a host that keeps its history locally (a history provider), and this " +
                "host's history is kept by the service (UseServiceManagedHistory). The current conversation has " +
                "been left untouched.", nameof(serialized));

        if (!hostService
            && _historyKeys is { Count: > 0 } hostKeys
            && serialized.TryGetProperty(HistoryKeysProperty, out var k) && k.ValueKind == JsonValueKind.Array)
        {
            var snapshotKeys = k.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .ToList();

            if (!snapshotKeys.SequenceEqual(hostKeys, StringComparer.Ordinal))
                throw new ArgumentException(
                    $"The snapshot's history was kept by a provider that stores it as [{string.Join(", ", snapshotKeys)}]; " +
                    $"this host's provider reads [{string.Join(", ", hostKeys)}], so the history would restore empty. " +
                    "The current conversation has been left untouched.", nameof(serialized));
        }
    }

    private static string? ConversationIdOf(AgentSession session)
        => session is ChatClientAgentSession { ConversationId: { Length: > 0 } id } ? id : null;
}
