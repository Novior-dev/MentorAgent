using Novior.MentorAgent.Blazor.Hub;
using Microsoft.Extensions.Logging;
using System.Net.Http;

namespace Novior.MentorAgent.Blazor.Services;

/// <summary>
/// WASM implementation of <see cref="IMentorStateService"/>.
/// Subscribes to the SignalR hub callbacks from <see cref="MentorHubClient"/>
/// and raises the corresponding events so <see cref="Novior.MentorAgent.Abstractions.Components.ChatWidget"/>
/// works identically to Blazor Server.
/// </summary>
public sealed class WasmMentorStateService : IMentorStateService, IAsyncDisposable
{
    private readonly MentorHubClient _hub;
    private readonly WasmMentorPageContext _pageContext;
    private readonly ILogger<WasmMentorStateService> _logger;
    private readonly HttpClient _http;
    private readonly MentorAgentBlazorOptions? _options;
    private readonly List<IDisposable> _subscriptions = [];

    // -- Pending confirmations -------------------------------------------------
    private readonly Dictionary<Guid, ConfirmationRequest> _pending = new();
    private readonly Lock _lock = new();

    // -- Events ----------------------------------------------------------------

    public event Action<string>?                          OnStreamingChunk;
    public event Action?                                  OnStreamingCompleted;
    public event Action<string>?                          OnActionExecuting;
    public event Action<string>?                          OnActionCompleted;
    public event Action<string>?                          OnActionFailed;
    public event Action<bool>?                            OnBusyChanged;
    public event Action<string>?                          OnError;
    public event Action<ConfirmationRequest>?             OnConfirmationRequired;
    public event Action<ConfirmationRequest, bool>?       OnApprovalResponse;
    public event Action<string>?                          OnNavigationRequested;
    public event Action<string>?                          OnUIActionExecuting;
    public event Action<string>?                          OnUIActionCompleted;
    public event Action<IReadOnlyList<MentorRagResult>>?  OnRagSourcesReady;
    public event Action<IReadOnlyList<string>>?           OnGeneratedImages;
    public event Action<IReadOnlyList<MentorCard>>?       OnCardsReady;
    public event Action<string, bool>?                    OnMcpServerStatusChanged;
    public event Action<MentorHostedTools>?               OnHostedToolsDeclared;
    public event Action<string, string>?                  OnTeamMemberSpeaking;

    public WasmMentorStateService(MentorHubClient hub, WasmMentorPageContext pageContext, ILogger<WasmMentorStateService> logger, HttpClient http,
        MentorAgentBlazorOptions? options = null)
    {
        _options     = options;
        _hub         = hub;
        _pageContext = pageContext;
        _logger      = logger;
        _http        = http;
        SubscribeToHub();
    }

    private void SubscribeToHub()
    {
        _subscriptions.Add(_hub.On<string>("StreamingChunk",   t  => OnStreamingChunk?.Invoke(t)));
        _subscriptions.Add(_hub.On("StreamingCompleted",       () => OnStreamingCompleted?.Invoke()));
        _subscriptions.Add(_hub.On<bool>("BusyChanged",        b  =>
        {
            // Idle means the turn is over — finished or stopped — and nothing is waiting for an answer.
            if (!b) lock (_lock) _pending.Clear();
            OnBusyChanged?.Invoke(b);
        }));
        _subscriptions.Add(_hub.On<string>("Error",            e  => OnError?.Invoke(e)));
        _subscriptions.Add(_hub.On<string>("ActionExecuting",  a  => OnActionExecuting?.Invoke(a)));
        _subscriptions.Add(_hub.On<string>("ActionCompleted",  a  => OnActionCompleted?.Invoke(a)));
        _subscriptions.Add(_hub.On<string>("ActionFailed",     e  => OnActionFailed?.Invoke(e)));
        _subscriptions.Add(_hub.On<string>("NavigationRequested", url => OnNavigationRequested?.Invoke(url)));
        _subscriptions.Add(_hub.On<string>("UIActionExecuting",   a => OnUIActionExecuting?.Invoke(a)));
        _subscriptions.Add(_hub.On<string>("UIActionCompleted",   a => OnUIActionCompleted?.Invoke(a)));

        _subscriptions.Add(_hub.On<string, string, string>("ConfirmationRequired",
            (actionId, toolName, message) =>
            {
                if (!Guid.TryParse(actionId, out var id)) return;
                var req = new ConfirmationRequest { ActionId = id, ToolName = toolName, Message = message };
                lock (_lock) _pending[id] = req;
                OnConfirmationRequired?.Invoke(req);
            }));

        _subscriptions.Add(_hub.On<List<MentorRagResult>>("RagSourcesReady",
            sources => OnRagSourcesReady?.Invoke(sources)));

        // Hosted image generation (#15): ready-to-render sources (data: URI or remote URL).
        _subscriptions.Add(_hub.On<List<string>>("GeneratedImages",
            images => OnGeneratedImages?.Invoke(images)));

        // Generative UI L2 (#14): structured cards built by a server-side tool.
        _subscriptions.Add(_hub.On<List<MentorCard>>("Cards",
            cards => OnCardsReady?.Invoke(cards)));

        _subscriptions.Add(_hub.On<string, string>("TeamMemberSpeaking",
            (team, member) => OnTeamMemberSpeaking?.Invoke(team, member)));

        _subscriptions.Add(_hub.On<string, bool>("McpServerStatusChanged",
            (name, connected) => NotifyMcpServerStatusChanged(name, connected)));

        // Hosted tools (#15): sent once on connect as the raw flags. This is what the badge should
        // trust — the client cannot know a server-side capability from its own configuration.
        _subscriptions.Add(_hub.On<int>("HostedToolsDeclared",
            flags => NotifyHostedToolsDeclared((MentorHostedTools)flags)));

        // UIActionRequested — server wants client to execute a registered UI action
        _subscriptions.Add(_hub.On<string, string?>("UIActionRequested",
            (actionName, paramJson) =>
            {
                var actions = _pageContext.UIActions;
                if (!actions.TryGetValue(actionName, out var reg)) return;

                object? param = paramJson is not null
                    ? (object?)JsonSerializer.Deserialize<JsonElement>(paramJson)
                    : null;

                if (reg.HandlerAsync is not null)
                    _ = reg.HandlerAsync(param);
                else
                    reg.Handler(param);
            }));
    }

