using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace MentorAgent_Helpdesk.Identity;

/// <summary>The roles of the helpdesk. MentorAgent's <c>RequiredRoles</c> name them.</summary>
public static class HelpdeskRoles
{
    public const string Agent = "Agent";
}

/// <summary>A made-up person to act as. With <see cref="Role"/> <see langword="null"/>, a user without roles.</summary>
public sealed record TestUser(string Id, string Name, string? Role)
{
    public static IReadOnlyList<TestUser> All { get; } =
    [
        new("alex", "Alex", HelpdeskRoles.Agent),
        new("sam", "Sam", null),
    ];

    public static TestUser Find(string id) => All.Single(u => u.Id == id);
}

/// <summary>
/// The signed-in user of one circuit, picked from <see cref="TestUser.All"/> instead of a real sign-in. MentorAgent
/// reads it as it would read a real one: <c>RequiredRoles</c> is checked with <c>IsInRole</c> on this principal.
/// Replace it with your application's authentication.
/// </summary>
public sealed class TestSignIn : AuthenticationStateProvider
{
    public TestUser Current { get; private set; } = TestUser.All[0];

    public override Task<AuthenticationState> GetAuthenticationStateAsync()
        => Task.FromResult(new AuthenticationState(Principal(Current)));

    /// <summary>Acts as <paramref name="user"/> from now on, in this circuit only.</summary>
    public void SignInAs(TestUser user)
    {
        if (user == Current) return;
        Current = user;
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }

    private static ClaimsPrincipal Principal(TestUser user)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id),
            new(ClaimTypes.Name, user.Name),
        };
        if (user.Role is not null) claims.Add(new Claim(ClaimTypes.Role, user.Role));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));
    }
}
