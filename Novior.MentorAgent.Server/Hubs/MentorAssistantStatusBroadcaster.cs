using Microsoft.Extensions.Hosting;

namespace Novior.MentorAgent.Server.Hubs;

/// <summary>
/// Tells every hub connection when the application's assistant is turned off or on (<c>AssistantStatus</c>), so the
/// WebAssembly widget and <c>/mentor/agent.js</c> follow the switch as the in-process widget does. Does nothing without
/// an <see cref="IMentorAssistantStatus"/>. A client reads the value when it connects with <c>GetAssistantStatus</c>;
/// this sends only the changes.
/// </summary>
/// <remarks>
/// The handler of <see cref="IMentorAssistantStatus.Changed"/> never throws and never waits: the send runs after it,
/// in the order of the changes, and a failure costs a line in the log with the type of the exception only.
/// </remarks>
internal sealed class MentorAssistantStatusBroadcaster(
    IServiceProvider services,
    IHubContext<MentorHub> hub,
    ILogger<MentorAssistantStatusBroadcaster> logger) : IHostedService
{
    internal const string Event = "AssistantStatus";

    private readonly object _gate = new();
    private IMentorAssistantStatus? _status;
    private bool _last = true;
    private Task _sends = Task.CompletedTask;

    /// <summary>The sends started so far, for the tests.</summary>
    internal Task Sends { get { lock (_gate) return _sends; } }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _status = services.GetService<IMentorAssistantStatus>();
        if (_status is null) return Task.CompletedTask;

        try { _last = _status.IsOn; }
        catch (Exception ex) { logger.LogWarning("[MentorAgent] The assistant's status could not be read: {Type}.", ex.GetType().Name); }
        _status.Changed += OnChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_status is not null) _status.Changed -= OnChanged;
        return Task.CompletedTask;
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        try
        {
            var on = _status!.IsOn;
            lock (_gate)
            {
                // Only the changes: a service that raises Changed with the same value sends nothing.
                if (on == _last) return;
                _last = on;
                _sends = _sends.ContinueWith(_ => SendAsync(on), TaskScheduler.Default).Unwrap();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning("[MentorAgent] The assistant's status could not be read: {Type}.", ex.GetType().Name);
        }
    }

    private async Task SendAsync(bool on)
    {
        try
        {
            await hub.Clients.All.SendAsync(Event, on);
        }
        catch (Exception ex)
        {
            logger.LogWarning("[MentorAgent] The assistant's status did not reach the hub's connections: {Type}.", ex.GetType().Name);
        }
    }
}
