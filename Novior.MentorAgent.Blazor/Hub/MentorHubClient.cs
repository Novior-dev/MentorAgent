using Microsoft.AspNetCore.Components;

namespace Novior.MentorAgent.Blazor.Hub;

/// <summary>
/// Typed wrapper around <see cref="HubConnection"/> for the Novior.MentorAgent.Server hub.
/// Manages connection lifecycle with automatic exponential-backoff reconnect.
/// </summary>
public sealed class MentorHubClient : IAsyncDisposable
{
    private readonly HubConnection _connection;
    private readonly ILogger<MentorHubClient> _logger;

    public bool IsConnected => _connection.State == HubConnectionState.Connected;

    /// <summary>
    /// Id of the live SignalR connection, or <see langword="null"/> when disconnected.
    /// Needed by the out-of-band HTTP endpoints (<c>/mentor/cancel</c>) that must reach this
    /// connection's orchestrator without going through the hub.
    /// </summary>
    public string? ConnectionId => _connection.ConnectionId;

    /// <summary>
    /// Connects to <see cref="MentorAgentBlazorOptions.HubUrl"/> as written. A relative
    /// <c>HubUrl</c> is not resolved here: use the constructor that takes the
    /// <see cref="NavigationManager"/>, which <c>AddMentorAgentBlazor()</c> picks in WebAssembly.
    /// </summary>
    public MentorHubClient(MentorAgentBlazorOptions options, ILogger<MentorHubClient> logger)
        : this(options, baseUri: null, logger)
    {
    }

    /// <summary>
    /// Connects to <see cref="MentorAgentBlazorOptions.HubUrl"/>, resolving a relative value such as
    /// the default <c>"/mentor-hub"</c> against the page's base address, as a browser does.
    /// </summary>
    public MentorHubClient(MentorAgentBlazorOptions options, NavigationManager navigation, ILogger<MentorHubClient> logger)
        : this(options, navigation.BaseUri, logger)
    {
    }

