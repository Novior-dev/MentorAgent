using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Server.Auth;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Novior.MentorAgent.Server.Hubs;

/// <summary>
/// SignalR hub that exposes <see cref="MentorOrchestrator"/> to remote clients
/// (Blazor WASM, MAUI, React, Vue, mobile, console, etc.).
///
/// Each connection gets its own DI scope via <see cref="MentorHubScopes"/> — built once on
/// connect and reused for every message. This mirrors the Blazor Server "one scope per circuit"
/// model, so the coordinator (and its MCP connections) is built once and the conversation
/// <c>AgentSession</c> persists across messages.
///
/// Protocol:
///   Client → Server: UpdatePageContext, ReportPages, ReportObservedPage, GetAssistantStatus, SendMessage,
///                    CancelRequest, ResetSession, RespondToApproval
///   Server → Client: StreamingChunk, StreamingCompleted, BusyChanged, Error, ActionExecuting,
///                    ActionCompleted, ActionFailed, ConfirmationRequired, NavigationRequested,
///                    RagSourcesReady, TeamMemberSpeaking, UIActionExecuting, UIActionCompleted,
///                    UIActionRequested, McpServerStatusChanged, GeneratedImages, HostedToolsDeclared,
///                    Cards, AssistantStatus (to every connection, only when the assistant is turned off or on)
///   Over HTTP, beside the hub: POST /mentor/approve, POST /mentor/cancel, GET and POST /mentor/session
///   (a hub call would queue behind the turn it is meant to reach)
/// </summary>
public class MentorHub : Hub
{
    private readonly MentorHubScopes     _scopes;
    private readonly HubPageContextStore _pageContextStore;
    private readonly MentorWidgetOptions _widgetOptions;
    private readonly ILogger<MentorHub>  _logger;

    private readonly MentorApprovalOwners _approvalOwners;

    public MentorHub(
        MentorHubScopes scopes,
        HubPageContextStore pageContextStore,
        MentorApprovalOwners approvalOwners,
        IOptions<MentorWidgetOptions> widgetOptions,
        ILogger<MentorHub> logger)
    {
        _scopes           = scopes;
        _pageContextStore = pageContextStore;
        _approvalOwners   = approvalOwners;
        _widgetOptions    = widgetOptions.Value;
        _logger           = logger;
    }

