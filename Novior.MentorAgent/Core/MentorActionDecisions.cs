using System.Threading.Channels;

namespace Novior.MentorAgent.Core;

/// <summary>
/// Hands the tool gate's decisions to every <see cref="IMentorActionObserver"/> of the process, off the
/// action's path.
///
/// <para>
/// Each observer has a queue and a reader of its own. <see cref="Publish(MentorActionDecision)"/> only writes to the queues and
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

    /// <summary>
    /// Story 1289 — whether an observer takes the calls to the models (<see cref="IMentorActionObserver.OnModelCallAsync"/>
    /// implemented): only then is a call's user read, so a host without one pays nothing for them.
    /// </summary>
    internal bool HasCallObservers => Volatile.Read(ref _closed) == 0 && Subscribers.Any(s => s.TakesCalls);

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
            subscriber.Decisions.Post(decision);
    }

    /// <summary>
    /// Story 1289 — queues <paramref name="call"/> for every observer that takes the calls to the models, on a queue of
    /// its own: a burst of calls never makes a decision drop. Never throws and never waits.
    /// </summary>
    internal void Publish(MentorModelCall call)
    {
        if (Volatile.Read(ref _closed) == 1) return;
        foreach (var subscriber in Subscribers)
            subscriber.Calls?.Post(call);
    }

    /// <summary>Waits until every observer has received what was published: for the tests.</summary>
    internal async Task FlushAsync(TimeSpan? within = null)
    {
        var deadline = DateTime.UtcNow + (within ?? TimeSpan.FromSeconds(10));
        while (Subscribers.Any(s => s.Decisions.Pending > 0 || s.Calls?.Pending > 0))
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
        return [.. _subscribers.Value.SelectMany(s => s.Complete())];
    }

    /// <summary>One observer: a queue for the decisions and, when it takes them, one for the calls to the models.</summary>
    private sealed class Subscriber
    {
        internal Subscriber(IMentorActionObserver observer, MentorActionDecisions owner)
        {
            var name = observer.GetType().FullName ?? observer.GetType().Name;
            Decisions = new Lane<MentorActionDecision>(owner, name, "decisions",
                (decision, ct) => observer.OnDecisionAsync(decision, ct));
            if (Implements(observer.GetType(), nameof(IMentorActionObserver.OnModelCallAsync)))
                Calls = new Lane<MentorModelCall>(owner, name, "model calls",
                    (call, ct) => observer.OnModelCallAsync(call, ct));
        }

        internal Lane<MentorActionDecision> Decisions { get; }

        /// <summary>None when the observer keeps the interface's default, which does nothing.</summary>
        internal Lane<MentorModelCall>? Calls { get; }

        internal bool TakesCalls => Calls is not null;

        internal IEnumerable<Task> Complete()
        {
            yield return Decisions.Complete();
            if (Calls is not null) yield return Calls.Complete();
        }

        /// <summary>Whether <paramref name="type"/> has a body of its own for the interface member, not the default.</summary>
        private static bool Implements(Type type, string member)
        {
            var map = type.GetInterfaceMap(typeof(IMentorActionObserver));
            for (var i = 0; i < map.InterfaceMethods.Length; i++)
                if (map.InterfaceMethods[i].Name == member)
                    return map.TargetMethods[i].DeclaringType != typeof(IMentorActionObserver);
            return false;
        }
    }

    /// <summary>
    /// One queue of an observer and its reader. A full queue drops for this observer and this queue only, and says so:
    /// a Warning when it starts dropping, Debug while it keeps dropping, an Information line with the count when it
    /// catches up. A failing observer is logged the same way, so an observer that is down does not write one Warning
    /// per item.
    /// </summary>
    private sealed class Lane<T>
    {
        private readonly MentorActionDecisions _owner;
        private readonly string _name;
        private readonly string _items;
        private readonly string _on;   // what a failure was on, said for the calls only: a decision's line stays as it was
        private readonly Func<T, CancellationToken, ValueTask> _deliver;
        private readonly Channel<T> _queue;
        private readonly Task _reader;
        private int _pending;   // queued, or being delivered
        private int _failing;   // 1 from a failure to the next answer
        private int _dropping;  // 1 while the queue is full
        private int _dropped;   // dropped since the queue filled up

        internal Lane(MentorActionDecisions owner, string name, string items, Func<T, CancellationToken, ValueTask> deliver)
        {
            _owner   = owner;
            _name    = name;
            _items   = items;
            _on      = items == "decisions" ? "" : " on " + items;
            _deliver = deliver;
            _queue   = Channel.CreateBounded<T>(new BoundedChannelOptions(owner._capacity)
            {
                SingleReader = true,
                FullMode     = BoundedChannelFullMode.Wait,   // TryWrite answers false: the item is dropped
            });
            _reader  = Task.Run(ReadAsync);
        }

        internal int Pending => Volatile.Read(ref _pending);

        internal void Post(T item)
        {
            Interlocked.Increment(ref _pending);
            if (_queue.Writer.TryWrite(item))
            {
                if (Volatile.Read(ref _dropping) == 1 && Interlocked.Exchange(ref _dropping, 0) == 1)
                    _owner._logger.LogInformation(
                        "[MentorAgent] Action observer {Observer} is receiving {Items} again; {Count} were dropped while its queue was full.",
                        _name, _items, Interlocked.Exchange(ref _dropped, 0));
                return;
            }

            Interlocked.Decrement(ref _pending);
            if (Volatile.Read(ref _owner._closed) == 1) return;

            Interlocked.Increment(ref _dropped);
            if (Interlocked.Exchange(ref _dropping, 1) == 0)
                _owner._logger.LogWarning(
                    "[MentorAgent] The queue of action observer {Observer} is full ({Capacity} {Items}): {Items} for it " +
                    "are dropped until it catches up. The actions are not affected.", _name, _owner._capacity, _items, _items);
            else
                _owner._logger.LogDebug("[MentorAgent] The queue of action observer {Observer} is still full: one of its {Items} dropped.",
                    _name, _items);
        }

        internal Task Complete()
        {
            _queue.Writer.TryComplete();
            return _reader;
        }

        private async Task ReadAsync()
        {
            await foreach (var item in _queue.Reader.ReadAllAsync())
            {
                try { await DeliverAsync(item); }
                finally { Interlocked.Decrement(ref _pending); }
            }
        }

        private async Task DeliverAsync(T item)
        {
            using var cts = new CancellationTokenSource(_owner._timeout, _owner.Time);
            try
            {
                await _deliver(item, cts.Token).AsTask().WaitAsync(_owner._timeout, _owner.Time);
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
                    "[MentorAgent] Action observer {Observer} {What}{On} — the action was not affected. Until it answers " +
                    "again, its failures are logged at Debug.", _name, what, _on);
            else
                _owner._logger.LogDebug("[MentorAgent] Action observer {Observer} {What} again.", _name, what);

            if (ex is not null)
                _owner._logger.LogDebug(ex, "[MentorAgent] Action observer {Observer} threw.", _name);
        }
    }
}
