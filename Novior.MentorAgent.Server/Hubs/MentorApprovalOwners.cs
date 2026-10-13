using System.Collections.Concurrent;

namespace Novior.MentorAgent.Server.Hubs;

/// <summary>
/// Which SignalR connection raised each pending HITL confirmation (BUG-058).
///
/// <para>
/// <c>MentorApprovalStore</c> in the core is keyed by <see cref="Guid"/> alone, which is right for
/// it — the core has no notion of a connection. But <c>POST /mentor/approve</c> then had nothing to
/// check a caller against, so anyone who learned an action id could approve a gated, destructive
/// action its owner had not answered, or deny one they wanted. The action id shares the connection
/// id's weakness: the documented client sends it in a query string, so it reaches access logs,
/// proxy logs and browser history on the way back.
/// </para>
///
/// <para>
/// This map lives in the <b>Server</b> package, not the core, precisely because the thing it knows
/// is transport-shaped. With the connection in hand the endpoint reuses the same ownership check
/// <c>/mentor/session</c> uses, against the principal that opened it.
/// </para>
///
/// <para>
/// An action id that is <b>not</b> here has no owner to check: an SSE client's confirmation never
/// passes through a hub connection, and neither does an in-process Blazor Server one. Those keep
/// today's behaviour rather than failing closed, which would break HITL for every such host.
/// </para>
/// </summary>
public sealed class MentorApprovalOwners
{
    private readonly ConcurrentDictionary<Guid, string> _byAction = new();

    /// <summary>Records that <paramref name="connectionId"/> raised <paramref name="actionId"/>.</summary>
    public void Record(Guid actionId, string connectionId) => _byAction[actionId] = connectionId;

    /// <summary>The connection that raised it, or <see langword="null"/> when nothing recorded one.</summary>
    public string? ConnectionFor(Guid actionId)
        => _byAction.TryGetValue(actionId, out var id) ? id : null;

    /// <summary>Forgets one entry once it has been answered.</summary>
    public void Forget(Guid actionId) => _byAction.TryRemove(actionId, out _);

    /// <summary>
    /// Forgets everything a dropped connection had pending. Without this the map grows for the life
    /// of the process — a confirmation that is never answered would otherwise leak an entry per
    /// abandoned turn.
    /// </summary>
    public void ForgetConnection(string connectionId)
    {
        foreach (var pair in _byAction)
            if (pair.Value == connectionId)
                _byAction.TryRemove(pair.Key, out _);
    }
}
