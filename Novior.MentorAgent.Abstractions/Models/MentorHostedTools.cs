namespace Novior.MentorAgent.Abstractions.Models;

/// <summary>
/// Provider-hosted tools the coordinator may use (roadmap #15 — Agent Framework <i>hosted tools</i>).
///
/// <para>
/// Unlike <c>[MentorAction]</c> function tools, these are <b>not</b> executed by your application:
/// they run on the model provider's infrastructure during inference. MentorAgent only declares them,
/// which is exactly what the Agent Framework does (<c>HostedWebSearchTool</c>,
/// <c>HostedCodeInterpreterTool</c>, <c>HostedFileSearchTool</c>, <c>HostedImageGenerationTool</c>,
/// <c>HostedMcpServerTool</c> from <c>Microsoft.Extensions.AI</c>).
/// </para>
///
/// <para>
/// ⚠️ <b>Availability depends on the provider.</b> Azure OpenAI / OpenAI <i>Responses</i> clients
/// support the full set; the <i>Chat Completions</i> client supports web search only. Enabling a tool
/// the provider does not support makes the provider reject the call — see the package README.
/// </para>
/// </summary>
[Flags]
public enum MentorHostedTools
{
    /// <summary>No hosted tools (default).</summary>
    None = 0,

    /// <summary>
    /// Web search — the provider searches the web for up-to-date information (current events,
    /// documentation, anything past the model's training cut-off).
    /// </summary>
    WebSearch = 1,

    /// <summary>
    /// Code interpreter — the provider writes and runs code in its own sandbox for exact
    /// computation, data analysis and file processing. The code never runs on your servers.
    /// </summary>
    CodeInterpreter = 2,

    /// <summary>
    /// File search — the provider searches documents you uploaded to its vector store(s).
    /// Requires <c>MentorOptions.FileSearchVectorStoreIds</c>.
    /// </summary>
    FileSearch = 4,

    /// <summary>
    /// Image generation — the provider generates images from the conversation and returns them
    /// inline. The generated images are rendered in the message bubble.
    /// </summary>
    ImageGeneration = 8,

    /// <summary>
    /// Hosted MCP — the <b>provider</b> connects to a remote MCP server and calls its tools during
    /// inference (as opposed to <c>MentorOptions.McpServers</c>, where <i>your</i> process is the MCP
    /// client). Requires <c>MentorOptions.HostedMcpServers</c>.
    /// </summary>
    HostedMcp = 16,
}
