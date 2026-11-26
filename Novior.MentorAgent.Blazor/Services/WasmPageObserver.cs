using Novior.MentorAgent.Blazor.Hub;

namespace Novior.MentorAgent.Blazor.Services;

/// <summary>
/// Story 1640 — what the widget observes of the pages in a WebAssembly client, sent to the hub
/// (<c>ReportObservedPage</c>). The client cannot read the server's <c>ObservePages</c>: it observes, and the server's
/// first answer stops it when the observation is off there (<c>page.observe-off</c>). A server older than the method,
/// or one that fails to take the page, stops it too, with a line in the log, never the connection.
/// </summary>
/// <remarks>
/// The widget opens the hub only when it needs it, usually at the first message, and the observation starts with the
/// page: until the hub is connected and knows the client's pages (<see cref="MentorHubClient.ReadyToObserve"/>), the
/// reports wait here, in the browser, at most <see cref="MaxQueued"/> of them, the oldest dropped first. They leave in
/// order when the hub is ready; no connection is opened for them.
/// </remarks>
internal sealed class WasmPageObserver : IMentorPageObserver, IDisposable
{
    /// <summary>The reports kept while the hub is not ready: as many as the browser sends in a page's life.</summary>
    internal const int MaxQueued = 200;

    private readonly Func<bool> _ready;
    private readonly Func<string?, IReadOnlyList<PageObservedElement?>?, string?, Task<bool>> _send;
    private readonly Action? _unsubscribe;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly Queue<(string? Path, IReadOnlyList<PageObservedElement?>? Elements, string? Limit)> _queue = new();
    private readonly SemaphoreSlim _sending = new(1, 1);
    private bool _refused;
    private bool _droppedLogged;

    public WasmPageObserver(MentorHubClient hub, ILogger<WasmPageObserver> logger)
        : this(() => hub.ReadyToObserve, hub.ReportObservedPageAsync, logger, ready =>
        {
            hub.ReadyToObserveChanged += ready;
            return () => hub.ReadyToObserveChanged -= ready;
        })
    {
    }

    /// <param name="ready">Whether the hub is connected and knows the client's pages.</param>
    /// <param name="send">Sends one report; the server's answer.</param>
    /// <param name="logger">The log.</param>
    /// <param name="subscribe">Called with what to run when the hub becomes ready; returns how to stop it.</param>
    internal WasmPageObserver(Func<bool> ready, Func<string?, IReadOnlyList<PageObservedElement?>?, string?, Task<bool>> send,
        ILogger logger, Func<Action, Action>? subscribe = null)
    {
        _ready = ready;
        _send = send;
        _logger = logger;
        _unsubscribe = subscribe?.Invoke(() => _ = FlushAsync());
    }

    public bool IsOn => true;

    public async Task<bool> ReportAsync(string? path, IReadOnlyList<PageObservedElement?>? elements, string? limit)
    {
        bool waits;
        lock (_gate)
        {
            // A report that waited was refused: this answer stops the browser, as the server's would have.
            if (_refused)
            {
                _refused = false;
                return false;
            }

            // Not ready, or older reports still waiting (they leave first): this one waits behind them.
            waits = !_ready() || _queue.Count > 0;
            if (waits)
            {
                _queue.Enqueue((path, elements, limit));
                if (_queue.Count > MaxQueued)
                {
                    _queue.Dequeue();
                    if (!_droppedLogged)
                    {
                        _droppedLogged = true;
                        _logger.LogDebug("[MentorAgent.Blazor] page.observe: more than {Max} observed page(s) waited for the hub; the oldest are dropped.", MaxQueued);
                    }
                }
            }
        }

        if (waits)
        {
            // The hub may have become ready while this one was queued, after its signal.
            if (_ready()) _ = FlushAsync();
            return true;
        }

        await _sending.WaitAsync().ConfigureAwait(false);
        try
        {
            return await SendAsync(path, elements, limit).ConfigureAwait(false);
        }
        finally
        {
            _sending.Release();
        }
    }

    /// <summary>Sends the reports that waited, in order, while the hub stays ready; a refusal drops the rest.</summary>
    private async Task FlushAsync()
    {
        await _sending.WaitAsync().ConfigureAwait(false);
        try
        {
            while (true)
            {
                (string? Path, IReadOnlyList<PageObservedElement?>? Elements, string? Limit) report;
                lock (_gate)
                {
                    if (_queue.Count == 0 || !_ready()) return;
                    report = _queue.Dequeue();
                }

                if (!await SendAsync(report.Path, report.Elements, report.Limit).ConfigureAwait(false))
                {
                    lock (_gate)
                    {
                        _queue.Clear();
                        _refused = true;
                    }
                    return;
                }
            }
        }
        finally
        {
            _sending.Release();
        }
    }

    private async Task<bool> SendAsync(string? path, IReadOnlyList<PageObservedElement?>? elements, string? limit)
    {
        try
        {
            return await _send(path, elements, limit).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug("[MentorAgent.Blazor] page.observe: the server did not take the observed page ({Type}); the observation stops.",
                ex.GetType().Name);
            return false;
        }
    }

    public void Dispose() => _unsubscribe?.Invoke();
}
