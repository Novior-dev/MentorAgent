namespace Novior.MentorAgent.Core;

/// <summary>
/// Supplies Level-2 specialists that were not found by the <c>[MentorAgent]</c> attribute scan
/// (roadmap #23). Register an implementation before <c>AddMentorAgent()</c>; every registered source
/// is asked once, while the coordinator is being built, and the agents it returns join the same
/// handoff graph as the attribute-declared ones — so <c>route_to_specialist</c> can reach them and
/// they return to the coordinator when done.
/// <para>
/// This lives in <c>MentorAgent</c> and not in <c>Novior.MentorAgent.Abstractions</c> because
/// <see cref="AIAgent"/> comes from <c>Microsoft.Agents.AI</c>, which Abstractions must not
/// reference if it is to stay WebAssembly-safe.
/// </para>
/// <para>
/// <c>Novior.MentorAgent.Declarative</c> is the implementation that ships: it builds agents from YAML.
/// </para>
/// </summary>
public interface IMentorAgentSource
{
    /// <summary>
    /// Builds the agents. Called once per coordinator, not per turn. Returning an empty list is
    /// normal (no definitions found) and must not throw — a source that cannot load should log and
    /// return nothing rather than take the assistant down with it.
    /// </summary>
    Task<IReadOnlyList<AIAgent>> GetAgentsAsync(MentorAgentSourceContext context, CancellationToken ct = default);
}

/// <summary>
/// What a <see cref="IMentorAgentSource"/> is given to build its agents.
/// </summary>
/// <param name="ChatClient">
/// The same chat client the coordinator uses, already wrapped in MentorAgent's middleware pipeline
/// (token counting, routing, observability). Build agents on this one, not on a fresh client, or
/// their usage disappears from the metrics.
/// </param>
/// <param name="Tools">
/// Every Level-1 tool, <b>already wrapped in the gate</b>. An agent built from a declarative
/// definition runs its own function-calling loop inside the workflow, where the coordinator's
/// middleware never reaches — so the gate has to travel with the function. Hand these to the agent
/// as-is and <c>RequiredRoles</c>, human approval, action feedback and per-tool metrics keep
/// working exactly as they do for a C# specialist. Building your own <c>AIFunction</c> from the same
/// method instead would silently bypass all four.
/// </param>
public sealed record MentorAgentSourceContext(
    IChatClient ChatClient,
    IReadOnlyList<AIFunction> Tools);
