namespace Novior.MentorAgent.Core;

/// <summary>What a safety check came back with.</summary>
internal enum SafetyCheckOutcome
{
    /// <summary>A verdict: safe.</summary>
    Safe,

    /// <summary>A verdict: unsafe — blocked whatever <see cref="Options.MentorOptions.SafetyCheckFailure"/> says.</summary>
    Unsafe,

    /// <summary>No verdict: the check timed out, the provider failed, or a custom guardrail threw.</summary>
    Failed,

    /// <summary>No verdict because the user pressed Stop — the turn is over, nothing to report.</summary>
    Cancelled,
}

/// <param name="Outcome">The verdict, or why there is none.</param>
/// <param name="Error">The exception behind <see cref="SafetyCheckOutcome.Failed"/>.</param>
/// <param name="TimedOut">True when <see cref="SafetyCheckOutcome.Failed"/> is the timeout, not an error.</param>
internal readonly record struct SafetyCheckResult(SafetyCheckOutcome Outcome, Exception? Error = null, bool TimedOut = false);

/// <summary>
/// Runs one safety check — input or output, built-in or the host's own guardrail — under
/// <see cref="Options.MentorOptions.SafetyCheckTimeout"/>, and says how it ended.
///
/// <para>
/// One place, because the two checks had drifted apart in exactly the ways that matter. The input
/// check ran on a token of its own, so Stop could not interrupt it; it swallowed every exception into
/// "allowed", which made the host's timeout and its own identical; and a custom
/// <see cref="Options.MentorOptions.InputGuardrail"/> that honoured the 15-second token threw an
/// <see cref="OperationCanceledException"/> straight out of the turn — outside its try — so the busy
/// flag was never cleared and the widget spun for good. Deciding what a failure <i>means</i> is the
/// caller's (<see cref="Options.MentorOptions.SafetyCheckFailure"/>); telling the three kinds of
/// "no verdict" apart is this.
/// </para>
/// </summary>
internal static class SafetyCheckRunner
{
    /// <param name="check">The check itself: true = safe. It receives the bounded token.</param>
    /// <param name="timeout">Zero or negative = no limit (Stop still cancels).</param>
    /// <param name="turn">The turn's token — cancelled when the user presses Stop.</param>
    public static async Task<SafetyCheckResult> RunAsync(
        Func<CancellationToken, Task<bool>> check, TimeSpan timeout, CancellationToken turn)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(turn);
        if (timeout > TimeSpan.Zero)
            bounded.CancelAfter(timeout);

        try
        {
            return new(await check(bounded.Token) ? SafetyCheckOutcome.Safe : SafetyCheckOutcome.Unsafe);
        }
        catch (OperationCanceledException) when (turn.IsCancellationRequested)
        {
            return new(SafetyCheckOutcome.Cancelled);
        }
        catch (OperationCanceledException ex) when (bounded.IsCancellationRequested)
        {
            return new(SafetyCheckOutcome.Failed, ex, TimedOut: true);
        }
        catch (Exception ex)
        {
            // A check that noticed the Stop in some other way than by throwing
            // OperationCanceledException (an HttpRequestException wrapping the abort, say) is still
            // a Stop, not a provider failure.
            return turn.IsCancellationRequested
                ? new(SafetyCheckOutcome.Cancelled)
                : new(SafetyCheckOutcome.Failed, ex);
        }
    }
}
