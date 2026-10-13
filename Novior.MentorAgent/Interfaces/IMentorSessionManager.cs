/// <summary>
/// Manages the Agent Framework <see cref="AgentSession"/> for the current session.
///
/// In Blazor Server: Scoped (one session per circuit/user).
/// In Blazor WASM:   Singleton (one session per tab).
///
/// AgentSession is the container for the Agent Framework conversation history:
/// we do not manage messages manually — the session handles that internally.
/// </summary>
public interface IMentorSessionManager
{
    /// <summary>
    /// Returns the current session, creating it on first access.
    /// </summary>
    Task<AgentSession> GetOrCreateSessionAsync(AIAgent agent, CancellationToken ct = default);

    /// <summary>
    /// Resets the current session (starts a new conversation).
    /// </summary>
    Task ResetAsync(AIAgent agent, CancellationToken ct = default);

    /// <summary>
    /// Serializes the current session for persistence (e.g. WASM localStorage).
    /// </summary>
    Task<System.Text.Json.JsonElement?> SerializeCurrentSessionAsync(AIAgent agent);

    /// <summary>
    /// Restores a serialized session.
    /// </summary>
    Task RestoreSessionAsync(AIAgent agent, System.Text.Json.JsonElement serialized, CancellationToken ct = default);

    /// <summary>Event: the session has changed (reset or restore).</summary>
    event Action? OnSessionChanged;
}