using System.Security.Claims;

namespace Novior.MentorAgent.Server.Auth;

/// <summary>
/// Scoped holder for the authenticated principal of a SignalR connection.
///
/// Each connection has one DI scope of its own (<c>MentorHubScopes</c>), built on connect and reused
/// for every hub method; SignalR's own scope per method call is not the one MentorAgent runs in.
/// <see cref="Hubs.MentorHub"/> writes <c>Context.User</c> into this holder at the start of each
/// method (with the connection id), so the principal is the current one even if the connection's
/// token was refreshed. <see cref="ServerAuthenticationStateProvider"/> then surfaces it to the
/// MentorAgent core, which resolves the current user through the standard
/// <c>AuthenticationStateProvider</c> abstraction.
/// </summary>
public sealed class MentorServerUserContext
{
    /// <summary>The authenticated principal for the current hub invocation, if any.</summary>
    public ClaimsPrincipal? User { get; set; }
}
