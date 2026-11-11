namespace Novior.MentorAgent.Core;

/// <summary>
/// Supplies Level-2 specialists that were not found by the <c>[MentorAgent]</c> attribute scan
/// (roadmap #23). Register an implementation before <c>AddMentorAgent()</c>; every registered source
/// is asked once, while the coordinator is being built, and the agents it returns join the same
/// handoff graph as the attribute-declared ones — so <c>route_to_specialist</c> can reach them and
/// they return to the coordinator when done.
/// <para>
/// A coordinator is built for every new session, so a source is asked at least once per session: once per
/// Blazor circuit, MAUI app scope or hub connection, and once per request on <c>/mentor/chat</c> and per task
/// over A2A, where a request is the session. <see cref="MentorAgentSourceContext.SessionId"/> says which one.
/// What each call loaded and left out reaches every <see cref="IMentorAgentSourceObserver"/>.
/// </para>
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

    /// <summary>
    /// Builds the agents and says what was left out, and why: the method MentorAgent calls. By default it returns the
    /// agents of <see cref="GetAgentsAsync"/> and leaves nothing out, so a source that implements only that method
    /// works as it always did. Implement it to report a definition you could not load, or a tool you took away from an
    /// agent: the report reaches every <see cref="IMentorAgentSourceObserver"/>, and nothing is left out in silence.
    /// The same rule as <see cref="GetAgentsAsync"/>: one bad definition is a rejection, not an exception.
    /// </summary>
    async Task<MentorAgentSourceResult> LoadAsync(MentorAgentSourceContext context, CancellationToken ct = default)
        => new(await GetAgentsAsync(context, ct), []);
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
    IReadOnlyList<AIFunction> Tools)
{
    /// <summary>
    /// The session the coordinator is built for: an opaque id, the same as <c>MentorActionRequest.SessionId</c> and
    /// <c>MentorActionDecision.SessionId</c> — the circuit, the MAUI app's scope, the hub connection, the
    /// <c>/mentor/chat</c> request, the A2A task. A source that gives each session the agents of one version, say,
    /// reads it here, as an <c>IMentorActionGovernor</c> reads the request's. The coordinator that
    /// <c>WarmUpAtStartup</c> builds has a session of its own, which no user continues.
    /// </summary>
    public string? SessionId { get; init; }
}
