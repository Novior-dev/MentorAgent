using Novior.MentorAgent.Core;
using Microsoft.AspNetCore.SignalR;

namespace Novior.MentorAgent.Server.Hubs;

/// <summary>
/// Decorates <see cref="IMentorStateService"/> to forward every AI event
/// to the connected SignalR client via <c>Clients.Client(connectionId).SendAsync(...)</c>.
///
/// Also bridges the <see cref="IMentorPageContext"/> by applying page context snapshots
/// received from the remote client before each AI call.
///
/// One instance per SignalR connection scope.
/// </summary>
public sealed class HubStateServiceDecorator : IDisposable
{
    private readonly IMentorStateService _inner;
    private readonly IMentorPageContext  _pageContext;
    private readonly IHubContext<MentorHub> _hubContext;
    private readonly ILogger<HubStateServiceDecorator> _logger;

    private string? _connectionId;

    private readonly MentorApprovalOwners _approvalOwners;

    public HubStateServiceDecorator(
        IMentorStateService stateService,
        IMentorPageContext pageContext,
        IHubContext<MentorHub> hubContext,
        MentorApprovalOwners approvalOwners,
        ILogger<HubStateServiceDecorator> logger)
    {
        _inner           = stateService;
        _pageContext     = pageContext;
        _hubContext      = hubContext;
        _approvalOwners  = approvalOwners;
        _logger          = logger;

        // Subscribe to all events and forward to SignalR client
        _inner.OnStreamingChunk        += OnStreamingChunk;
        _inner.OnStreamingCompleted    += OnStreamingCompleted;
        _inner.OnBusyChanged           += OnBusyChanged;
        _inner.OnError                 += OnError;
        _inner.OnActionExecuting       += OnActionExecuting;
        _inner.OnActionCompleted       += OnActionCompleted;
        _inner.OnActionFailed          += OnActionFailed;
        _inner.OnConfirmationRequired  += OnConfirmationRequired;
        _inner.OnNavigationRequested   += OnNavigationRequested;
        _inner.OnRagSourcesReady       += OnRagSourcesReady;
        _inner.OnGeneratedImages       += OnGeneratedImages;
        _inner.OnCardsReady            += OnCardsReady;
        _inner.OnTeamMemberSpeaking    += OnTeamMemberSpeaking;
        _inner.OnUIActionExecuting     += OnUIActionExecuting;
        _inner.OnUIActionCompleted     += OnUIActionCompleted;
        _inner.OnMcpServerStatusChanged += OnMcpServerStatusChanged;
    }

    public void SetConnectionId(string connectionId) => _connectionId = connectionId;

    // â”€â”€ Page context snapshot â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    public void ApplySnapshot(PageContextSnapshot snapshot)
    {
        _pageContext.SetPageName(snapshot.PageName ?? string.Empty);
        _pageContext.Clear();
        if (!string.IsNullOrEmpty(snapshot.PageName))
            _pageContext.SetPageName(snapshot.PageName);

        foreach (var (key, value) in snapshot.ContextData)
            _pageContext.Set(key, value);

        // Register pseudo UI-action handlers that forward invocations to the client
        foreach (var action in snapshot.UIActions)
        {
            var capturedAction = action; // capture for lambda
            // The overload that keeps the client's ParameterHint: the model reads the parameter's
            // shape only from that text ("(parameter: integer)"), and the 3-argument overload dropped it.
            // Still fire-and-forget: the browser runs the action; the turn does not wait for it.
            _pageContext.RegisterUIActionAsync(
                capturedAction.Name,
                capturedAction.Description,
                param =>
                {
                    var paramJson = param is not null ? JsonSerializer.Serialize(param) : null;
                    _ = SendAsync("UIActionRequested", capturedAction.Name, paramJson);
                    return Task.CompletedTask;
                },
                capturedAction.ParameterHint);
        }
    }

    // â”€â”€ Event forwarders â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private void OnStreamingChunk(string chunk)
        => _ = SendAsync("StreamingChunk", chunk);

