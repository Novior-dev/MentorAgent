using Novior.MentorAgent.Models;
using Novior.MentorAgent.Core.Mcp;
using Novior.MentorAgent.Options;
using Microsoft.Extensions.AI;

namespace Novior.MentorAgent.Tests.Fakes;

/// <summary>
/// An MCP server that needs neither a socket nor a child process — the only way to test connection
/// sharing without starting the very thing it is meant to avoid starting.
/// </summary>
internal sealed class FakeMcpSessionFactory : IMcpSessionFactory
{
    /// <summary>Connections actually opened. The number the pool exists to keep at one.</summary>
    internal int Connects;

    /// <summary>Sessions handed out, in order.</summary>
    internal List<FakeMcpSession> Sessions { get; } = [];

    /// <summary>Thrown by every connect while set — a server that is down.</summary>
    internal Exception? ThrowOnConnect { get; set; }

    /// <summary>Awaited inside connect, so a test can hold several callers on one connection.</summary>
    internal TaskCompletionSource? Gate { get; set; }

    /// <summary>
    /// The tools a named server offers, and which of them it marks <c>readOnlyHint</c>. A server not
    /// listed offers one tool, <c>{name}_tool</c>, and marks nothing read-only.
    /// </summary>
    internal Dictionary<string, (AITool[] Tools, string[] ReadOnly)> Servers { get; } = [];

    public async Task<IMcpSession> ConnectAsync(MentorMcpServer server, CancellationToken ct)
    {
        Interlocked.Increment(ref Connects);
        if (Gate is not null) await Gate.Task;
        if (ThrowOnConnect is { } ex) throw ex;

        var session = Servers.TryGetValue(server.Name, out var configured)
            ? new FakeMcpSession(server.Name, configured.Tools, configured.ReadOnly)
            : new FakeMcpSession(server.Name);
        lock (Sessions) Sessions.Add(session);
        return session;
    }
}

internal sealed class FakeMcpSession(string name, AITool[]? tools = null, string[]? readOnly = null) : IMcpSession
{
    public string ServerName => name;

    public IReadOnlyList<AITool> Tools { get; } =
        tools ?? [AIFunctionFactory.Create(() => "ok", $"{name}_tool", "A tool from a fake MCP server")];

    /// <summary>The tools the server marks <c>readOnlyHint: true</c> (BUG-106).</summary>
    public IReadOnlySet<string> ReadOnlyToolNames { get; } =
        new HashSet<string>(readOnly ?? [], StringComparer.Ordinal);

    /// <summary>Set to false to play a session whose process died after it was cached.</summary>
    internal bool Alive { get; set; } = true;

    internal bool Disposed { get; private set; }

    public Task<bool> IsAliveAsync(CancellationToken ct) => Task.FromResult(Alive);

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