    public override async Task OnConnectedAsync()
    {
        // Create the per-connection scope up front and wire the state decorator so events
        // can be forwarded from the very first message.
        BindScope();

        // Hosted tools (#15) are a server-side capability: a detached client cannot know them from its
        // own configuration, and a hand-mirrored list drifts silently — the badge then claims tools the
        // server dropped, or hides ones it gained. Sent as the raw flags so it costs one small message.
        // Purely additive: a client that does not subscribe behaves exactly as before.
        await Clients.Caller.SendAsync("HostedToolsDeclared", (int)_widgetOptions.HostedTools);

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _pageContextStore.Remove(Context.ConnectionId);

        // Scope first, owners second (BUG-128). Disposing the orchestrator cancels a turn parked on a
        // confirmation, and the owner record is what makes POST /mentor/approve check the caller: forgot
        // first, the parked turn could be released by anyone who knew the action id.
        await _scopes.RemoveAsync(Context.ConnectionId);   // disposes the orchestrator → cancels its turn, closes MCP once
        _approvalOwners.ForgetConnection(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Resolves this connection's per-scope services and syncs the connection id + authenticated
    /// principal into them. Returns the orchestrator and the state decorator for the caller.
    /// </summary>
    private (IMentorOrchestrator Orchestrator, HubStateServiceDecorator Decorator) BindScope()
    {
        var sp = _scopes.GetOrCreate(Context.ConnectionId);

        var decorator = sp.GetRequiredService<HubStateServiceDecorator>();
        decorator.SetConnectionId(Context.ConnectionId);
        sp.GetService<HubRecipeRunner>()?.Bind(Context.ConnectionId);   // story 1638: before the orchestrator reads it
        sp.GetRequiredService<MentorServerUserContext>().User = Context.User;
        sp.GetRequiredService<MentorTransportUser>().User = Context.User;   // read before the host's provider (BUG-131)
        sp.GetRequiredService<MentorTurnContext>().Surface = MentorActionSurface.Hub;   // story 180

        return (sp.GetRequiredService<IMentorOrchestrator>(), decorator);
    }

    // ── Client → Server ───────────────────────────────────────────────────────

    /// <summary>Stores the page context snapshot for this connection.</summary>
    public Task UpdatePageContext(PageContextSnapshot snapshot)
    {
        BindScope();
        _pageContextStore.Store(Context.ConnectionId, snapshot);

        // Story 1288, slice C — the snapshot reaches the page context only when a turn starts; the catalogue's trace of
        // the pages lists the page when the client declares it. Names only: the trace never keeps a description.
        if (_scopes.GetOrCreate(Context.ConnectionId).GetService<Novior.MentorAgent.Core.MentorPageActionTrace>() is { } trace)
            foreach (var action in snapshot.UIActions ?? [])
                trace.Record(snapshot.PageName, action?.Name);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Story 1635 — the pages of a WebAssembly client, as its router knows them, sent once a connection opens: the
    /// server cannot read the client's code. Listed for the AutoMentor when it is on; ignored otherwise. Structure
    /// only, and checked like everything the browser sends (<c>page.route-shape</c>, <c>page.route-limit</c>).
    /// </summary>
    public Task ReportPages(List<PageRouteInfo>? pages)
    {
        _scopes.GetOrCreate(Context.ConnectionId).GetService<MentorPageDirectory>()?.Report(pages);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Whether the application's assistant is on (<see cref="IMentorAssistantStatus"/>), for a client that has just
    /// connected: the changes come after as <c>AssistantStatus</c>. <see langword="true"/> when the application
    /// registers none.
    /// </summary>
    public Task<bool> GetAssistantStatus()
        => Task.FromResult(_scopes.GetOrCreate(Context.ConnectionId).GetService<IMentorAssistantStatus>()?.IsOn ?? true);

    private const string ObserverKey = "Novior.MentorAgent.ObservedPages";

    /// <summary>
    /// Story 1640 — what the widget of a WebAssembly client observed of the page at <paramref name="path"/>
    /// (<c>page.observe</c>): kept as the application keeps its own observation, checked like everything the browser
    /// sends. The user is the connection's signed-in user, as a hash, or the connection itself; a connection sends at
    /// most <c>MaxObservedReports</c> messages. Answers whether to go on: false when the observation is off
    /// (<c>page.observe-off</c>) or past a limit (<c>page.observe-limit</c>).
    /// </summary>
    public Task<bool> ReportObservedPage(string? path, List<PageObservedElement?>? elements, string? limit)
    {
        if (_scopes.GetOrCreate(Context.ConnectionId).GetService<MentorPageDirectory>() is not { } directory)
            return Task.FromResult(false);

        if (Context.Items.TryGetValue(ObserverKey, out var kept) is false || kept is not ObservedBy observer)
            Context.Items[ObserverKey] = observer = new ObservedBy(MentorPageDirectory.ViewerOf(Context.User) ?? MentorPageDirectory.NewViewer());
        return Task.FromResult(directory.Observe(path, elements, observer.Viewer, ++observer.Reports, limit));
    }

    /// <summary>A connection's user for the observation, as a hash or a random number, and how many messages it sent.</summary>
    private sealed class ObservedBy(long viewer)
    {
        public long Viewer { get; } = viewer;
        public int Reports { get; set; }
    }

    /// <summary>
    /// Sends a user message to the AI orchestrator.
    ///
    /// <para>
    /// <b>Clients must always send two arguments</b> — <c>null</c> or an empty list when the turn
    /// carries no images. The C# default on <paramref name="attachments"/> is a convenience for
    /// server-side callers and <b>does not make the argument optional on the wire</b>: SignalR binds
    /// invocation arguments positionally by count in the hub protocol's parser, before dispatch and
    /// before any <c>IHubFilter</c>, so a one-argument invocation is rejected with
    /// <c>InvalidDataException: Invocation provides 1 argument(s) but target expects 2</c>. Hub
    /// methods cannot be overloaded, so no signature accepts both arities.
    /// </para>
    ///
    /// <para>
    /// This comment previously claimed the default kept 1-argument clients working. It did not, and
    /// the four reference clients in the package README were written against that claim (BUG-051).
    /// The wire arity is pinned by <c>Server/HubMethodArityTests</c>.
    /// </para>
    /// </summary>
    public async Task SendMessage(string text, IReadOnlyList<MentorAttachment>? attachments = null)
    {
        var (orchestrator, decorator) = BindScope();
        ApplyPage(decorator);
        await orchestrator.SendMessageAsync(text, attachments);
    }

    /// <summary>
    /// The page context the client sent last, applied before the turn; the path of its page to the recipes' runner
    /// (story 1638), which offers the recipes of that page. No path, no recipes: a client that does not send one cannot
    /// run them.
    /// </summary>
    private void ApplyPage(HubStateServiceDecorator decorator)
    {
        var snapshot = _pageContextStore.Get(Context.ConnectionId);
        if (snapshot is not null)
            decorator.ApplySnapshot(snapshot);
        _scopes.GetOrCreate(Context.ConnectionId).GetService<HubRecipeRunner>()?.Show(snapshot?.Path);
    }

    /// <summary>
    /// Sends a user turn: <c>{ "text": "…", "attachments": null, "language": "it" }</c>. The language is the
    /// name or the ISO code of a <c>MentorLanguage</c>, in any case; anything else, or none, is
    /// <c>MentorOptions.Language</c>. A method of its own, because <see cref="SendMessage"/> keeps its two
    /// arguments (BUG-051): one object, which can carry more later without changing the arity again.
    /// </summary>
    public async Task SendUserMessage(MentorUserMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var (orchestrator, decorator) = BindScope();
        ApplyPage(decorator);
        await orchestrator.SendMessageAsync(message);
    }

    /// <summary>Cancels the current AI request.</summary>
    public Task CancelRequest()
    {
        var (orchestrator, _) = BindScope();
        orchestrator.CancelCurrentRequest();
        return Task.CompletedTask;
    }

    /// <summary>Resets the conversation session.</summary>
    public async Task ResetSession()
    {
        var (orchestrator, _) = BindScope();
        await orchestrator.ResetSessionAsync();
    }

    /// <summary>
    /// Responds to a HITL confirmation dialog.
    ///
    /// ⚠️ Kept for backwards compatibility only — <b>use <c>POST /mentor/approve</c> instead</b>.
    /// SignalR dispatches at most one hub invocation at a time per connection, so while
    /// <c>SendMessage</c> is parked waiting for this answer the invocation sits in the queue and the
    /// approval never arrives. Works only if the turn is somehow no longer running.
    /// Applies to both <c>MentorHitlMode</c> values.
    /// </summary>
    public async Task RespondToApproval(string requestId, bool approved)
    {
        _logger.LogDebug("[MentorHub] RespondToApproval — requestId={Id}, approved={Approved}", requestId, approved);
        if (!Guid.TryParse(requestId, out var actionId))
        {
            _logger.LogWarning("[MentorHub] RespondToApproval — invalid Guid: '{Id}'", requestId);
            return;
        }

        var (orchestrator, _) = BindScope();
        var req = new ConfirmationRequest { ActionId = actionId, ToolName = string.Empty, Message = string.Empty };
        await orchestrator.RespondToApprovalAsync(req, approved);
    }
}