    private void OnStreamingCompleted()
        => _ = SendAsync("StreamingCompleted");

    private void OnBusyChanged(bool busy)
        => _ = SendAsync("BusyChanged", busy);

    private void OnError(string message)
        => _ = SendAsync("Error", message);

    private void OnActionExecuting(string action)
        => _ = SendAsync("ActionExecuting", action);

    private void OnActionCompleted(string action)
        => _ = SendAsync("ActionCompleted", action);

    private void OnActionFailed(string error)
        => _ = SendAsync("ActionFailed", error);

    private void OnConfirmationRequired(ConfirmationRequest req)
    {
        // Record who is being asked before telling them. POST /mentor/approve has only an action id
        // to go on, so without this it cannot tell the owner from anybody else (BUG-058).
        if (_connectionId is not null)
            _approvalOwners.Record(req.ActionId, _connectionId);

        _ = SendAsync("ConfirmationRequired", req.ActionId.ToString(), req.ToolName, req.Message);
    }

    private void OnNavigationRequested(string url)
        => _ = SendAsync("NavigationRequested", url);

    private void OnRagSourcesReady(IReadOnlyList<MentorRagResult> sources)
        => _ = SendAsync("RagSourcesReady", sources);

    /// <summary>
    /// Forwards provider-generated images (#15). A base64 image is bulky, so it is sent as its own
    /// message rather than folded into the stream — and only when the provider actually produced one.
    /// </summary>
    private void OnGeneratedImages(IReadOnlyList<string> images)
        => _ = SendAsync("GeneratedImages", images);

    /// <summary>
    /// Forwards generative-UI cards (#14 L2). Built by a server-side tool, so the client receives a
    /// structure it can render as-is — including the buttons, whose labels and targets were written
    /// by application code rather than produced by the model.
    /// </summary>
    private void OnCardsReady(IReadOnlyList<MentorCard> cards)
        => _ = SendAsync("Cards", cards);

    private void OnTeamMemberSpeaking(string team, string member)
        => _ = SendAsync("TeamMemberSpeaking", team, member);

    private void OnUIActionExecuting(string action)
        => _ = SendAsync("UIActionExecuting", action);

    private void OnUIActionCompleted(string action)
        => _ = SendAsync("UIActionCompleted", action);

    private void OnMcpServerStatusChanged(string name, bool connected)
        => _ = SendAsync("McpServerStatusChanged", name, connected);

    private Task SendAsync(string method, params object?[] args)
    {
        if (_connectionId is null) return Task.CompletedTask;
        try
        {
            return _hubContext.Clients.Client(_connectionId).SendCoreAsync(method, args);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[MentorAgent.Server] Failed to send '{Method}' to client {Id}",
                method, _connectionId);
            return Task.CompletedTask;
        }
    }

    public void Dispose()
    {
        _inner.OnStreamingChunk        -= OnStreamingChunk;
        _inner.OnStreamingCompleted    -= OnStreamingCompleted;
        _inner.OnBusyChanged           -= OnBusyChanged;
        _inner.OnError                 -= OnError;
        _inner.OnActionExecuting       -= OnActionExecuting;
        _inner.OnActionCompleted       -= OnActionCompleted;
        _inner.OnActionFailed          -= OnActionFailed;
        _inner.OnConfirmationRequired  -= OnConfirmationRequired;
        _inner.OnNavigationRequested   -= OnNavigationRequested;
        _inner.OnRagSourcesReady       -= OnRagSourcesReady;
        _inner.OnGeneratedImages       -= OnGeneratedImages;
        _inner.OnCardsReady            -= OnCardsReady;
        _inner.OnTeamMemberSpeaking    -= OnTeamMemberSpeaking;
        _inner.OnUIActionExecuting     -= OnUIActionExecuting;
        _inner.OnUIActionCompleted     -= OnUIActionCompleted;
        _inner.OnMcpServerStatusChanged -= OnMcpServerStatusChanged;
    }
}
