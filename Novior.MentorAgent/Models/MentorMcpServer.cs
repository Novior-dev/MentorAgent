namespace Novior.MentorAgent.Models;

/// <summary>
/// Configuration for an external MCP (Model Context Protocol) server that MentorAgent
/// will connect to as a client. The server's tools become available to the coordinator
/// as L1 tools, just like <c>[MentorAction]</c> methods.
///
/// Usage:
/// <code>
/// options.McpServers = [
///     // stdio transport (local process launched via npx, uvx, etc.)
///     new MentorMcpServer {
///         Name = "filesystem",
///         Command = "npx",
///         Arguments = ["-y", "@modelcontextprotocol/server-filesystem", "/tmp"],
///         RequiresConfirmation = true
///     },
///     // HTTP transport (remote MCP server)
///     new MentorMcpServer {
///         Name = "my-tools",
///         ServerUrl = "https://my-mcp-server.example.com/mcp",
///         AllowedTools = ["search", "fetch"]
///     }
/// ];
/// </code>
/// </summary>
public sealed class MentorMcpServer
{
    /// <summary>Display name for this server (shown in the MCP status badge).</summary>
    public required string Name { get; set; }

    /// <summary>
    /// HTTP/SSE endpoint URL for HTTP-based MCP servers.
    /// Mutually exclusive with <see cref="Command"/>.
    /// </summary>
    public string? ServerUrl { get; set; }

    /// <summary>
    /// Executable command for stdio-based MCP servers (e.g. "npx", "uvx", "python").
    /// Mutually exclusive with <see cref="ServerUrl"/>.
    /// </summary>
    public string? Command { get; set; }

    /// <summary>
    /// Command-line arguments for stdio transport (e.g. ["-y", "@modelcontextprotocol/server-filesystem", "/tmp"]).
    /// </summary>
    public IList<string>? Arguments { get; set; }

    /// <summary>
    /// Optional environment variables passed to the stdio process.
    /// </summary>
    public IDictionary<string, string>? EnvironmentVariables { get; set; }

    /// <summary>
    /// If not null, only these tool names from the MCP server will be registered.
    /// Null (default) = register all tools from the server.
    /// </summary>
    public string[]? AllowedTools { get; set; }

    /// <summary>
    /// When true, the coordinator will ask the user to confirm before calling
    /// any tool from this MCP server (same as <c>[MentorAction(RequiresConfirmation=true)]</c>).
    /// Default: false
    /// </summary>
    public bool RequiresConfirmation { get; set; }

    /// <summary>
    /// Whether one connection to this server is shared by every session in the process.
    /// <see langword="null"/> (default) decides by transport: a server reached over
    /// <see cref="ServerUrl"/> is <b>shared</b>, a server started from <see cref="Command"/> is
    /// <b>per session</b>.
    ///
    /// <para>
    /// The default is the safe reading of each case. A remote endpoint is shared by nature, so
    /// pooling the client changes nothing it can observe. A local child process can hold state for
    /// the person who started it — <c>@modelcontextprotocol/server-memory</c> is a knowledge graph
    /// with no notion of a user — so sharing it would put every visitor in the same store.
    /// </para>
    ///
    /// <para>
    /// Set <see langword="true"/> for a stdio server you know is stateless, and the process starts
    /// one child instead of one per visitor: measured on the sample, two browser tabs were eight node
    /// processes and 580 MB. Set <see langword="false"/> for a URL server that keeps per-connection
    /// state.
    /// </para>
    /// </summary>
    public bool? Shared { get; set; }
}
