using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace Novior.MentorAgent.Scanner;

/// <summary>
/// Story 1636, slice B — the scan's own user (<c>page.scan-user</c>). For each set of roles the scan issues a one-time
/// token, kept in memory only; the browser redeems it once at <c>/_mentor/scan/start</c> and gets a session cookie in
/// its place. While that session lasts, a request from the loopback with the cookie is the scan's user, with those
/// roles. <see cref="End"/> voids the token and the session: the scan calls it after each pass and when it ends, also
/// when it ends with an error.
/// </summary>
internal sealed class MentorUiScanGate
{
    internal const string StartPath = "/_mentor/scan/start";
    internal const string TokenHeader = "X-MentorAgent-Scan-Token";
    internal const string CookieName = ".MentorAgent.Scan";
    internal const string AuthenticationType = "MentorAgent.Scan";
    internal const string UserName = "mentor-ui-scan";

    private readonly Lock _lock = new();
    private byte[]? _token;
    private byte[]? _session;
    private ClaimsPrincipal? _user;
    private IReadOnlyList<string> _roles = [];

    /// <summary>How many sessions were started: one for each token redeemed.</summary>
    internal int Sessions { get; private set; }

    /// <summary>Whether a token waits to be redeemed or a session is open.</summary>
    internal bool Active
    {
        get
        {
            lock (_lock) return _token is not null || _session is not null;
        }
    }

    /// <summary>
    /// A new one-time token for a user with <paramref name="roles"/>; whatever token or session was open is void.
    /// </summary>
    internal string Issue(IReadOnlyList<string> roles)
    {
        lock (_lock)
        {
            _token = RandomNumberGenerator.GetBytes(32);
            _session = null;
            _user = null;
            _roles = [.. roles];
            return Convert.ToHexString(_token);
        }
    }

    /// <summary>
    /// Redeems <paramref name="token"/>: when it is the one issued and not used yet, it is burned and the value of the
    /// session cookie comes back; otherwise <see langword="null"/>, and an open session stays as it is.
    /// </summary>
    internal string? Redeem(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        lock (_lock)
        {
            if (_token is null || !Matches(_token, token)) return null;
            _token = null;
            _session = RandomNumberGenerator.GetBytes(32);
            _user = Principal(_roles);
            Sessions++;
            return Convert.ToHexString(_session);
        }
    }

    /// <summary>The scan's user when <paramref name="cookie"/> is the open session's, otherwise <see langword="null"/>.</summary>
    internal ClaimsPrincipal? UserFor(string? cookie)
    {
        if (string.IsNullOrEmpty(cookie)) return null;
        lock (_lock)
            return _session is not null && Matches(_session, cookie) ? _user : null;
    }

    /// <summary>Voids the token and the session: the cookie stops working.</summary>
    internal void End()
    {
        lock (_lock)
        {
            _token = null;
            _session = null;
            _user = null;
            _roles = [];
        }
    }

    private static bool Matches(byte[] expected, string given)
    {
        var bytes = Encoding.ASCII.GetBytes(given);
        var wanted = Encoding.ASCII.GetBytes(Convert.ToHexString(expected));
        return CryptographicOperations.FixedTimeEquals(bytes, wanted);
    }

    private static ClaimsPrincipal Principal(IReadOnlyList<string> roles)
    {
        var identity = new ClaimsIdentity(AuthenticationType, ClaimTypes.Name, ClaimTypes.Role);
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, UserName));
        identity.AddClaim(new Claim(ClaimTypes.Name, UserName));
        foreach (var role in roles) identity.AddClaim(new Claim(ClaimTypes.Role, role));
        return new ClaimsPrincipal(identity);
    }
}
