namespace Novior.MentorAgent.Core;

/// <summary>
/// Internal contract for the confirmation/approval store.
/// Implemented by <see cref="MentorStateService"/> (same Scoped instance as <see cref="IMentorStateService"/>).
///
/// <para>
/// Design: <c>MentorAgentUIMiddleware.InvokeFunctionCallingAsync</c> calls
/// <see cref="WaitForConfirmationAsync"/> which blocks (asynchronously) until the user
/// clicks Confirm or Cancel in the UI. The Blazor UI thread calls
/// <see cref="IMentorStateService.ConfirmAsync"/> or <see cref="IMentorStateService.Cancel"/>,
/// which resolve the <see cref="System.Threading.Tasks.TaskCompletionSource{TResult}"/>
/// and unblock the AI thread.
/// </para>
/// </summary>
internal interface IApprovalStore
{
    /// <summary>
    /// Registers a pending confirmation and returns a task that completes when the user responds.
    /// </summary>
    /// <returns><see langword="true"/> if approved, <see langword="false"/> if cancelled.</returns>
    Task<bool> WaitForConfirmationAsync(Guid actionId, CancellationToken cancellationToken = default);
}
