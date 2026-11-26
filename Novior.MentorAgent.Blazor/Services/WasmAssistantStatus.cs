using Novior.MentorAgent.Blazor.Hub;

namespace Novior.MentorAgent.Blazor.Services;

/// <summary>
/// Story 1290 — whether the server's application has its assistant on, for the widget of a WebAssembly client: read
/// with the hub's <c>GetAssistantStatus</c> at every connection, then followed through <c>AssistantStatus</c>, which
/// the server sends only when the value changes. On until the server says otherwise, and with a server older than
/// these messages.
/// </summary>
internal sealed class WasmAssistantStatus : IMentorAssistantStatus, IDisposable
{
    private readonly MentorHubClient _hub;
    private readonly ILogger<WasmAssistantStatus> _logger;
    private readonly IDisposable _subscription;
    private readonly object _gate = new();
    private volatile bool _on = true;
    private EventHandler? _changed;

    public WasmAssistantStatus(MentorHubClient hub, ILogger<WasmAssistantStatus> logger)
    {
        _hub = hub;
        _logger = logger;
        _subscription = hub.On<bool>("AssistantStatus", Set);
        hub.Opened += OnOpened;

        // The widget resolves the service when it renders, before anyone writes: the value is read then, on the connection
        // opened for it if none is open yet.
        if (hub.IsConnected) OnOpened();
        else hub.StartEarly();
    }

    public bool IsOn => _on;

    public event EventHandler? Changed
    {
        add { lock (_gate) _changed += value; }
        remove { lock (_gate) _changed -= value; }
    }

    /// <summary>The last read of the value from the hub, for the tests.</summary>
    internal Task LastRead { get; private set; } = Task.CompletedTask;

    private void OnOpened() => LastRead = ReadAsync();

    /// <summary>
    /// Whether the assistant is on, once the value of the connection just opened is read: waited for up to
    /// <paramref name="limit"/>, then as it is, so a message is never held by a read that does not end.
    /// </summary>
    internal async Task<bool> ReadIsOnAsync(TimeSpan limit, CancellationToken ct)
    {
        try
        {
            await LastRead.WaitAsync(limit, ct);
        }
        catch (TimeoutException)
        {
            _logger.LogDebug("[MentorAgent.Blazor] The server did not say in time whether the assistant is on.");
        }
        return _on;
    }

    private async Task ReadAsync()
    {
        try
        {
            Set(await _hub.GetAssistantStatusAsync());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A server older than GetAssistantStatus, or a connection gone meanwhile: the value stays as it was.
            _logger.LogDebug("[MentorAgent.Blazor] The server did not say whether the assistant is on: {Type}.", ex.GetType().Name);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Set(bool on)
    {
        EventHandler? handlers;
        lock (_gate)
        {
            if (on == _on) return;
            _on = on;
            handlers = _changed;
        }
        if (handlers is null) return;

        // One handler that throws does not stop the others.
        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler>())
        {
            try { handler(this, EventArgs.Empty); }
            catch (Exception ex)
            {
                _logger.LogWarning("[MentorAgent.Blazor] A handler of the assistant's status failed: {Type}.", ex.GetType().Name);
            }
        }
    }

    public void Dispose()
    {
        _hub.Opened -= OnOpened;
        _subscription.Dispose();
    }
}
