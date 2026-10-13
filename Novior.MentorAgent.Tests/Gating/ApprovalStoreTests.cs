using FluentAssertions;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Core;
using Xunit;

namespace Novior.MentorAgent.Tests.Gating;

/// <summary>
/// The approval store — the rendezvous between a turn parked inside a tool call and whatever
/// answers it.
///
/// <para>
/// Everything here is a race by construction. The turn registers a
/// <see cref="TaskCompletionSource{T}"/> and blocks; the answer arrives from a different DI scope
/// (over HTTP on the server, from a click handler in Blazor), possibly twice, possibly after the
/// user has already pressed Stop. Every one of those combinations either releases the turn exactly
/// once or leaves it parked forever — and "parked forever" presents to the user as a widget that
/// spins until it times out.
/// </para>
/// </summary>
public sealed class ApprovalStoreTests
{
    private static ConfirmationRequest Request(Guid id, string tool = "issue_refund")
        => new() { ActionId = id, ToolName = tool, Message = $"{tool} — order 1001" };

    // ── The happy paths ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Approving_releases_the_waiting_turn_with_true()
    {
        var store = new MentorApprovalStore();
        var id = Guid.NewGuid();
        store.RegisterPending(Request(id));

        var waiting = store.RegisterAndWaitAsync(id, CancellationToken.None);
        store.TryResolve(id, approved: true);

        (await waiting.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
    }

    [Fact]
    public async Task Denying_releases_the_waiting_turn_with_false()
    {
        // A denial has to unblock as reliably as an approval, or pressing Cancel hangs the widget.
        var store = new MentorApprovalStore();
        var id = Guid.NewGuid();
        store.RegisterPending(Request(id));

        var waiting = store.RegisterAndWaitAsync(id, CancellationToken.None);
        store.TryResolve(id, approved: false);

        (await waiting.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeFalse();
    }

    [Fact]
    public void Resolving_returns_the_request_so_the_caller_can_report_what_was_answered()
    {
        var store = new MentorApprovalStore();
        var id = Guid.NewGuid();
        store.RegisterPending(Request(id, "cancel_order"));
        store.RegisterAndWaitAsync(id, CancellationToken.None);

        store.TryResolve(id, approved: true)!.ToolName.Should().Be("cancel_order");
    }

    // ── The synchronous-answer race (BUG-012) ───────────────────────────────────

    [Fact]
    public async Task An_answer_given_before_anyone_waits_is_not_lost()
    {
        // The caller raises OnConfirmationRequired and only then awaits. A subscriber that answers
        // synchronously — an auto-approval policy, a pre-approved client — therefore resolves the
        // request before any waiter exists. Losing that answer parked the turn permanently: no
        // error, no log, just a widget spinning until the request timed out.
        var store = new MentorApprovalStore();
        var id = Guid.NewGuid();

        store.RegisterPending(Request(id));
        store.TryResolve(id, approved: true);          // answered first
        var waiting = store.RegisterAndWaitAsync(id, CancellationToken.None);  // awaited second

        (await waiting.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
    }

    [Fact]
    public async Task A_denial_given_before_anyone_waits_is_not_lost_either()
    {
        var store = new MentorApprovalStore();
        var id = Guid.NewGuid();

        store.RegisterPending(Request(id));
        store.TryResolve(id, approved: false);
        var waiting = store.RegisterAndWaitAsync(id, CancellationToken.None);

        (await waiting.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeFalse();
    }

    [Fact]
    public async Task An_answer_racing_the_wait_lands_exactly_once_either_way()
    {
        // Whichever order the two actually interleave, the turn must be released once. Run enough
        // times that a fix relying on lucky timing would show up.
        for (var i = 0; i < 200; i++)
        {
            var store = new MentorApprovalStore();
            var id = Guid.NewGuid();
            store.RegisterPending(Request(id));

            // The answer is kicked off without awaiting, so it may land before or after the wait
            // begins — which is exactly the interleaving under test.
            var answer = Task.Run(() => store.TryResolve(id, approved: true));
            var result = await store.RegisterAndWaitAsync(id, CancellationToken.None)
                                    .WaitAsync(TimeSpan.FromSeconds(5));
            await answer;

            result.Should().BeTrue("iteration {0}", i);
        }
    }

    // ── Double answers ──────────────────────────────────────────────────────────

    [Fact]
    public void A_second_answer_finds_nothing_left_to_resolve()
    {
        // The user double-taps Confirm, or an approval arrives just after the turn was cancelled.
        // The second call has to be a no-op that reports itself, not an exception.
        var store = new MentorApprovalStore();
        var id = Guid.NewGuid();
        store.RegisterPending(Request(id));
        store.RegisterAndWaitAsync(id, CancellationToken.None);

        store.TryResolve(id, approved: true).Should().NotBeNull();
        store.TryResolve(id, approved: true).Should().BeNull("the request was already consumed");
    }

    [Fact]
    public async Task A_contradictory_second_answer_does_not_change_the_verdict()
    {
        // Confirm then Cancel in quick succession. The first answer wins; a TCS cannot be set
        // twice, and the guarantee is that the turn saw exactly one of them.
        var store = new MentorApprovalStore();
        var id = Guid.NewGuid();
        store.RegisterPending(Request(id));

        var waiting = store.RegisterAndWaitAsync(id, CancellationToken.None);
        store.TryResolve(id, approved: true);
        store.TryResolve(id, approved: false);

        (await waiting.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
    }

    [Fact]
    public void Resolving_an_unknown_id_is_a_no_op()
    {
        // Reachable whenever a client answers after a restart. Throwing here would turn a stale
        // click into a 500.
        var store = new MentorApprovalStore();

        var act = () => store.TryResolve(Guid.NewGuid(), approved: true);

        act.Should().NotThrow();
        store.TryResolve(Guid.NewGuid(), approved: true).Should().BeNull();
    }

    [Fact]
    public async Task Concurrent_answers_release_the_turn_exactly_once()
    {
        // Eight callers answering the same request at once — a user hammering a button on a slow
        // connection. Only one may win, and none may throw.
        var store = new MentorApprovalStore();
        var id = Guid.NewGuid();
        store.RegisterPending(Request(id));
        var waiting = store.RegisterAndWaitAsync(id, CancellationToken.None);

        var resolved = 0;
        Parallel.For(0, 8, _ =>
        {
            if (store.TryResolve(id, approved: true) is not null) Interlocked.Increment(ref resolved);
        });

        resolved.Should().Be(1);
        (await waiting.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
    }

    // ── Cancellation ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Pressing_stop_cancels_a_pending_approval_rather_than_leaving_it_parked()
    {
        // The Stop button while a confirmation dialog is open. Without the cancellation
        // registration the turn would sit on the TCS until the request timed out.
        var store = new MentorApprovalStore();
        var id = Guid.NewGuid();
        store.RegisterPending(Request(id));

        using var cts = new CancellationTokenSource();
        var waiting = store.RegisterAndWaitAsync(id, cts.Token);

        await cts.CancelAsync();

        var act = async () => await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task An_answer_arriving_after_cancellation_does_not_throw()
    {
        // The approval was already in flight when the user pressed Stop. The HTTP endpoint must
        // still return cleanly.
        var store = new MentorApprovalStore();
        var id = Guid.NewGuid();
        store.RegisterPending(Request(id));

        using var cts = new CancellationTokenSource();
        var waiting = store.RegisterAndWaitAsync(id, cts.Token);
        await cts.CancelAsync();

        var act = () => store.TryResolve(id, approved: true);
        act.Should().NotThrow();

        var awaiting = async () => await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        await awaiting.Should().ThrowAsync<OperationCanceledException>(
            "cancellation wins over an answer that was already in flight");
    }

    [Fact]
    public async Task A_cancelled_wait_takes_its_request_off_the_pending_list()
    {
        // BUG-129: Stop cancelled the wait but left the request pending, so a late answer still found
        // it and was reported as an approval — OnApprovalResponse(true) for an action that never ran.
        var store = new MentorApprovalStore();
        var id = Guid.NewGuid();
        store.RegisterPending(Request(id));

        using var cts = new CancellationTokenSource();
        var waiting = store.RegisterAndWaitAsync(id, cts.Token);
        await cts.CancelAsync();
        var act = async () => await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        await act.Should().ThrowAsync<OperationCanceledException>();

        store.IsPending(id).Should().BeFalse("nobody waits for an answer any more");
        store.TryResolve(id, approved: true).Should().BeNull(
            "a late answer reached nothing, and must not be reported as one that did");
    }

    [Fact]
    public async Task An_answer_racing_a_cancellation_is_reported_only_when_the_turn_got_it()
    {
        // Stop and Confirm at the same moment. Either may win; what must never happen is the store
        // reporting an approval the waiting turn did not receive.
        for (var i = 0; i < 200; i++)
        {
            var store = new MentorApprovalStore();
            var id = Guid.NewGuid();
            store.RegisterPending(Request(id));

            using var cts = new CancellationTokenSource();
            var waiting = store.RegisterAndWaitAsync(id, cts.Token);

            var answer = Task.Run(() => store.TryResolve(id, approved: true));
            var cancel = Task.Run(() => cts.Cancel());
            await cancel;
            var reported = await answer;

            bool? verdict;
            try { verdict = await waiting.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { verdict = null; }

            if (reported is not null)
                verdict.Should().BeTrue("iteration {0}: the answer was reported, so the turn must have it", i);
            else
                verdict.Should().BeNull("iteration {0}: the answer was refused, so the turn was cancelled", i);
        }
    }

    [Fact]
    public async Task A_token_already_cancelled_fails_immediately()
    {
        var store = new MentorApprovalStore();
        var id = Guid.NewGuid();
        store.RegisterPending(Request(id));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () => await store.RegisterAndWaitAsync(id, cts.Token).WaitAsync(TimeSpan.FromSeconds(5));

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ── Isolation ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Two_pending_approvals_are_independent()
    {
        // Two browser tabs, or one turn that gated two tools. Answering one must not release the
        // other — approving something the user never saw is the worst possible failure here.
        var store = new MentorApprovalStore();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        store.RegisterPending(Request(first, "issue_refund"));
        store.RegisterPending(Request(second, "cancel_order"));

        var waitingFirst = store.RegisterAndWaitAsync(first, CancellationToken.None);
        var waitingSecond = store.RegisterAndWaitAsync(second, CancellationToken.None);

        store.TryResolve(first, approved: true);

        (await waitingFirst.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        waitingSecond.IsCompleted.Should().BeFalse("the other approval is still the user's to give");

        store.TryResolve(second, approved: false);
        (await waitingSecond.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeFalse();
    }

    [Fact]
    public async Task Cancelling_one_approval_leaves_the_other_waiting()
    {
        var store = new MentorApprovalStore();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        store.RegisterPending(Request(first));
        store.RegisterPending(Request(second));

        using var cts = new CancellationTokenSource();
        var waitingFirst = store.RegisterAndWaitAsync(first, cts.Token);
        var waitingSecond = store.RegisterAndWaitAsync(second, CancellationToken.None);

        await cts.CancelAsync();

        // Awaited rather than polled on IsCanceled: the wait is an async method, so cancellation
        // reaches the returned task one state-machine hop after the token fires.
        var act = async () => await waitingFirst.WaitAsync(TimeSpan.FromSeconds(5));
        await act.Should().ThrowAsync<OperationCanceledException>();

        waitingSecond.IsCompleted.Should().BeFalse("the other approval is still the user's to give");
    }

    [Fact]
    public async Task Many_concurrent_approvals_each_get_their_own_answer()
    {
        // The server case: many connections, each with a pending confirmation. A store that mixed
        // them up would approve one user's action from another user's click.
        var store = new MentorApprovalStore();
        var ids = Enumerable.Range(0, 50).Select(_ => Guid.NewGuid()).ToArray();

        foreach (var id in ids) store.RegisterPending(Request(id));
        var waits = ids.Select(id => store.RegisterAndWaitAsync(id, CancellationToken.None)).ToArray();

        // Approve the even ones, deny the odd ones, all at once.
        Parallel.For(0, ids.Length, i => store.TryResolve(ids[i], approved: i % 2 == 0));

        var results = await Task.WhenAll(waits);
        for (var i = 0; i < results.Length; i++)
            results[i].Should().Be(i % 2 == 0, "approval {0} got the wrong answer", i);
    }
}
