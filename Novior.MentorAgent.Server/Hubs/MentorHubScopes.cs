using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace Novior.MentorAgent.Server.Hubs;

/// <summary>
/// Keeps ONE DI scope per SignalR connection, so the MentorAgent coordinator (MCP connections,
/// skills, discovery, agent) is built once per connection and reused for every message —
/// mirroring Blazor Server's "one scope per circuit" model.
///
/// Without this, SignalR's default "one scope per hub method call" would, on every message:
///   - rebuild the coordinator and reconnect the external MCP servers (spawning a process each time),
///   - and start a brand-new <c>AgentSession</c>, so the conversation history never persisted.
///
/// Singleton. Each connection's scope is disposed when the connection ends.
/// </summary>
public sealed class MentorHubScopes : IAsyncDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ConcurrentDictionary<string, Connection> _scopes = new();

    public MentorHubScopes(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    /// <summary>Where a close that could not wait for its requests says so.</summary>
    internal ILogger? Logger { get; init; }

    /// <summary>
    /// How long a close waits for the requests inside the scope. Past it the scope is disposed anyway: a request that
    /// by a defect never left must not keep the connection's scope, and its MCP processes, for ever.
    /// </summary>
    internal TimeSpan CloseWait { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Returns the service provider of this connection's scope, creating it on first use.</summary>
    public IServiceProvider GetOrCreate(string connectionId)
        => _scopes.GetOrAdd(connectionId, _ => new Connection(_scopeFactory.CreateAsyncScope())).Scope.ServiceProvider;

    /// <summary>
    /// Returns an existing connection scope without creating one, or <see langword="null"/>.
    /// Used by out-of-band HTTP endpoints (e.g. <c>POST /mentor/cancel</c>) that must reach the
    /// live orchestrator of a SignalR connection without going through the hub — a hub method
    /// cannot run while <c>SendMessage</c> is awaiting on the same connection.
    /// </summary>
    public IServiceProvider? TryGet(string connectionId)
        => _scopes.TryGetValue(connectionId, out var connection) ? connection.Scope.ServiceProvider : null;

    /// <summary>
    /// Enters an existing connection scope for the work of one request, or <see langword="null"/> when the
    /// connection is gone or closing. The scope is not disposed while the request is inside it: the
    /// connection's close waits for the returned lease (BUG-254).
    /// </summary>
    /// <remarks>
    /// <see cref="TryGet"/> hands out a provider that the close may dispose a moment later, so an endpoint
    /// that resolved a service from it raced the close and answered 500. A lease is held only for work
    /// of the server's own, never while the client sends a body: a slow client must not keep a closed
    /// connection's scope, and its MCP processes, alive.
    /// </remarks>
    internal Lease? TryEnter(string connectionId)
        => _scopes.TryGetValue(connectionId, out var connection) && connection.TryEnter() ? new Lease(connection) : null;

    /// <summary>
    /// Disposes this connection's scope (closes MCP connections, disposes the orchestrator), once the
    /// requests inside it are done, at most <see cref="CloseWait"/>. From the start of the call no request can enter it.
    /// </summary>
    public async ValueTask RemoveAsync(string connectionId)
    {
        if (_scopes.TryRemove(connectionId, out var connection))
        {
            try
            {
                await connection.CloseAsync().WaitAsync(CloseWait).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // No connection id in the log: it is the key to the connection's endpoints.
                Logger?.LogWarning("[MentorAgent] A request still used a closed connection's scope after {Seconds} s; the scope is disposed anyway.",
                    (int)CloseWait.TotalSeconds);
            }
            await connection.Scope.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var connection in _scopes.Values)
            await connection.Scope.DisposeAsync();
        _scopes.Clear();
    }

    /// <summary>A request inside a connection's scope; disposing it lets the scope close.</summary>
    internal sealed class Lease : IDisposable
    {
        private Connection? _connection;

        internal Lease(Connection connection) => _connection = connection;

        /// <summary>The connection scope's services, alive until this lease is disposed.</summary>
        public IServiceProvider Services
            => (_connection ?? throw new ObjectDisposedException(nameof(Lease))).Scope.ServiceProvider;

        public void Dispose() => Interlocked.Exchange(ref _connection, null)?.Exit();
    }

    internal sealed class Connection(AsyncServiceScope scope)
    {
        private readonly object _gate = new();
        private int _inside;
        private bool _closed;
        private TaskCompletionSource? _empty;

        public AsyncServiceScope Scope { get; } = scope;

        public bool TryEnter()
        {
            lock (_gate)
            {
                if (_closed) return false;
                _inside++;
                return true;
            }
        }

        public void Exit()
        {
            TaskCompletionSource? empty;
            lock (_gate)
            {
                if (--_inside > 0 || !_closed) return;
                empty = _empty;
            }
            empty?.TrySetResult();
        }

        /// <summary>Lets no request in any more; completes when the last one inside has left.</summary>
        public Task CloseAsync()
        {
            lock (_gate)
            {
                _closed = true;
                if (_inside == 0) return Task.CompletedTask;
                _empty = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                return _empty.Task;
            }
        }
    }
}
