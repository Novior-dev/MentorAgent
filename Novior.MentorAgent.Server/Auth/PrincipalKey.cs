using System.Security.Claims;
using Novior.MentorAgent.Core;

namespace Novior.MentorAgent.Server.Auth;

/// <summary>
/// Who a signed-in principal is, for the owner checks of <c>/mentor/approve</c>, <c>/mentor/cancel</c> and
/// <c>/mentor/session</c>, and for the purpose that binds a session snapshot to its owner (BUG-130).
///
/// <para>
/// Up to rc.14 both read <c>NameIdentifier</c> alone. A principal without it — Windows authentication,
/// JwtBearer with <c>MapInboundClaims = false</c> — was refused its own confirmations, its own Stop and its
/// own session. The order is <see cref="MentorUserKey"/>'s, the one memory and the rate limits use:
/// <c>NameIdentifier</c>, then <c>sub</c>, then <c>oid</c>, then the identity's name.
/// </para>
///
/// <para>
/// The key names the claim it came from, so a name never matches somebody else's identifier. A
/// <c>NameIdentifier</c> keeps the rc.14 form, <c>user:&lt;id&gt;</c>: snapshots saved by rc.14 still restore.
/// </para>
/// </summary>
internal static class PrincipalKey
{
    /// <summary>The principal's key, or <see langword="null"/> when it is not signed in or carries no id.</summary>
    internal static string? Of(ClaimsPrincipal? principal)
        => MentorUserKey.Find(principal) is { } key
            ? (key.Claim == "nameid" ? "user:" : key.Claim + ":") + key.Value
            : null;
}
