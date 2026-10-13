namespace Novior.MentorAgent.Core;

/// <summary>
/// Thread-safe event bus between Agent Framework (AI thread) and the Blazor UI (rendering thread).
/// Also implements <see cref="IApprovalStore"/> to block the AI thread while the user
/// responds to a confirmation banner.
/// </summary>
internal sealed class MentorStateService : IMentorStateService, IApprovalStore
{
    private readonly ILogger<MentorStateService> _logger;
    private readonly MentorApprovalStore _store;

    public MentorStateService(ILogger<MentorStateService> logger, MentorApprovalStore store)
    {
        _logger = logger;
        _store  = store;
    }

    // -- Events ----------------------------------------------------------------

    public event Action<string>?                              OnStreamingChunk;
    public event Action?                                      OnStreamingCompleted;
    public event Action<string>?                              OnActionExecuting;
    public event Action<string>?                              OnActionCompleted;
    public event Action<string>?                              OnActionFailed;
    public event Action<bool>?                                OnBusyChanged;
    public event Action<string>?                              OnError;
    public event Action<ConfirmationRequest>?                 OnConfirmationRequired;
    public event Action<ConfirmationRequest, bool>?           OnApprovalResponse;
    public event Action<string>?                              OnNavigationRequested;
    public event Action<string>?                              OnUIActionExecuting;
    public event Action<string>?                              OnUIActionCompleted;
    public event Action<IReadOnlyList<MentorRagResult>>?      OnRagSourcesReady;
    public event Action<IReadOnlyList<string>>?               OnGeneratedImages;
    public event Action<IReadOnlyList<MentorCard>>?           OnCardsReady;
    public event Action<string, bool>?                        OnMcpServerStatusChanged;
    public event Action<MentorHostedTools>?                   OnHostedToolsDeclared;
    public event Action<string, string>?                      OnTeamMemberSpeaking;

    // -- Notify (called by middleware on the AI thread) --------------------------

    public void NotifyStreamingChunk(string chunk)             => OnStreamingChunk?.Invoke(chunk);
    public void NotifyStreamingCompleted()                     => OnStreamingCompleted?.Invoke();
    public void NotifyBusyChanged(bool isBusy)                 => OnBusyChanged?.Invoke(isBusy);
    public void NotifyError(string message)                    => OnError?.Invoke(message);
    public void NotifyActionCompleted(string toolName)         => OnActionCompleted?.Invoke(toolName);
    public void NotifyNavigationRequested(string url)          => OnNavigationRequested?.Invoke(url);
    public void NotifyUIActionExecuting(string actionName)     => OnUIActionExecuting?.Invoke(actionName);
    public void NotifyUIActionCompleted(string actionName)     => OnUIActionCompleted?.Invoke(actionName);
    public void NotifyMcpServerStatusChanged(string name, bool connected) => OnMcpServerStatusChanged?.Invoke(name, connected);
    // In-process hosts read the active set straight from MentorWidgetOptions, so nothing raises this
    // locally; it exists for transports that have to tell a detached client what the server enabled.
    public void NotifyHostedToolsDeclared(MentorHostedTools tools)        => OnHostedToolsDeclared?.Invoke(tools);
    public void NotifyTeamMemberSpeaking(string team, string member)      => OnTeamMemberSpeaking?.Invoke(team, member);

    public void NotifyActionFailed(string toolName, string error)
        => OnActionFailed?.Invoke($"{toolName}: {error}");

    public void NotifyActionExecuting(string toolName, string? displayName = null)
        => OnActionExecuting?.Invoke(displayName ?? toolName);

    public void NotifyRagSourcesReady(IReadOnlyList<MentorRagResult> sources)
        => OnRagSourcesReady?.Invoke(sources);

    public void NotifyGeneratedImages(IReadOnlyList<string> imageSources)
        => OnGeneratedImages?.Invoke(imageSources);

    public void NotifyCardsReady(IReadOnlyList<MentorCard> cards)
        => OnCardsReady?.Invoke(cards);

    public void NotifyConfirmationRequired(ConfirmationRequest request)
    {
        _store.RegisterPending(request);
        _logger.LogDebug("[MentorAgent:Approval] NotifyConfirmationRequired — ActionId={Id}", request.ActionId);
        OnConfirmationRequired?.Invoke(request);
    }

    // -- IApprovalStore -------------------------------------------------------

    public Task<bool> WaitForConfirmationAsync(Guid actionId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("[MentorAgent:Approval] WaitForConfirmationAsync — ActionId={Id}", actionId);
        return _store.RegisterAndWaitAsync(actionId, cancellationToken);
    }

    // -- Confirm / Cancel (called by the UI on the Blazor thread) ---------------

    public Task ConfirmAsync(Guid actionId)
    {
        Answer(actionId, approved: true);
        return Task.CompletedTask;
    }

    public void Cancel(Guid actionId) => Answer(actionId, approved: false);

    /// <summary>
    /// Answers a confirmation and reports whether a turn was still waiting for it. Only then is
    /// <see cref="OnApprovalResponse"/> raised: an answer after Stop, after the turn's scope ended or
    /// a second time is not a decision anything acted on (BUG-129). <c>POST /mentor/approve</c> turns
    /// <see langword="false"/> into a 404.
    /// </summary>
    internal bool Answer(Guid actionId, bool approved)
    {
        var caller = approved ? nameof(ConfirmAsync) : nameof(Cancel);
        _logger.LogDebug("[MentorAgent:Approval] {Caller} — ActionId={Id}", caller, actionId);

        var req = _store.TryResolve(actionId, approved);
        if (req is null)
        {
            _logger.LogWarning(
                "[MentorAgent:Approval] {Caller} — request not found for ActionId={Id}: answered already, stopped, or its turn ended.",
                caller, actionId);
            return false;
        }

        OnApprovalResponse?.Invoke(req, approved);
        return true;
    }
}
