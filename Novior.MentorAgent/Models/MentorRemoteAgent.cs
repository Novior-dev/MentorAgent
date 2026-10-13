namespace Novior.MentorAgent.Models;

/// <summary>
/// Configuration for a remote A2A (Agent-to-Agent) agent that MentorAgent
/// will connect to as a consumer. Remote agents appear in the Handoff workflow
/// just like local <c>[MentorAgent]</c> classes.
///
/// Usage:
/// <code>
/// options.RemoteAgents = [
///     new MentorRemoteAgent {
///         Name = "InventoryAgent",
///         Description = "Manages warehouse and inventory operations",
///         AgentCardUrl = "https://inventory.example.com"
///     }
/// ];
/// </code>
/// </summary>
public sealed class MentorRemoteAgent
{
    /// <summary>
    /// Name of the remote agent — used as the Handoff target name.
    /// Must be unique within the agent configuration.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// Human-readable description of what this agent does.
    /// Injected into the coordinator's system prompt so the AI knows when to route to it.
    /// </summary>
    public required string Description { get; set; }

    /// <summary>
    /// Base URL of the remote A2A agent (e.g. <c>https://inventory.example.com</c>).
    /// The SDK automatically appends <c>/.well-known/agent-card.json</c> to discover
    /// the agent's capabilities and communication endpoints.
    /// Do NOT include the <c>/.well-known/agent-card.json</c> path — pass only the base URL.
    /// </summary>
    public required string AgentCardUrl { get; set; }

    /// <summary>
    /// Optional authorization headers to include in A2A requests
    /// (e.g. Bearer tokens, API keys).
    /// <code>
    /// Headers = new Dictionary&lt;string, string&gt; {
    ///     ["Authorization"] = $"Bearer {apiKey}"
    /// }
    /// </code>
    /// </summary>
    public Dictionary<string, string>? Headers { get; set; }
}
