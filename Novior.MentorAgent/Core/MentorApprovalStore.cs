using System.Collections.Concurrent;
using System.Security.Claims;

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
    private readonly ConcurrentDictionary<Guid, (ConfirmationRequest Request, long AskedAt)> _pending       = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<MentorApprovalAnswer>>  _confirmations = new();
    private readonly TimeProvider _time;

    public MentorApprovalStore(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

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
        _pending[request.ActionId] = (request, _time.GetTimestamp());
        _confirmations.TryAdd(request.ActionId, NewWaiter());
    }

    internal async Task<bool> RegisterAndWaitAsync(Guid actionId, CancellationToken ct)
        => (await RegisterAndWaitForAnswerAsync(actionId, TimeSpan.Zero, ct)).Approved;

    /// <summary>
    /// As <see cref="RegisterAndWaitAsync"/>, with who answered and when: what the gate's decision records
    /// (story 180).
    /// </summary>
    /// <param name="actionId">The confirmation waited for.</param>
    /// <param name="timeout">
    /// <c>MentorOptions.ConfirmationTimeout</c>, counted from <see cref="RegisterPending"/>, when the banner was shown:
    /// nobody has answered by then, and the answer is a rejection that <see cref="MentorApprovalAnswer.TimedOut"/>
    /// marks. Zero or negative: no limit.
    /// </param>
    /// <param name="ct">The turn's token: Stop, or the end of its scope.</param>
    internal async Task<MentorApprovalAnswer> RegisterAndWaitForAnswerAsync(Guid actionId, TimeSpan timeout, CancellationToken ct)
    {
        // GetOrAdd, not assignment: RegisterPending has normally created the waiter already, and
        // replacing it would discard an answer that arrived in between.
        var tcs = _confirmations.GetOrAdd(actionId, _ => NewWaiter());

        using var registration = ct.Register(() => tcs.TrySetCanceled(ct));
        using var deadline = timeout > TimeSpan.Zero ? StartDeadline(actionId, timeout) : null;
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
    /// <param name="actionId">The confirmation answered.</param>
    /// <param name="approved">The answer.</param>
    /// <param name="by">
    /// Who answered, when the transport knows it (<c>POST /mentor/approve</c>); null in a circuit and in the
    /// MAUI app, where the one who answers is the session's user.
    /// </param>
    internal ConfirmationRequest? TryResolve(Guid actionId, bool approved, ClaimsPrincipal? by = null)
    {
        // Whoever takes the request gives the one answer that counts.
        if (!_pending.TryRemove(actionId, out var pending))
            return null;

        // A wait cancelled a moment ago still has its request here until its finally runs; the
        // waiter, already cancelled, refuses the answer, and so does this method.
        return _confirmations.TryGetValue(actionId, out var tcs)
               && tcs.TrySetResult(new MentorApprovalAnswer(approved, by, _time.GetUtcNow()))
            ? pending.Request
            : null;
    }

    /// <summary>Whether a confirmation is still waiting for its answer.</summary>
    internal bool IsPending(Guid actionId) => _pending.ContainsKey(actionId);

    /// <summary>
    /// Ends the wait with a rejection once <paramref name="timeout"/> has passed since the banner was shown (story
    /// 176); at once when it has passed already. Null when nothing is pending: the answer came first.
    /// </summary>
    private ITimer? StartDeadline(Guid actionId, TimeSpan timeout)
    {
        if (!_pending.TryGetValue(actionId, out var pending)) return null;

        var left = timeout - _time.GetElapsedTime(pending.AskedAt);
        if (left <= TimeSpan.Zero)
        {
            Expire(actionId);
            return null;
        }
        return _time.CreateTimer(_ => Expire(actionId), null, left, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// The deadline's answer. Like <see cref="TryResolve"/> it takes the request first, so of the deadline and the
    /// user's answer only the first counts: an answer after the deadline finds nothing, as one after Stop does.
    /// </summary>
    private void Expire(Guid actionId)
    {
        if (_pending.TryRemove(actionId, out _) && _confirmations.TryGetValue(actionId, out var tcs))
            tcs.TrySetResult(new MentorApprovalAnswer(false, null, _time.GetUtcNow(), TimedOut: true));
    }

    private static TaskCompletionSource<MentorApprovalAnswer> NewWaiter()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// An answer to a confirmation: what, who and when (UTC). <paramref name="TimedOut"/>: nobody answered within
/// <c>ConfirmationTimeout</c>, and the confirmation counts as rejected.
/// </summary>
internal readonly record struct MentorApprovalAnswer(bool Approved, ClaimsPrincipal? By, DateTimeOffset At, bool TimedOut = false);
