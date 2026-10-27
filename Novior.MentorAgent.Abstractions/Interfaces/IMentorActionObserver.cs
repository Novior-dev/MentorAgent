namespace Novior.MentorAgent.Abstractions.Interfaces;

/// <summary>
/// Receives every decision MentorAgent's tool gate takes — allowed, denied, confirmation asked, confirmed,
/// rejected, stopped, run, failed — for every session and every surface of the process: the Blazor Server
/// circuit, the MAUI app, the hub, <c>/mentor/chat</c>, the MCP server and A2A. For an audit trail of the
/// actions, with who asked and why the gate decided as it did.
///
/// <para>
/// Register it with <c>AddMentorActionObserver&lt;T&gt;()</c>, or as a singleton
/// <see cref="IMentorActionObserver"/> of your own; every observer registered is called, in the order of
/// registration. It is resolved once, from the application's root services: an observer that needs scoped
/// services creates a scope with <c>IServiceScopeFactory</c>.
/// </para>
///
/// <para>
/// It never runs on the action's path. Each observer has a queue of its own and receives the decisions in
/// the order they were taken; a slow observer or one that throws delays nothing and stops nothing, and its
/// failure is logged. A call that has not finished within 5 seconds is no longer awaited: the next decision
/// may then reach the observer while that call is still running, so an observer must be thread-safe.
/// </para>
///
/// <para>
/// <see cref="IMentorStateService.OnActionStarted"/> is the per-session event a component shows; this is
/// the process-wide record. A member added to this interface in a later version comes with a default
/// implementation.
/// </para>
/// </summary>
public interface IMentorActionObserver
{
    /// <summary>One decision of the gate.</summary>
    /// <param name="decision">What was decided, for which call, and for whom.</param>
    /// <param name="cancellationToken">Cancelled when the call takes longer than 5 seconds.</param>
    ValueTask OnDecisionAsync(MentorActionDecision decision, CancellationToken cancellationToken);
}
