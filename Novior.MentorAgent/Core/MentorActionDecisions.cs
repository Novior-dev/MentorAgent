using System.Threading.Channels;

namespace Novior.MentorAgent.Core;

/// <summary>
/// Hands the tool gate's decisions to every <see cref="IMentorActionObserver"/> of the process, off the
/// action's path.
///
/// <para>
/// Each observer has a queue and a reader of its own. <see cref="Publish"/> only writes to the queues and
/// never waits, so an observer that is slow, hangs or throws delays no action and no other observer: with
/// one reader for all of them, an observer that hung made every other one wait 5 seconds per decision, and
/// the queue filled up for all. The order holds per observer. A call that takes longer than the timeout is
/// no longer awaited, and the next decision may reach the observer while it still runs.
/// </para>
///
/// <para>
/// A full queue drops the decision for that observer only, and says so: a Warning when it starts dropping,
/// Debug while it keeps dropping, an Information line with the count when it catches up. A failing observer
/// is logged the same way — a Warning when it starts failing, Debug while it keeps failing — so an observer
/// that is down does not write one Warning per action.
/// </para>
/// </summary>
internal sealed class MentorActionDecisions : IDisposable, IAsyncDisposable
{
    internal const int DefaultCapacity = 10_000;
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly ILogger _logger;
    private readonly int _capacity;
    private readonly TimeSpan _timeout;
    private readonly Lazy<Subscriber[]> _subscribers;
    private int _closed;

    public MentorActionDecisions(IServiceProvider services, ILogger<MentorActionDecisions> logger, TimeProvider? time = null)
        : this(services, logger, time ?? TimeProvider.System, DefaultCapacity, DefaultTimeout)
    {
    }

    /// <summary>With the capacity and the timeout given: the tests' constructor.</summary>
    internal MentorActionDecisions(IServiceProvider services, ILogger logger, TimeProvider time, int capacity, TimeSpan timeout)
    {
        _logger   = logger;
        _capacity = capacity;
        _timeout  = timeout;
        Time      = time;

        // Read at the first decision, not at construction: an observer registered after AddMentorAgent() counts.
        _subscribers = new(
            () => [.. services.GetServices<IMentorActionObserver>().Select(o => new Subscriber(o, this))],
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The clock the decisions are stamped with.</summary>
    internal TimeProvider Time { get; }

    /// <summary>
    /// Whether anybody listens. The gate reads the user for an action that requires no role only then, so a
    /// host without observers pays nothing for them.
    /// </summary>
    internal bool HasObservers => Volatile.Read(ref _closed) == 0 && Subscribers.Length > 0;

    private Subscriber[] Subscribers
    {
        get
        {
            try { return _subscribers.Value; }
            catch (ObjectDisposedException) { return []; }   // the application is shutting down
        }
    }

    /// <summary>Queues <paramref name="decision"/> for every observer. Never throws and never waits.</summary>
    internal void Publish(MentorActionDecision decision)
    {
        if (Volatile.Read(ref _closed) == 1) return;
        foreach (var subscriber in Subscribers)
            subscriber.Post(decision);
    }

    /// <summary>Waits until every observer has received what was published: for the tests.</summary>
    internal async Task FlushAsync(TimeSpan? within = null)
    {
        var deadline = DateTime.UtcNow + (within ?? TimeSpan.FromSeconds(10));
        while (Subscribers.Any(s => s.Pending > 0))
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The action observers did not receive every decision in time.");
            await Task.Delay(10);
        }
    }

    public void Dispose() => Close();

    public async ValueTask DisposeAsync()
    {
        // The decisions already queued get a moment to arrive; a reader that is stuck is not waited for.
        try { await Task.WhenAll(Close()).WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (TimeoutException) { }
    }

    private Task[] Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1 || !_subscribers.IsValueCreated) return [];
        return [.. _subscribers.Value.Select(s => s.Complete())];
    }

