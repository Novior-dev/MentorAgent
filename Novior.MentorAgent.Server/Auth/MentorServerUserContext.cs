using System.Security.Claims;

namespace Novior.MentorAgent.Server.Auth;

/// <summary>
/// Scoped holder for the authenticated principal of the current SignalR invocation.
///
/// SignalR creates a new DI scope per hub method call, so <see cref="Hubs.MentorHub"/>
/// captures <c>Context.User</c> into this holder at the start of each method (mirroring
/// the SetConnectionId pattern). <see cref="ServerAuthenticationStateProvider"/> then
/// surfaces it to the MentorAgent core, which resolves the current user through the
/// standard <c>AuthenticationStateProvider</c> abstraction.
/// </summary>
public sealed class MentorServerUserContext
{
    /// <summary>The authenticated principal for the current hub invocation, if any.</summary>
    public ClaimsPrincipal? User { get; set; }
}
