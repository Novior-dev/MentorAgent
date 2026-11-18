using Microsoft.Extensions.AI;

namespace Novior.MentorAgent.Core;

/// <summary>
/// Story 1287 — builds the tool of an action of origin <c>endpoint</c>: the core has no HTTP of its own, so
/// Novior.MentorAgent.Server registers the one implementation. The tool runs the endpoint itself, in the process,
/// with its authorization first, as the user of the turn read from the session's services.
/// </summary>
internal interface IMentorEndpointActionRunner
{
    /// <summary>The tool for <paramref name="action"/>, whose <see cref="MethodDiscoveryInfo.Endpoint"/> is set.</summary>
    AIFunction Create(MethodDiscoveryInfo action, IServiceProvider services);
}

/// <summary>
/// Story 1287 — the endpoint an action of origin <c>endpoint</c> runs, as the core sees it: opaque but for its name in
/// the trace. Novior.MentorAgent.Server implements it.
/// </summary>
internal interface IMentorEndpoint
{
    /// <summary>
    /// The verb and the route, <c>POST /orders/{id}/cancel</c>: the <see cref="MentorActionRegistration.Source"/> of the
    /// endpoint's registration (<c>endpoint.trace</c>).
    /// </summary>
    string Display { get; }
}
