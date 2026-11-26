using Novior.MentorAgent.Abstractions.Interfaces;

namespace Novior.MentorAgent.Tests.Fakes;

/// <summary>
/// An application's switch of its assistant (<see cref="IMentorAssistantStatus"/>), as the bridge's adapter is:
/// <see cref="Set"/> raises <see cref="Changed"/> only at a change, with every handler protected from the others.
/// <see cref="Raise"/> raises it without a change, as a service that breaks the rule would.
/// </summary>
internal sealed class TestAssistantSwitch(bool on = true) : IMentorAssistantStatus
{
    private readonly object _gate = new();
    private EventHandler? _changed;
    private volatile bool _on = on;

    /// <summary>When set, reading <see cref="IsOn"/> throws it once.</summary>
    internal Exception? ThrowOnce { get; set; }

    /// <summary>When set, reading <see cref="IsOn"/> waits for it: a read that does not end.</summary>
    internal ManualResetEventSlim? Hold { get; set; }

    public bool IsOn
    {
        get
        {
            Hold?.Wait(TimeSpan.FromSeconds(30));
            if (ThrowOnce is { } ex)
            {
                ThrowOnce = null;
                throw ex;
            }
            return _on;
        }
    }

    /// <summary>The handlers that threw.</summary>
    internal int HandlerFailures { get; private set; }

    public int Handlers { get { lock (_gate) return _changed?.GetInvocationList().Length ?? 0; } }

    public event EventHandler? Changed
    {
        add { lock (_gate) _changed += value; }
        remove { lock (_gate) _changed -= value; }
    }

    internal void Set(bool on)
    {
        if (_on == on) return;
        _on = on;
        Raise();
    }

    internal void Raise()
    {
        EventHandler? handlers;
        lock (_gate) handlers = _changed;
        foreach (var handler in handlers?.GetInvocationList().Cast<EventHandler>() ?? [])
        {
            try { handler(this, EventArgs.Empty); }
            catch (Exception) { HandlerFailures++; /* one handler does not stop the others */ }
        }
    }
}
