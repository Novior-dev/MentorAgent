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
        // action is blocked and every user shares the memory key "anonymous".
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
            return Task.CompletedTask;
        };
    }

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

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_connection.State != HubConnectionState.Disconnected) return;
        try
        {
            await _connection.StartAsync(ct);
            _logger.LogInformation("[MentorAgent.Blazor] Connected to hub.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MentorAgent.Blazor] Failed to connect to hub.");
            throw;
        }
    }

    // ── Client → Server methods ───────────────────────────────────────────────

    public Task UpdatePageContextAsync(PageContextSnapshot snapshot, CancellationToken ct = default)
        => _connection.InvokeAsync("UpdatePageContext", snapshot, ct);

    public Task SendMessageAsync(string text, CancellationToken ct = default)
        => _connection.InvokeAsync("SendMessage", text, null, ct);

    // #13 — send the message together with image attachments (the hub method has a nullable
    // trailing attachments parameter, so older 1-arg callers still bind).
    public Task SendMessageAsync(string text, IReadOnlyList<MentorAttachment>? attachments, CancellationToken ct = default)
        => _connection.InvokeAsync("SendMessage", text, attachments, ct);

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
