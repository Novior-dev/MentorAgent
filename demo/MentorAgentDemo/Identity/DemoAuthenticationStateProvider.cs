using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace MentorAgentDemo.Identity;

/// <summary>
/// The signed-in user of one visitor's circuit, chosen with the role selector instead of a sign-in. MentorAgent
/// reads it as it would read a real one: <c>RequiredRoles</c> is checked with <c>IsInRole</c> on this principal.
/// </summary>
public sealed class DemoAuthenticationStateProvider : AuthenticationStateProvider
{
    public DemoUser Current { get; private set; } = DemoUser.Default;

    public override Task<AuthenticationState> GetAuthenticationStateAsync()
        => Task.FromResult(new AuthenticationState(Principal(Current)));

    /// <summary>Acts as <paramref name="user"/> from now on, in this circuit only.</summary>
    public void SignInAs(DemoUser user)
    {
        if (user == Current) return;
        Current = user;
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }

    private static ClaimsPrincipal Principal(DemoUser user)
        => new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, user.Id),
                new Claim(ClaimTypes.Name, user.Name),
                new Claim(ClaimTypes.Role, user.Role),
            ],
            authenticationType: "Demo"));
}