    private MentorHubClient(MentorAgentBlazorOptions options, string? baseUri, ILogger<MentorHubClient> logger)
    {
        _logger = logger;

        // BUG-145 — the .NET client turns the URL into an absolute Uri as written, and in the browser
        // a bare path parses as file:///mentor-hub: the default HubUrl never reached the server.
        var hubUrl = ResolveHubUrl(options.HubUrl, baseUri);

        // BUG-046 — the connection has to be able to carry credentials. Without this the server's
        // identity bridge reads HubCallerContext.User and finds nothing, so every RequiredRoles
        // action is blocked and the user is keyed as anonymous: a key per connection by default
        // (AnonymousIdentity = PerSession), the one shared key "anonymous" with Shared.
        _connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, connection =>
            {
                if (options.AccessTokenProvider is { } accessTokenProvider)
                    connection.AccessTokenProvider = accessTokenProvider;

                options.ConfigureConnection?.Invoke(connection);
            })
            .WithAutomaticReconnect(new[] { TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10) })
            .Build();

        _connection.Closed += ex =>
        {
            _logger.LogWarning("[MentorAgent.Blazor] Hub connection closed. {Ex}", ex?.Message);
            return Task.CompletedTask;
        };
        _connection.Reconnected += connectionId =>
        {
            _logger.LogInformation("[MentorAgent.Blazor] Hub reconnected. ConnectionId: {Id}", connectionId);
            Opened?.Invoke();
            return ReportPagesAsync(CancellationToken.None);
        };
        // Story 1640 — a new connection knows the client's pages only once ReportPages has run again.
        _connection.Reconnecting += _ =>
        {
            _pagesReported = false;
            return Task.CompletedTask;
        };

        _pageAssemblies = options.PageAssemblies;
    }

    private readonly System.Reflection.Assembly[] _pageAssemblies;
    private IReadOnlyList<PageRouteInfo>? _pages;
    private volatile bool _pagesReported;

    /// <summary>
    /// Story 1640 — connected, and the client's pages sent on this connection (<c>ReportPages</c>): before that the
    /// server does not know the client's routes, and would leave out what the widget observed on them.
    /// </summary>
    internal bool ReadyToObserve => IsConnected && _pagesReported;

    /// <summary>
    /// Story 1640 — raised when <see cref="ReadyToObserve"/> becomes true, at every connection: what the widget observed
    /// before it waited in the browser, and leaves now.
    /// </summary>
    internal event Action? ReadyToObserveChanged;

    /// <summary>For the tests: runs before the client's pages are sent, as a slow first read of them would.</summary>
    internal Func<Task>? BeforeReportPages { get; init; }

    /// <summary>
    /// Story 1635 — the client's pages, read once from its code (<see cref="MentorPageRoutes"/>): the server cannot
    /// read a WebAssembly client's components. Structure only: a route and the component that serves it.
    /// </summary>
    internal IReadOnlyList<PageRouteInfo> Pages()
    {
        if (_pages is not null) return _pages;

        var skipped = new List<string>();
        var assemblies = MentorPageRoutes.Assemblies(System.Reflection.Assembly.GetEntryAssembly(), _pageAssemblies, skipped);
        _pages = [.. MentorPageRoutes.Read(assemblies, skipped)
            .Take(MentorPageRoutes.MaxPages)
            .Select(r => new PageRouteInfo { Route = r.Template, Component = r.Component })];
        if (skipped.Count > 0)
            _logger.LogWarning("[MentorAgent.Blazor] page.read: {Count} assembly(ies) or type(s) could not be read for their pages and were skipped: {Skipped}.",
                skipped.Count, string.Join(", ", skipped));
        return _pages;
    }

    /// <summary>
    /// Sends the server the client's pages, at every connection: a server that restarted has forgotten them. A server
    /// older than <c>ReportPages</c>, or one that fails to take them, costs a line in the log, never the connection.
    /// </summary>
    private async Task ReportPagesAsync(CancellationToken ct)
    {
        try
        {
            if (BeforeReportPages is { } before) await before();
            var pages = Pages();
            if (pages.Count > 0)
                await _connection.InvokeAsync("ReportPages", pages, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug("[MentorAgent.Blazor] The server did not take the client's pages: {Message}", ex.Message);
        }
        finally
        {
            // Taken or not, the observation may go on: a server that did not take the pages finds none of the paths.
            _pagesReported = true;
            ReadyToObserveChanged?.Invoke();
        }
    }

    /// <summary>
    /// Story 1640 — what the widget observed of the page at <paramref name="path"/>, to the hub's
    /// <c>ReportObservedPage</c>; the server answers whether to go on.
    /// </summary>
    internal Task<bool> ReportObservedPageAsync(string? path, IReadOnlyList<PageObservedElement?>? elements, string? limit)
        => _connection.InvokeAsync<bool>("ReportObservedPage", path, elements, limit);

    /// <summary>
    /// An absolute http(s) <paramref name="hubUrl"/> as written; a relative one against
    /// <paramref name="baseUri"/> ("/mentor-hub" against the origin, "mentor-hub" under the app's
    /// base path). Anything else, or no base, is left as it was. The scheme check matters: in
    /// WebAssembly, as on Linux, "/mentor-hub" parses as an absolute <c>file:///</c> URI.
    /// </summary>
    internal static string ResolveHubUrl(string hubUrl, string? baseUri)
    {
        if (string.IsNullOrWhiteSpace(hubUrl))
            return hubUrl;

        if (Uri.TryCreate(hubUrl, UriKind.Absolute, out var absolute)
            && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
            return hubUrl;

        if (Uri.TryCreate(baseUri, UriKind.Absolute, out var page)
            && (page.Scheme == Uri.UriSchemeHttp || page.Scheme == Uri.UriSchemeHttps)
            && Uri.TryCreate(hubUrl, UriKind.Relative, out var relative))
            return new Uri(page, relative).ToString();

        return hubUrl;
    }

    // ── Connection ─────────────────────────────────────────────────────────────

    private readonly object _startGate = new();
    private Task? _starting;

    /// <summary>
    /// Opens the connection, or waits for the opening already under way: a caller that sends right after must find it
    /// open. An opening started early (<see cref="StartEarly"/>) that failed is tried again here.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        Task? inFlight;
        lock (_startGate) inFlight = _starting is { IsCompleted: false } running ? running : null;
        if (inFlight is not null)
        {
            try { await inFlight.WaitAsync(ct); }
            catch (Exception) when (!ct.IsCancellationRequested) { /* tried again below */ }
        }

        Task mine;
        lock (_startGate)
        {
            if (_connection.State != HubConnectionState.Disconnected) return;
            mine = _starting is { IsCompleted: false } other ? other : _starting = StartCoreAsync(quiet: false, ct);
        }
        await mine;
    }

    /// <summary>
    /// Story 1290 — opens the connection before anyone writes, so the widget knows at once whether the assistant is on.
    /// A server that does not answer costs a line in the log: the first message tries again.
    /// </summary>
    internal void StartEarly()
    {
        lock (_startGate)
        {
            if (_connection.State != HubConnectionState.Disconnected || _starting is { IsCompleted: false }) return;
            _starting = StartCoreAsync(quiet: true, CancellationToken.None);
            _starting.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
    }

    private async Task StartCoreAsync(bool quiet, CancellationToken ct)
    {
        try
        {
            await _connection.StartAsync(ct);
            _logger.LogInformation("[MentorAgent.Blazor] Connected to hub.");
        }
        catch (Exception ex)
        {
            if (quiet)
                _logger.LogInformation("[MentorAgent.Blazor] The hub did not answer yet ({Type}): the first message tries again.",
                    ex.GetType().Name);
            else
                _logger.LogError(ex, "[MentorAgent.Blazor] Failed to connect to hub.");
            throw;
        }

        Opened?.Invoke();
        await ReportPagesAsync(ct);
    }

    /// <summary>
    /// Story 1290 — raised when a connection opens, the first one and every reconnection: a value the server sends
    /// only when it changes has to be read again on a new connection.
    /// </summary>
    internal event Action? Opened;

    /// <summary>Story 1290 — whether the application's assistant is on, from the hub's <c>GetAssistantStatus</c>.</summary>
    internal Task<bool> GetAssistantStatusAsync(CancellationToken ct = default)
        => _connection.InvokeAsync<bool>("GetAssistantStatus", ct);

    // ── Client → Server methods ───────────────────────────────────────────────

    public Task UpdatePageContextAsync(PageContextSnapshot snapshot, CancellationToken ct = default)
        => _connection.InvokeAsync("UpdatePageContext", snapshot, ct);

    public Task SendMessageAsync(string text, CancellationToken ct = default)
        => _connection.InvokeAsync("SendMessage", text, null, ct);

    // #13 — send the message together with image attachments (the hub method has a nullable
    // trailing attachments parameter, so older 1-arg callers still bind).
    public Task SendMessageAsync(string text, IReadOnlyList<MentorAttachment>? attachments, CancellationToken ct = default)
        => _connection.InvokeAsync("SendMessage", text, attachments, ct);

    /// <summary>Sends a user turn with its images and its language, to the hub's <c>SendUserMessage</c>.</summary>
    public Task SendUserMessageAsync(MentorUserMessage message, CancellationToken ct = default)
        => _connection.InvokeAsync("SendUserMessage", message, ct);

    public Task CancelRequestAsync(CancellationToken ct = default)
        => _connection.InvokeAsync("CancelRequest", ct);

    public Task ResetSessionAsync(CancellationToken ct = default)
        => _connection.InvokeAsync("ResetSession", ct);

    public Task RespondToApprovalAsync(string requestId, bool approved, CancellationToken ct = default)
        => _connection.InvokeAsync("RespondToApproval", requestId, approved, ct);

    // ── Server → Client subscriptions ────────────────────────────────────────

    public IDisposable On<T>(string methodName, Action<T> handler)
        => _connection.On(methodName, handler);

    public IDisposable On(string methodName, Action handler)
        => _connection.On(methodName, handler);

    public IDisposable On<T1, T2>(string methodName, Action<T1, T2> handler)
        => _connection.On(methodName, handler);

    public IDisposable On<T1, T2, T3>(string methodName, Action<T1, T2, T3> handler)
        => _connection.On(methodName, handler);

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
    }
}
