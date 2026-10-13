using Novior.MentorAgent.Blazor.Hub;

namespace Novior.MentorAgent.Blazor.Services;

/// <summary>
/// WASM implementation of <see cref="IMentorOrchestrator"/>.
/// Delegates all operations to <see cref="MentorHubClient"/> (SignalR).
/// Before each <c>SendMessageAsync</c>, sends the current page context
/// snapshot to the server so the AI has the correct page state.
/// </summary>
public sealed class WasmMentorOrchestrator : IMentorOrchestrator, IAsyncDisposable
{
    private readonly MentorHubClient     _hub;
    private readonly WasmMentorPageContext _pageContext;
    private readonly ILogger<WasmMentorOrchestrator> _logger;
    private readonly HttpClient _http;
    private readonly MentorAgentBlazorOptions? _options;

    public WasmMentorOrchestrator(
        MentorHubClient hub,
        WasmMentorPageContext pageContext,
        ILogger<WasmMentorOrchestrator> logger,
        HttpClient http,
        MentorAgentBlazorOptions? options = null)
    {
        _options     = options;
        _hub         = hub;
        _pageContext = pageContext;
        _logger      = logger;
        _http        = http;
    }

    /// <summary>Ensures the SignalR connection is established.</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
        => await _hub.StartAsync(ct);

    public Task SendMessageAsync(string userMessage, CancellationToken ct = default)
        => SendMessageAsync(userMessage, null, ct);

    public async Task SendMessageAsync(string userMessage, IReadOnlyList<MentorAttachment>? attachments, CancellationToken ct = default)
    {
        if (!_hub.IsConnected)
        {
            _logger.LogWarning("[MentorAgent.Blazor] Hub not connected. Attempting to connect...");
            await _hub.StartAsync(ct);
        }

        // Send page context before each message so the server has the current state
        var snapshot = _pageContext.BuildSnapshot();
        await _hub.UpdatePageContextAsync(snapshot, ct);
        await _hub.SendMessageAsync(userMessage, attachments, ct);
    }

    /// <summary>
    /// Stops the in-flight AI turn. This goes over HTTP, NOT the hub: SignalR dispatches at most
    /// one hub invocation at a time per connection, so a <c>CancelRequest</c> hub call would queue
    /// behind the still-running <c>SendMessage</c> and only run after the turn it was meant to
    /// abort had finished — i.e. the Stop button would appear to do nothing. Same reasoning as
    /// the HITL approval in <c>WasmMentorStateService</c>.
    /// </summary>
    public void CancelCurrentRequest()
    {
        var connectionId = _hub.ConnectionId;
        if (string.IsNullOrEmpty(connectionId))
        {
            // No connection means no turn is running server-side: the server aborts the turn when
            // the connection drops. There is nothing to cancel, and invoking the hub here could
            // only throw into a fire-and-forget task.
            _logger.LogDebug("[MentorAgent.Blazor] Cancel ignored — no live connection.");
            return;
        }

        _ = CancelOverHttpAsync(connectionId);
    }

    private async Task CancelOverHttpAsync(string connectionId)
    {
        try
        {
            var response = await MentorServerRequests.PostAsync(_http, _options,
                $"/mentor/cancel?connectionId={Uri.EscapeDataString(connectionId)}");
            if (!response.IsSuccessStatusCode)
                _logger.LogWarning("[MentorAgent.Blazor] Cancel failed — HTTP {Status}.", (int)response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MentorAgent.Blazor] Cancel request failed.");
        }
    }

    /// <summary>
    /// Starts a new conversation on the server. With no live hub connection there is nothing to
    /// reset — the hub connects on the first message, and every connection gets its own server scope,
    /// so the next message starts a new conversation anyway (BUG-109: invoking a hub that is not
    /// connected threw into the page and raised the Blazor error bar). Connecting only to reset would
    /// build a coordinator for an empty conversation.
    /// </summary>
    public async Task ResetSessionAsync(CancellationToken ct = default)
    {
        if (!_hub.IsConnected)
        {
            _logger.LogDebug("[MentorAgent.Blazor] Reset: no live hub connection — nothing to reset; " +
                             "the next message starts a new conversation.");
            return;
        }

        await _hub.ResetSessionAsync(ct);
    }

