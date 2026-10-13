using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using System.Security.Claims;

namespace Novior.MentorAgent.Server.Auth;

/// <summary>
/// Bridges the ASP.NET Core server-side authenticated principal to the MentorAgent core,
/// which resolves the current user through the standard <see cref="AuthenticationStateProvider"/>.
///
/// This is what makes per-user memory, per-user rate limiting and <c>RequiredRoles</c> work
/// on the headless server (React / Vue / Angular / WASM clients) — not just in Blazor.
///
/// Identity source (in order):
/// <list type="number">
///   <item>SignalR hub calls → <c>HubCallerContext.User</c>, captured per-scope into
///     <see cref="MentorServerUserContext"/> by <see cref="Hubs.MentorHub"/>.</item>
///   <item>SSE / plain HTTP calls → <c>HttpContext.User</c> via <see cref="IHttpContextAccessor"/>.</item>
/// </list>
/// Registered with <c>TryAddScoped</c> so a host that already provides its own
/// <see cref="AuthenticationStateProvider"/> (e.g. a Blazor server) is never overridden.
///
/// <para>
/// But <c>AddRazorComponents()</c> registers Blazor's provider with <c>TryAdd</c> too: called after
/// <c>AddMentorAgentServer()</c>, it adds nothing, and this class becomes the provider of every Blazor
/// circuit and prerender. So it takes the state Blazor hands it, as Blazor's own does
/// (<see cref="IHostEnvironmentAuthenticationStateProvider"/>), and answers with it from then on — the
/// circuit's user, whatever the order of the two calls (BUG-131).
/// </para>
/// </summary>
internal sealed class ServerAuthenticationStateProvider
    : AuthenticationStateProvider, IHostEnvironmentAuthenticationStateProvider
{
    private readonly MentorServerUserContext _hubUser;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private Task<AuthenticationState>? _hostState;

    public ServerAuthenticationStateProvider(
        MentorServerUserContext hubUser,
        IHttpContextAccessor httpContextAccessor)
    {
        _hubUser = hubUser;
        _httpContextAccessor = httpContextAccessor;
    }

    public override Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        if (_hostState is not null)
            return _hostState;

        var principal = _hubUser.User
            ?? _httpContextAccessor.HttpContext?.User
            ?? new ClaimsPrincipal(new ClaimsIdentity());

        return Task.FromResult(new AuthenticationState(principal));
    }

    /// <summary>What Blazor calls when a circuit or a prerender starts, with the user it authenticated.</summary>
    public void SetAuthenticationState(Task<AuthenticationState> authenticationStateTask)
    {
        _hostState = authenticationStateTask ?? throw new ArgumentNullException(nameof(authenticationStateTask));
        NotifyAuthenticationStateChanged(_hostState);
    }
}
