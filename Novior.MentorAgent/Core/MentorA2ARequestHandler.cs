using A2A;

namespace Novior.MentorAgent.Core;

/// <summary>
/// The A2A SDK's request handler, with MentorAgent's own step before <c>tasks/cancel</c> (BUG-199). Every other request
/// goes to the SDK as it is.
///
/// <para>
/// On <c>tasks/cancel</c> the SDK cancels the token of the task's turn first and calls
/// <see cref="MentorAgentA2AHandler.CancelAsync"/> only after. A call in progress was then stopped before anything
/// said why, and its decision read <c>session-ended</c>, as when a connection closes. Here the turn is stopped as Stop
/// stops it — its decisions read <c>stop</c> — and then the SDK goes on.
/// </para>
///
/// <para>
/// From A2A 1.0.0-preview4 every request carries the transport's <see cref="A2ARequestContext"/> (null when the
/// request negotiated no extension): it is handed on to the SDK with the cancellation token, never dropped.
/// </para>
/// </summary>
internal sealed class MentorA2ARequestHandler(IA2ARequestHandler sdk, MentorAgentA2AHandler agent)
    : IA2ARequestHandler, IAsyncDisposable
{
    public async Task<AgentTask> CancelTaskAsync(CancelTaskRequest request, A2ARequestContext? requestContext, CancellationToken cancellationToken = default)
    {
        // A task whose turn runs here is cancelled by MentorAgent: the turn stops as with Stop, and its execution returns
        // and ends the task Canceled (MentorAgentA2AHandler). From A2A 1.0.0-preview4 the SDK's own tasks/cancel waits for
        // the execution to end, and then finds the task already Canceled; so the cancel is answered with the task once
        // it is. The SDK cancels only a task with no turn here, or one that did not end Canceled in time.
        if (agent.StopByCaller(request.Id))
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            wait.CancelAfter(CanceledWait);
            try
            {
                while (true)
                {
                    var task = await sdk.GetTaskAsync(new GetTaskRequest { Id = request.Id }, requestContext, wait.Token);
                    if (task.Status.State == TaskState.Canceled) return task;
                    if (task.Status.State.IsTerminal()) break;   // it ended otherwise first: the SDK says it cannot be cancelled
                    await Task.Delay(10, wait.Token);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Not Canceled in time: the SDK's tasks/cancel does it.
            }
        }
        return await sdk.CancelTaskAsync(request, requestContext, cancellationToken);
    }

    /// <summary>How long a cancel waits for a stopped turn to end its task Canceled: well under a caller's own wait.</summary>
    private static readonly TimeSpan CanceledWait = TimeSpan.FromSeconds(1);

    public Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, A2ARequestContext? requestContext, CancellationToken cancellationToken = default)
        => sdk.SendMessageAsync(request, requestContext, cancellationToken);

    public IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(SendMessageRequest request, A2ARequestContext? requestContext, CancellationToken cancellationToken = default)
        => sdk.SendStreamingMessageAsync(request, requestContext, cancellationToken);

    public Task<AgentTask> GetTaskAsync(GetTaskRequest request, A2ARequestContext? requestContext, CancellationToken cancellationToken = default)
        => sdk.GetTaskAsync(request, requestContext, cancellationToken);

    public Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, A2ARequestContext? requestContext, CancellationToken cancellationToken = default)
        => sdk.ListTasksAsync(request, requestContext, cancellationToken);

    public IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(SubscribeToTaskRequest request, A2ARequestContext? requestContext, CancellationToken cancellationToken = default)
        => sdk.SubscribeToTaskAsync(request, requestContext, cancellationToken);

    public Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(TaskPushNotificationConfig config, A2ARequestContext? requestContext, CancellationToken cancellationToken = default)
        => sdk.CreateTaskPushNotificationConfigAsync(config, requestContext, cancellationToken);

    public Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(GetTaskPushNotificationConfigRequest request, A2ARequestContext? requestContext, CancellationToken cancellationToken = default)
        => sdk.GetTaskPushNotificationConfigAsync(request, requestContext, cancellationToken);

    public Task<ListTaskPushNotificationConfigsResponse> ListTaskPushNotificationConfigsAsync(ListTaskPushNotificationConfigsRequest request, A2ARequestContext? requestContext, CancellationToken cancellationToken = default)
        => sdk.ListTaskPushNotificationConfigsAsync(request, requestContext, cancellationToken);

    public Task DeleteTaskPushNotificationConfigAsync(DeleteTaskPushNotificationConfigRequest request, A2ARequestContext? requestContext, CancellationToken cancellationToken = default)
        => sdk.DeleteTaskPushNotificationConfigAsync(request, requestContext, cancellationToken);

    public Task<AgentCard> GetExtendedAgentCardAsync(GetExtendedAgentCardRequest request, A2ARequestContext? requestContext, CancellationToken cancellationToken = default)
        => sdk.GetExtendedAgentCardAsync(request, requestContext, cancellationToken);

    /// <summary>
    /// The SDK's server cancels and joins the turns still running when it is disposed: the container disposes this
    /// instead of it, so this passes it on.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        switch (sdk)
        {
            case IAsyncDisposable async: await async.DisposeAsync(); break;
            case IDisposable sync:       sync.Dispose();             break;
        }
    }
}