    // -- Notify (not used in WASM — events come from hub) ---------------------

    public void NotifyStreamingChunk(string chunk)                               => OnStreamingChunk?.Invoke(chunk);
    public void NotifyStreamingCompleted()                                        => OnStreamingCompleted?.Invoke();
    public void NotifyActionExecuting(string toolName, string? displayName = null) => OnActionExecuting?.Invoke(displayName ?? toolName);
    public void NotifyActionCompleted(string toolName)                            => OnActionCompleted?.Invoke(toolName);
    public void NotifyActionFailed(string toolName, string error)                 => OnActionFailed?.Invoke($"{toolName}: {error}");
    public void NotifyBusyChanged(bool isBusy)                                    => OnBusyChanged?.Invoke(isBusy);
    public void NotifyError(string message)                                       => OnError?.Invoke(message);
    public void NotifyConfirmationRequired(ConfirmationRequest req)               { lock (_lock) _pending[req.ActionId] = req; OnConfirmationRequired?.Invoke(req); }
    public void NotifyNavigationRequested(string url)                             => OnNavigationRequested?.Invoke(url);
    public void NotifyUIActionExecuting(string actionName)                        => OnUIActionExecuting?.Invoke(actionName);
    public void NotifyUIActionCompleted(string actionName)                        => OnUIActionCompleted?.Invoke(actionName);
    public void NotifyRagSourcesReady(IReadOnlyList<MentorRagResult> sources)     => OnRagSourcesReady?.Invoke(sources);
    public void NotifyGeneratedImages(IReadOnlyList<string> imageSources)        => OnGeneratedImages?.Invoke(imageSources);
    public void NotifyCardsReady(IReadOnlyList<MentorCard> cards)                 => OnCardsReady?.Invoke(cards);
    public void NotifyMcpServerStatusChanged(string name, bool connected)         => OnMcpServerStatusChanged?.Invoke(name, connected);
    public void NotifyHostedToolsDeclared(MentorHostedTools tools)                => OnHostedToolsDeclared?.Invoke(tools);
    public void NotifyTeamMemberSpeaking(string team, string member)              => OnTeamMemberSpeaking?.Invoke(team, member);

    // -- Confirm / Cancel -----------------------------------------------------

    public async Task ConfirmAsync(Guid actionId)
    {
        _logger.LogDebug("[WasmMentorStateService] ConfirmAsync — ActionId={Id}", actionId);
        var req = TakePending(actionId);
        // HTTP endpoint instead of hub: SignalR processes messages sequentially per connection,
        // so RespondToApproval via hub would deadlock while SendMessage is awaiting.
        try
        {
            // To the hub's origin, with the hub's token: see MentorServerRequests.
            var response = await MentorServerRequests.PostAsync(_http, _options, $"/mentor/approve?actionId={actionId}&approved=true");
            Answered(nameof(ConfirmAsync), req, approved: true, actionId, response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[WasmMentorStateService] ConfirmAsync — HTTP FAILED, ActionId={Id}", actionId);
        }
    }

    public void Cancel(Guid actionId)
    {
        _logger.LogDebug("[WasmMentorStateService] Cancel — ActionId={Id}", actionId);
        var req = TakePending(actionId);
        _ = MentorServerRequests.PostAsync(_http, _options, $"/mentor/approve?actionId={actionId}&approved=false")
                 .ContinueWith(t =>
                 {
                     if (t.IsFaulted) _logger.LogError(t.Exception, "[WasmMentorStateService] Cancel — HTTP FAILED, ActionId={Id}", actionId);
                     else Answered(nameof(Cancel), req, approved: false, actionId, t.Result);
                 }, TaskScheduler.Default);
    }

    private ConfirmationRequest? TakePending(Guid actionId)
    {
        lock (_lock) return _pending.Remove(actionId, out var req) ? req : null;
    }

    /// <summary>
    /// Raises <see cref="OnApprovalResponse"/> only when the server says the answer reached a turn that was
    /// waiting (BUG-129). Up to rc.14 it was raised before the request: a click that landed after Stop, before
    /// the idle state arrived, was reported as an approval although nothing ran. A 404 is that case, not a fault.
    /// </summary>
    private void Answered(string caller, ConfirmationRequest? req, bool approved, Guid actionId, HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            _logger.LogDebug("[WasmMentorStateService] {Caller} — HTTP OK, ActionId={Id}", caller, actionId);
            if (req is not null) OnApprovalResponse?.Invoke(req, approved);
        }
        else if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            _logger.LogDebug("[WasmMentorStateService] {Caller} — nothing waits for ActionId={Id} any more (answered, stopped or ended).",
                caller, actionId);
        else
            _logger.LogError("[WasmMentorStateService] {Caller} — HTTP {Status}, ActionId={Id}", caller, (int)response.StatusCode, actionId);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var sub in _subscriptions) sub.Dispose();
        _subscriptions.Clear();
    }
}
