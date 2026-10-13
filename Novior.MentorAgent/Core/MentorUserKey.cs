using System.Security.Claims;

namespace Novior.MentorAgent.Core;

/// <summary>
/// Who a signed-in principal is: its <c>NameIdentifier</c> claim, else <c>sub</c>, else <c>oid</c>, else the
/// identity's name. One order for memory and <c>RateLimitPerUser</c> here and for the owner checks of
/// Novior.MentorAgent.Server (BUG-130, BUG-142).
///
/// <para>
/// Memory read <c>NameIdentifier</c>, then the name. A JwtBearer principal with <c>MapInboundClaims = false</c>
/// has neither — it keeps <c>sub</c> — so it was keyed as anonymous, a new key per session: neither its memory
/// nor its rate limit followed the person.
/// </para>
/// </summary>
internal static class MentorUserKey
{
    /// <summary>The claim the key comes from (<c>nameid</c>, <c>sub</c>, <c>oid</c>, <c>name</c>) and its value.</summary>
    internal static (string Claim, string Value)? Find(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true) return null;

        return Take("nameid", principal.FindFirst(ClaimTypes.NameIdentifier)?.Value)
            ?? Take("sub", principal.FindFirst("sub")?.Value)
            ?? Take("oid", principal.FindFirst("oid")?.Value)
            ?? Take("name", principal.Identity.Name);
    }

    /// <summary>
    /// The key memory and <c>RateLimitPerUser</c> use, or <see langword="null"/> when the principal is not
    /// signed in or carries no id. A <c>NameIdentifier</c> is the key as it is — the form every release has
    /// used, so the memories stored under it are still found. Any other claim is named in the key
    /// (<c>sub:…</c>, <c>name:…</c>): a name never matches another user's identifier.
    /// </summary>
    internal static string? Of(ClaimsPrincipal? principal)
        => Find(principal) is { } key
            ? key.Claim == "nameid" ? key.Value : $"{key.Claim}:{key.Value}"
            : null;

    /// <summary>
    /// How a log line names the user: <c>caller#</c> and a fingerprint of the id, never the id or the name
    /// itself, which are often an e-mail address (BUG-148). The fingerprint is the MCP server's audit line's
    /// for the same id, so one user's lines can be followed across the chat and MCP.
    /// </summary>
    internal static string LogTag(ClaimsPrincipal? principal)
        => Find(principal) is { } key
            ? "caller#" + Convert.ToHexString(
                  System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("user:" + key.Value)))[..10]
            : "a caller with no id";

    private static (string Claim, string Value)? Take(string claim, string? value)
        => string.IsNullOrEmpty(value) ? null : (claim, value);
}
