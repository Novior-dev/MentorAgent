using Microsoft.Extensions.DependencyInjection;
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
    private readonly ConcurrentDictionary<string, AsyncServiceScope> _scopes = new();

    public MentorHubScopes(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    /// <summary>Returns the service provider of this connection's scope, creating it on first use.</summary>
    public IServiceProvider GetOrCreate(string connectionId)
        => _scopes.GetOrAdd(connectionId, _ => _scopeFactory.CreateAsyncScope()).ServiceProvider;

    /// <summary>
    /// Returns an existing connection scope without creating one, or <see langword="null"/>.
    /// Used by out-of-band HTTP endpoints (e.g. <c>POST /mentor/cancel</c>) that must reach the
    /// live orchestrator of a SignalR connection without going through the hub — a hub method
    /// cannot run while <c>SendMessage</c> is awaiting on the same connection.
    /// </summary>
    public IServiceProvider? TryGet(string connectionId)
        => _scopes.TryGetValue(connectionId, out var scope) ? scope.ServiceProvider : null;

    /// <summary>Disposes this connection's scope (closes MCP connections, disposes the orchestrator).</summary>
    public async ValueTask RemoveAsync(string connectionId)
    {
        if (_scopes.TryRemove(connectionId, out var scope))
            await scope.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var scope in _scopes.Values)
            await scope.DisposeAsync();
        _scopes.Clear();
    }
}
