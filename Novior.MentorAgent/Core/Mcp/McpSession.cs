using Novior.MentorAgent.Models;
using Novior.MentorAgent.Options;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace Novior.MentorAgent.Core.Mcp;

/// <summary>One live connection to an MCP server, with the tool list it answered at connect time.</summary>
internal interface IMcpSession : IAsyncDisposable
{
    /// <summary>The configured name of the server, for status events and logs.</summary>
    string ServerName { get; }

    /// <summary>The tools the server published, captured once when the session opened.</summary>
    IReadOnlyList<AITool> Tools { get; }

    /// <summary>
    /// The tools the server marks <c>readOnlyHint: true</c> — its own word that calling them changes
    /// nothing. Tool filtering offers only those on a turn where no action matched (BUG-106).
    /// </summary>
    IReadOnlySet<string> ReadOnlyToolNames { get; }

    /// <summary>
    /// The MCP protocol's own liveness check. A shared session outlives the visitor who opened it,
    /// so the next one has to be able to find out that the child process died or the remote endpoint
    /// went away — otherwise pooling would hand out a corpse.
    /// </summary>
    Task<bool> IsAliveAsync(CancellationToken ct);
}

/// <summary>Opens sessions. An interface so tests need neither a network nor a child process.</summary>
internal interface IMcpSessionFactory
{
    Task<IMcpSession> ConnectAsync(MentorMcpServer server, CancellationToken ct);
}

/// <summary>The real thing: an <see cref="McpClient"/> over stdio or HTTP.</summary>
internal sealed class McpSession(string serverName, McpClient client, IReadOnlyList<AITool> tools) : IMcpSession
{
    public string ServerName => serverName;
    public IReadOnlyList<AITool> Tools => tools;

    public IReadOnlySet<string> ReadOnlyToolNames { get; } = new HashSet<string>(
        tools.OfType<McpClientTool>()
             .Where(t => t.ProtocolTool.Annotations?.ReadOnlyHint == true)
             .Select(t => t.Name),
        StringComparer.Ordinal);

    public async Task<bool> IsAliveAsync(CancellationToken ct)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            await client.PingAsync(cancellationToken: deadline.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public ValueTask DisposeAsync() => client.DisposeAsync();
}

/// <summary>
/// Builds the transport and opens the session. This is the code that used to live inside
/// <c>McpToolsBuilder.ConnectAsync</c>, moved so that it can be shared by the pool and replaced by a
/// fake in tests.
/// </summary>
internal sealed class McpSessionFactory(ILoggerFactory loggerFactory) : IMcpSessionFactory
{
    public async Task<IMcpSession> ConnectAsync(MentorMcpServer server, CancellationToken ct)
    {
        IClientTransport transport;

        if (!string.IsNullOrWhiteSpace(server.ServerUrl))
        {
            transport = new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Endpoint = new Uri(server.ServerUrl),
                    Name     = server.Name,
                },
                loggerFactory);
        }
        else if (!string.IsNullOrWhiteSpace(server.Command))
        {
            var envVars = server.EnvironmentVariables is not null
                ? server.EnvironmentVariables.ToDictionary(kv => kv.Key, kv => (string?)kv.Value)
                : null;

            // On Windows, .cmd/.bat scripts cannot be launched directly by Process.Start.
            // Wrap them in "cmd.exe /c <command> <args>" automatically.
            var command   = server.Command;
            var arguments = server.Arguments?.ToList() ?? [];
            if (OperatingSystem.IsWindows() &&
                !command.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                !Path.IsPathRooted(command))
            {
                arguments = ["/c", command, .. arguments];
                command   = "cmd.exe";
            }

            transport = new StdioClientTransport(
                new StdioClientTransportOptions
                {
                    Command              = command,
                    Arguments            = arguments,
                    Name                 = server.Name,
                    EnvironmentVariables = envVars,
                },
                loggerFactory);
        }
        else
        {
            throw new InvalidOperationException(
                $"[MentorAgent] MCP server '{server.Name}' must specify either " +
                "'ServerUrl' (HTTP) or 'Command' (stdio).");
        }

        var client = await McpClient.CreateAsync(transport, loggerFactory: loggerFactory, cancellationToken: ct);
        var tools = await client.ListToolsAsync(cancellationToken: ct);

        return new McpSession(server.Name, client, [.. tools]);
    }
}
