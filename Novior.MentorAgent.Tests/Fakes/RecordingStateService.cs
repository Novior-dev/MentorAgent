using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;

namespace Novior.MentorAgent.Tests.Fakes;

/// <summary>
/// An <see cref="IMentorStateService"/> that records every notification in order.
///
/// <para>
/// Most UI defects in this library have been ordering defects, not missing events: a chunk arriving
/// after completion, a busy flag cleared before the last token, an approval raised twice. Asserting
/// on final state cannot see any of those. This decorator keeps the whole sequence so a test can say
/// what happened <em>and</em> in what order.
/// </para>
///
/// <para>
/// It also forwards to the real events, so a component under test still behaves normally while the
/// sequence is being captured.
/// </para>
/// </summary>
internal sealed class RecordingStateService : IMentorStateService
{
    private readonly List<(string Event, object?[] Args)> _log = [];
    private readonly Dictionary<Guid, ConfirmationRequest> _pending = [];
    private readonly Lock _gate = new();

    /// <summary>Every notification, in the order it was raised.</summary>
    internal IReadOnlyList<(string Event, object?[] Args)> Log
    {
        get { lock (_gate) return _log.ToList(); }
    }

    /// <summary>Just the event names, in order — the usual subject of a sequence assertion.</summary>
    internal IReadOnlyList<string> Sequence => Log.Select(e => e.Event).ToList();

    /// <summary>Streamed text, concatenated in arrival order.</summary>
    internal string StreamedText => string.Concat(Log
        .Where(e => e.Event == nameof(NotifyStreamingChunk))
        .Select(e => e.Args[0] as string));

    /// <summary>Approvals answered through this service — id and verdict, in order.</summary>
    internal List<(Guid ActionId, bool Approved)> Answers { get; } = [];

    /// <summary>Clears the log without disturbing subscriptions.</summary>
    internal void Reset()
    {
        lock (_gate) _log.Clear();
        Answers.Clear();
    }

    /// <summary>How many times an event was raised.</summary>
    internal int Count(string eventName) => Log.Count(e => e.Event == eventName);

    private void Record(string name, params object?[] args)
    {
        lock (_gate) _log.Add((name, args));
    }

    // ── Events ───────────────────────────────────────────────────────────────────

    public event Action<string>? OnStreamingChunk;
    public event Action? OnStreamingCompleted;
    public event Action<string>? OnActionExecuting;
    public event Action<string>? OnActionCompleted;
    public event Action<string>? OnActionFailed;
    public event Action<bool>? OnBusyChanged;
    public event Action<string>? OnError;
    public event Action<ConfirmationRequest>? OnConfirmationRequired;
    public event Action<ConfirmationRequest, bool>? OnApprovalResponse;
    public event Action<string>? OnNavigationRequested;
    public event Action<string>? OnUIActionExecuting;
    public event Action<string>? OnUIActionCompleted;
    public event Action<IReadOnlyList<MentorRagResult>>? OnRagSourcesReady;
    public event Action<IReadOnlyList<string>>? OnGeneratedImages;
    public event Action<IReadOnlyList<MentorCard>>? OnCardsReady;
    public event Action<string, bool>? OnMcpServerStatusChanged;
    public event Action<MentorHostedTools>? OnHostedToolsDeclared;
    public event Action<string, string>? OnTeamMemberSpeaking;

    // ── Notify ───────────────────────────────────────────────────────────────────

    public void NotifyStreamingChunk(string chunk)
    {
        Record(nameof(NotifyStreamingChunk), chunk);
        OnStreamingChunk?.Invoke(chunk);
    }

    public void NotifyStreamingCompleted()
    {
        Record(nameof(NotifyStreamingCompleted));
        OnStreamingCompleted?.Invoke();
    }

    public void NotifyActionExecuting(string toolName, string? displayName = null)
    {
        Record(nameof(NotifyActionExecuting), toolName, displayName);
        OnActionExecuting?.Invoke(displayName ?? toolName);
    }

