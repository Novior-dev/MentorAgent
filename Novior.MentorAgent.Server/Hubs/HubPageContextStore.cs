using System.Collections.Concurrent;

namespace Novior.MentorAgent.Server.Hubs;

/// <summary>
/// Singleton store that persists the last <see cref="PageContextSnapshot"/> per SignalR
/// connection ID across DI scope boundaries.
///
/// SignalR creates a new DI scope for every hub method invocation, so the Scoped
/// <see cref="IMentorPageContext"/> resolved in <c>UpdatePageContext</c> is a different
/// instance from the one resolved in <c>SendMessage</c>. This store bridges the gap:
/// <c>UpdatePageContext</c> writes here; <c>SendMessage</c> reads and applies before
/// the orchestrator runs.
/// </summary>
public sealed class HubPageContextStore
{
    private readonly ConcurrentDictionary<string, PageContextSnapshot> _snapshots = new();

    public void Store(string connectionId, PageContextSnapshot snapshot)
        => _snapshots[connectionId] = snapshot;

    public PageContextSnapshot? Get(string connectionId)
        => _snapshots.TryGetValue(connectionId, out var s) ? s : null;

    public void Remove(string connectionId)
        => _snapshots.TryRemove(connectionId, out _);
}