    /// <summary>
    /// Not available through this interface on WebAssembly: the <c>AgentSession</c> lives in the
    /// server's DI scope. The server hands it to a client over HTTP — <c>GET</c> and <c>POST</c>
    /// <c>/mentor/session?connectionId=</c> with <see cref="MentorHubClient.ConnectionId"/>, described in
    /// the Novior.MentorAgent.Blazor README — as an opaque snapshot bound to the signed-in user.
    /// <para>
    /// This throws rather than returning <see langword="null"/> on purpose. Null is the documented
    /// answer for "no conversation yet", so returning it here would tell a WebAssembly host that it
    /// had nothing worth saving — and it would dutifully store nothing, every time, and never find
    /// out why the restore did nothing either. That is the shape of defect this suite exists to
    /// find; an exception at development time is the honest alternative.
    /// </para>
    /// </summary>
    public Task<JsonElement?> SerializeSessionAsync(CancellationToken ct = default)
        => throw new NotSupportedException(SessionOverHttp);

    /// <inheritdoc cref="SerializeSessionAsync"/>
    public Task RestoreSessionAsync(JsonElement snapshot, CancellationToken ct = default)
        => throw new NotSupportedException(SessionOverHttp);

    private const string SessionOverHttp =
        "Session snapshots are not available through IMentorOrchestrator on WebAssembly: the AgentSession " +
        "lives in the server's DI scope. Save and restore it over HTTP instead — GET and POST " +
        "/mentor/session?connectionId=<MentorHubClient.ConnectionId> (see the Novior.MentorAgent.Blazor README).";

    /// <summary>
    /// Answers a pending HITL confirmation. Over HTTP, for the same reason as
    /// <see cref="CancelCurrentRequest"/>: the turn that raised the confirmation is parked inside
    /// <c>SendMessage</c>, and SignalR dispatches one invocation at a time per connection — so a
    /// <c>RespondToApproval</c> hub call would queue behind the very turn it is meant to release
    /// and never run. The server README marks that hub method "do not use" for exactly this.
    ///
    /// <para>
    /// The built-in widget answers through <c>IMentorStateService</c>, which already took the HTTP
    /// path; this method exists for hosts that drive approval from their own UI, and it must behave
    /// the same on WebAssembly as it does on Blazor Server.
    /// </para>
    /// </summary>
    public async Task RespondToApprovalAsync(ConfirmationRequest req, bool approved, CancellationToken ct = default)
    {
        var url = $"/mentor/approve?actionId={req.ActionId}&approved={(approved ? "true" : "false")}";
        try
        {
            var response = await MentorServerRequests.PostAsync(_http, _options, url, ct);
            // 404: nothing waits for it any more — answered, stopped, or its turn ended (BUG-128).
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                _logger.LogDebug("[MentorAgent.Blazor] Approval reached nothing waiting, ActionId={Id}.", req.ActionId);
            else if (!response.IsSuccessStatusCode)
                _logger.LogWarning(
                    "[MentorAgent.Blazor] Approval failed — HTTP {Status}, ActionId={Id}.",
                    (int)response.StatusCode, req.ActionId);
        }
        catch (Exception ex)
        {
            // Swallowed deliberately: the turn is parked on the server and will be released by its
            // own timeout. Throwing here would surface as an unhandled exception in the host's
            // click handler while leaving the conversation in the same state either way.
            _logger.LogError(ex, "[MentorAgent.Blazor] Approval request failed. ActionId={Id}", req.ActionId);
        }
    }

    public async ValueTask DisposeAsync()
        => await _hub.DisposeAsync();
}
