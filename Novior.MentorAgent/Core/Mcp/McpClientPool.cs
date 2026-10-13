using Novior.MentorAgent.Models;
using System.Collections.Concurrent;
using Novior.MentorAgent.Options;

namespace Novior.MentorAgent.Core.Mcp;

/// <summary>
/// Decides what "shared" means for an MCP server, and how one is identified.
/// </summary>
internal static class McpServerIdentity
{
    /// <summary>
    /// A server reached over a <b>URL</b> is shared by default: it is a remote endpoint that every
    /// session was already talking to separately, so pooling the connection changes nothing about
    /// what it sees. A server started from a <b>command</b> is a local child process that can hold
    /// state for the person who opened it — the sample's <c>server-memory</c> is literally a
    /// knowledge graph — so it stays per session unless the host says otherwise.
    /// </summary>
    internal static bool IsShared(MentorMcpServer server)
        => server.Shared ?? !string.IsNullOrWhiteSpace(server.ServerUrl);

    /// <summary>
    /// The transport signature, not the name: an entry whose command or URL was edited must never be
    /// served the session built from the old one.
    /// </summary>
    internal static string KeyOf(MentorMcpServer server)
        => string.IsNullOrWhiteSpace(server.ServerUrl)
            ? $"stdio|{server.Name}|{server.Command}|{string.Join(" >< ", server.Arguments ?? [])}|" +
              string.Join(",", (server.EnvironmentVariables ?? new Dictionary<string, string>())
                  .OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"))
            : $"url|{server.Name}|{server.ServerUrl}";
}

/// <summary>
/// One connection per shared MCP server for the whole process, instead of one per visitor.
///
/// <para>
/// Measured live on rc.4: two browser tabs on the Blazor Server sample meant <b>eight</b> node
/// processes and 580 MB, because every circuit started its own copy of every configured stdio
/// server — and paid its start-up time (3 to 50 seconds) before it could answer the first message.
/// </para>
///
/// <para>
/// Three details carry the weight here. Concurrent first callers must produce <em>one</em> connect,
/// not one each — hence <see cref="Lazy{T}"/> per entry. A caller that gives up (a circuit that
/// drops during a slow start) must not cancel the connect the others are waiting on — hence the
/// connect runs on <see cref="CancellationToken.None"/> and each caller awaits with its own token.
/// And a session that dies later must not be handed to the next visitor — hence the ping before a
/// cached session is reused.
/// </para>
/// </summary>
internal sealed class McpClientPool(
    IMcpSessionFactory factory,
    ILogger<McpClientPool> logger,
    TimeProvider? time = null) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>How long a failed server is left alone before another session pays for a retry.</summary>
    internal TimeSpan FailureBackoff { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Sessions opened by this pool — for tests and diagnostics.</summary>
    internal int Connects;

    internal async Task<IMcpSession> GetOrConnectAsync(MentorMcpServer server, CancellationToken ct)
    {
        var key = McpServerIdentity.KeyOf(server);

        while (true)
        {
            var entry = _entries.GetOrAdd(key, _ => new Entry(
                new Lazy<Task<IMcpSession>>(() => ConnectAsync(server), LazyThreadSafetyMode.ExecutionAndPublication),
                _time.GetUtcNow()));

            // A failure that has served its time is replaced *before* anyone waits on it again,
            // otherwise the first caller after the window would only clear the entry and still be
            // told no — the retry would land on the visitor after them.
            if (entry.Session.IsValueCreated && entry.Session.Value.IsFaulted
                && _time.GetUtcNow() - entry.At > FailureBackoff)
            {
                _entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
                continue;
            }

            IMcpSession session;
            try
            {
                // The caller's own token: if this circuit drops during a slow start, the others
                // waiting on the same connection are unaffected.
                session = await entry.Session.Value.WaitAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A failed connect is remembered for the backoff window: a server that is simply
                // down was costing every visitor a full connect timeout.
                logger.LogDebug("[MentorAgent:MCP] '{Name}' is in its failure window.", server.Name);
                throw new McpSessionUnavailableException(server.Name, ex);
            }

            // Alive? A shared session outlives the visitor who opened it, so the answer can change
            // between one visitor and the next.
            if (await session.IsAliveAsync(ct)) return session;

            logger.LogWarning(
                "[MentorAgent:MCP] The shared session for '{Name}' is no longer answering — reconnecting.",
                server.Name);

            if (_entries.TryRemove(new KeyValuePair<string, Entry>(key, entry)))
                await SafeDisposeAsync(session);
        }
    }

    /// <summary>
    /// Drops (and closes) sessions for servers that are no longer configured — an entry whose
    /// command or URL was edited leaves an orphan behind, and an orphaned stdio session is an
    /// orphaned child process.
    /// </summary>
    internal async Task PruneAsync(IEnumerable<MentorMcpServer> configured)
    {
        var live = configured.Select(McpServerIdentity.KeyOf).ToHashSet(StringComparer.Ordinal);

        foreach (var (key, entry) in _entries)
        {
            if (live.Contains(key)) continue;
            if (!_entries.TryRemove(new KeyValuePair<string, Entry>(key, entry))) continue;

            if (entry.Session.IsValueCreated && entry.Session.Value.IsCompletedSuccessfully)
                await SafeDisposeAsync(entry.Session.Value.Result);
        }
    }

    private async Task<IMcpSession> ConnectAsync(MentorMcpServer server)
    {
        Interlocked.Increment(ref Connects);
        var session = await factory.ConnectAsync(server, CancellationToken.None);
        logger.LogInformation(
            "[MentorAgent:MCP] Shared session opened for '{Name}' — one per process, not one per session.",
            server.Name);
        return session;
    }

    private static async Task SafeDisposeAsync(IMcpSession session)
    {
        try { await session.DisposeAsync(); } catch { /* best-effort */ }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (_, entry) in _entries)
        {
            if (entry.Session.IsValueCreated && entry.Session.Value.IsCompletedSuccessfully)
                await SafeDisposeAsync(entry.Session.Value.Result);
        }
        _entries.Clear();
    }

    private sealed record Entry(Lazy<Task<IMcpSession>> Session, DateTimeOffset At);
}

/// <summary>Raised when a pooled server cannot be reached; carries the original failure.</summary>
internal sealed class McpSessionUnavailableException(string serverName, Exception inner)
    : Exception($"MCP server '{serverName}' is not available.", inner);
