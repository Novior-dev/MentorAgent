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
/// </summary>
internal sealed class MentorA2ARequestHandler(IA2ARequestHandler sdk, MentorAgentA2AHandler agent)
    : IA2ARequestHandler, IAsyncDisposable
{
    public Task<AgentTask> CancelTaskAsync(CancelTaskRequest request, CancellationToken cancellationToken)
    {
        agent.StopByCaller(request.Id);
        return sdk.CancelTaskAsync(request, cancellationToken);
    }

    public Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, CancellationToken cancellationToken)
        => sdk.SendMessageAsync(request, cancellationToken);

    public IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(SendMessageRequest request, CancellationToken cancellationToken)
        => sdk.SendStreamingMessageAsync(request, cancellationToken);

    public Task<AgentTask> GetTaskAsync(GetTaskRequest request, CancellationToken cancellationToken)
        => sdk.GetTaskAsync(request, cancellationToken);

    public Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, CancellationToken cancellationToken)
        => sdk.ListTasksAsync(request, cancellationToken);

    public IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(SubscribeToTaskRequest request, CancellationToken cancellationToken)
        => sdk.SubscribeToTaskAsync(request, cancellationToken);

    public Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(TaskPushNotificationConfig config, CancellationToken cancellationToken)
        => sdk.CreateTaskPushNotificationConfigAsync(config, cancellationToken);

    public Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(GetTaskPushNotificationConfigRequest request, CancellationToken cancellationToken)
        => sdk.GetTaskPushNotificationConfigAsync(request, cancellationToken);

    public Task<ListTaskPushNotificationConfigsResponse> ListTaskPushNotificationConfigsAsync(ListTaskPushNotificationConfigsRequest request, CancellationToken cancellationToken)
        => sdk.ListTaskPushNotificationConfigsAsync(request, cancellationToken);

    public Task DeleteTaskPushNotificationConfigAsync(DeleteTaskPushNotificationConfigRequest request, CancellationToken cancellationToken)
        => sdk.DeleteTaskPushNotificationConfigAsync(request, cancellationToken);

    public Task<AgentCard> GetExtendedAgentCardAsync(GetExtendedAgentCardRequest request, CancellationToken cancellationToken)
        => sdk.GetExtendedAgentCardAsync(request, cancellationToken);

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