    public void NotifyActionCompleted(string toolName)
    {
        Record(nameof(NotifyActionCompleted), toolName);
        OnActionCompleted?.Invoke(toolName);
    }

    public void NotifyActionFailed(string toolName, string error)
    {
        Record(nameof(NotifyActionFailed), toolName, error);
        OnActionFailed?.Invoke(error);
    }

    public void NotifyBusyChanged(bool isBusy)
    {
        Record(nameof(NotifyBusyChanged), isBusy);
        OnBusyChanged?.Invoke(isBusy);
    }

    public void NotifyError(string message)
    {
        Record(nameof(NotifyError), message);
        OnError?.Invoke(message);
    }

    public void NotifyConfirmationRequired(ConfirmationRequest request)
    {
        Record(nameof(NotifyConfirmationRequired), request);
        lock (_gate) _pending[request.ActionId] = request;
        OnConfirmationRequired?.Invoke(request);
    }

    public void NotifyNavigationRequested(string url)
    {
        Record(nameof(NotifyNavigationRequested), url);
        OnNavigationRequested?.Invoke(url);
    }

    public void NotifyUIActionExecuting(string actionName)
    {
        Record(nameof(NotifyUIActionExecuting), actionName);
        OnUIActionExecuting?.Invoke(actionName);
    }

    public void NotifyUIActionCompleted(string actionName)
    {
        Record(nameof(NotifyUIActionCompleted), actionName);
        OnUIActionCompleted?.Invoke(actionName);
    }

    public void NotifyRagSourcesReady(IReadOnlyList<MentorRagResult> sources)
    {
        Record(nameof(NotifyRagSourcesReady), sources);
        OnRagSourcesReady?.Invoke(sources);
    }

    public void NotifyGeneratedImages(IReadOnlyList<string> imageSources)
    {
        Record(nameof(NotifyGeneratedImages), imageSources);
        OnGeneratedImages?.Invoke(imageSources);
    }

    public void NotifyCardsReady(IReadOnlyList<MentorCard> cards)
    {
        Record(nameof(NotifyCardsReady), cards);
        OnCardsReady?.Invoke(cards);
    }

    public void NotifyMcpServerStatusChanged(string serverName, bool connected)
    {
        Record(nameof(NotifyMcpServerStatusChanged), serverName, connected);
        OnMcpServerStatusChanged?.Invoke(serverName, connected);
    }

    public void NotifyHostedToolsDeclared(MentorHostedTools tools)
    {
        Record(nameof(NotifyHostedToolsDeclared), tools);
        OnHostedToolsDeclared?.Invoke(tools);
    }

    public void NotifyTeamMemberSpeaking(string teamName, string memberName)
    {
        Record(nameof(NotifyTeamMemberSpeaking), teamName, memberName);
        OnTeamMemberSpeaking?.Invoke(teamName, memberName);
    }

    // ── Approval ─────────────────────────────────────────────────────────────────

    public Task ConfirmAsync(Guid actionId)
    {
        Record(nameof(ConfirmAsync), actionId);
        Answers.Add((actionId, true));
        Resolve(actionId, approved: true);
        return Task.CompletedTask;
    }

    public void Cancel(Guid actionId)
    {
        Record(nameof(Cancel), actionId);
        Answers.Add((actionId, false));
        Resolve(actionId, approved: false);
    }

    /// <summary>
    /// Mirrors the real service: an answer raises <see cref="OnApprovalResponse"/> only when the
    /// id resolves to a pending request. Answering an unknown id — a double answer, or a stale
    /// dialog — is a no-op there, and must be a no-op here too, or tests would prove the wrong
    /// thing about repeated approvals.
    /// </summary>
    private void Resolve(Guid actionId, bool approved)
    {
        ConfirmationRequest? request;
        lock (_gate)
        {
            if (!_pending.Remove(actionId, out request)) return;
        }

        OnApprovalResponse?.Invoke(request, approved);
    }
}