    private sealed class Subscriber
    {
        private readonly IMentorActionObserver _observer;
        private readonly MentorActionDecisions _owner;
        private readonly Channel<MentorActionDecision> _queue;
        private readonly Task _reader;
        private int _pending;   // queued, or being delivered
        private int _failing;   // 1 from a failure to the next answer
        private int _dropping;  // 1 while the queue is full
        private int _dropped;   // dropped since the queue filled up

        internal Subscriber(IMentorActionObserver observer, MentorActionDecisions owner)
        {
            _observer = observer;
            _owner    = owner;
            _queue    = Channel.CreateBounded<MentorActionDecision>(new BoundedChannelOptions(owner._capacity)
            {
                SingleReader = true,
                FullMode     = BoundedChannelFullMode.Wait,   // TryWrite answers false: the decision is dropped
            });
            _reader   = Task.Run(ReadAsync);
        }

        internal int Pending => Volatile.Read(ref _pending);

        private string Name => _observer.GetType().FullName ?? _observer.GetType().Name;

        internal void Post(MentorActionDecision decision)
        {
            Interlocked.Increment(ref _pending);
            if (_queue.Writer.TryWrite(decision))
            {
                if (Volatile.Read(ref _dropping) == 1 && Interlocked.Exchange(ref _dropping, 0) == 1)
                    _owner._logger.LogInformation(
                        "[MentorAgent] Action observer {Observer} is receiving decisions again; {Count} were dropped while its queue was full.",
                        Name, Interlocked.Exchange(ref _dropped, 0));
                return;
            }

            Interlocked.Decrement(ref _pending);
            if (Volatile.Read(ref _owner._closed) == 1) return;

            Interlocked.Increment(ref _dropped);
            if (Interlocked.Exchange(ref _dropping, 1) == 0)
                _owner._logger.LogWarning(
                    "[MentorAgent] The queue of action observer {Observer} is full ({Capacity} decisions): decisions for it " +
                    "are dropped until it catches up. The actions are not affected.", Name, _owner._capacity);
            else
                _owner._logger.LogDebug("[MentorAgent] The queue of action observer {Observer} is still full: a decision dropped.", Name);
        }

        internal Task Complete()
        {
            _queue.Writer.TryComplete();
            return _reader;
        }

        private async Task ReadAsync()
        {
            await foreach (var decision in _queue.Reader.ReadAllAsync())
            {
                try { await DeliverAsync(decision); }
                finally { Interlocked.Decrement(ref _pending); }
            }
        }

        private async Task DeliverAsync(MentorActionDecision decision)
        {
            using var cts = new CancellationTokenSource(_owner._timeout, _owner.Time);
            try
            {
                await _observer.OnDecisionAsync(decision, cts.Token).AsTask().WaitAsync(_owner._timeout, _owner.Time);
                Volatile.Write(ref _failing, 0);
            }
            catch (TimeoutException)
            {
                Fault(DidNotAnswer, null);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                Fault(DidNotAnswer, null);
            }
            catch (Exception ex)
            {
                // The type only: the observer is the host's code, and its message can carry anything (BUG-148).
                Fault($"threw ({ex.GetType().Name})", ex);
            }
        }

        private string DidNotAnswer => "did not answer within "
            + _owner._timeout.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " s";

        private void Fault(string what, Exception? ex)
        {
            if (Interlocked.Exchange(ref _failing, 1) == 0)
                _owner._logger.LogWarning(
                    "[MentorAgent] Action observer {Observer} {What} — the action was not affected. Until it answers " +
                    "again, its failures are logged at Debug.", Name, what);
            else
                _owner._logger.LogDebug("[MentorAgent] Action observer {Observer} {What} again.", Name, what);

            if (ex is not null)
                _owner._logger.LogDebug(ex, "[MentorAgent] Action observer {Observer} threw.", Name);
        }
    }
}
