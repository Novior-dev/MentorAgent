using System.Collections.Concurrent;

namespace Novior.MentorAgent.Core;

/// <summary>
/// Singleton store for pending HITL confirmation state.
///
/// Must be Singleton — not Scoped — because SignalR creates a new DI scope per hub
/// method invocation. The TCS is registered in the SendMessage scope and must be
/// resolvable from the RespondToApproval scope (a different scope, same connection).
/// A Scoped store would give each hub method its own empty dictionaries.
/// </summary>
internal sealed class MentorApprovalStore
{
    private readonly ConcurrentDictionary<Guid, ConfirmationRequest>        _pending       = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<bool>> _confirmations = new();

    /// <summary>
    /// Records a confirmation as pending <b>and creates its waiter</b>, before anything is told the
    /// request exists.
    ///
    /// <para>
    /// Creating the waiter here rather than in <see cref="RegisterAndWaitAsync"/> closes a race that
    /// hangs the turn permanently. The caller raises <c>OnConfirmationRequired</c> and only then
    /// awaits, so a subscriber that answers <i>synchronously</i> — an auto-approval policy, a
    /// pre-approved client, a fast enough click — resolved a confirmation that had no waiter yet.
    /// The answer went nowhere, the subsequent await registered a fresh waiter nobody would ever
    /// resolve, and the turn sat there until the request timed out: no error, no log, nothing.
    /// </para>
    /// </summary>
    internal void RegisterPending(ConfirmationRequest request)
    {
        _pending[request.ActionId] = request;
        _confirmations.TryAdd(request.ActionId, NewWaiter());
    }

    internal async Task<bool> RegisterAndWaitAsync(Guid actionId, CancellationToken ct)
    {
        // GetOrAdd, not assignment: RegisterPending has normally created the waiter already, and
        // replacing it would discard an answer that arrived in between.
        var tcs = _confirmations.GetOrAdd(actionId, _ => NewWaiter());

        using var registration = ct.Register(() => tcs.TrySetCanceled(ct));
        try
        {
            return await tcs.Task;
        }
        finally
        {
            // The request goes with its waiter. A wait ended by Stop, or by the end of the turn's scope
            // (a closed hub connection, a closed circuit), left the request pending: a late answer
            // still found it and was reported as an approval nobody acted on (BUG-129).
            _confirmations.TryRemove(actionId, out _);
            _pending.TryRemove(actionId, out _);
        }
    }

    /// <summary>
    /// Resolves the pending confirmation. Returns the request only if the answer reached a turn that
    /// was still waiting for it; <see langword="null"/> when it was answered already, or its wait was
    /// cancelled — by Stop, or by the end of the scope the turn ran in (BUG-128, BUG-129).
    ///
    /// <para>
    /// The waiter is left in place for <see cref="RegisterAndWaitAsync"/> to clean up: removing it
    /// here would lose an answer given before anyone started waiting.
    /// </para>
    /// </summary>
    internal ConfirmationRequest? TryResolve(Guid actionId, bool approved)
    {
        // Whoever takes the request gives the one answer that counts.
        if (!_pending.TryRemove(actionId, out var req))
            return null;

        // A wait cancelled a moment ago still has its request here until its finally runs; the
        // waiter, already cancelled, refuses the answer, and so does this method.
        return _confirmations.TryGetValue(actionId, out var tcs) && tcs.TrySetResult(approved)
            ? req
            : null;
    }

    /// <summary>Whether a confirmation is still waiting for its answer.</summary>
    internal bool IsPending(Guid actionId) => _pending.ContainsKey(actionId);

    private static TaskCompletionSource<bool> NewWaiter()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
