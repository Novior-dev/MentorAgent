namespace Novior.MentorAgent.Abstractions.Models;

/// <summary>Where a call reached the gate from.</summary>
public enum MentorActionSurface
{
    /// <summary>
    /// The turn runs in the application's own process, with no MentorAgent transport in between: a Blazor Server
    /// circuit, or a MAUI app (Blazor Hybrid).
    /// </summary>
    InProcess = 0,

    /// <summary>The SignalR hub of Novior.MentorAgent.Server, which the WebAssembly client and the other clients use.</summary>
    Hub = 1,

    /// <summary>The <c>/mentor/chat</c> endpoint (server-sent events).</summary>
    Sse = 2,

    /// <summary>The MCP server (<c>MapMentorAgentMcp</c>): a client called the tool directly.</summary>
    Mcp = 3,

    /// <summary>A task from another agent, over A2A.</summary>
    A2A = 4,
}
