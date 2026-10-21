using System.Security.Claims;
using FluentAssertions;
using Novior.MentorAgent.Tests.Harness;
using Xunit;

namespace Novior.MentorAgent.Tests.Prompt;

/// <summary>
/// BUG-183 — the "Roles:" line of the session context lists what <c>IsInRole</c> answers to.
///
/// <para>
/// The gate decides with <c>IsInRole</c>, which reads each identity's own <c>RoleClaimType</c>. The line the
/// model reads was built from <c>ClaimTypes.Role</c> claims only, so with roles in a <c>roles</c> claim — Entra
/// ID, or JwtBearer with <c>MapInboundClaims = false</c> — a call passed the gate while the model was told no
/// role at all, and a <c>ClaimTypes.Role</c> claim the gate ignores was announced as a role.
/// </para>
/// </summary>
public sealed class UserRolesContextTests
{
    private static async Task<string?> RolesLineAsync(MentorHostFixture host)
    {
        host.Chat!.Say("ok");
        await host.RunTurnAsync("what can I do here?");

        return (host.Chat.Calls[0].Instructions ?? "")
            .Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .SingleOrDefault(l => l.StartsWith("- Roles:", StringComparison.Ordinal));
    }

    private static ClaimsIdentity Identity(string roleClaimType, params Claim[] claims)
        => new([new Claim(ClaimTypes.Name, "dana"), .. claims], "Test", ClaimTypes.Name, roleClaimType);

    [Fact]
    public async Task Roles_in_a_roles_claim_reach_the_model()
    {
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false);
        var user = new ClaimsPrincipal(Identity("roles",
            new Claim("roles", "Manager"), new Claim("roles", "Auditor"), new Claim(ClaimTypes.Role, "Ghost")));
        host.Auth.SignInAs(user);

        user.IsInRole("Manager").Should().BeTrue("the gate finds the role through the identity's RoleClaimType");
        user.IsInRole("Ghost").Should().BeFalse("a ClaimTypes.Role claim is not this identity's role");

        (await RolesLineAsync(host)).Should().Be("- Roles: Manager, Auditor",
            "the model is told the roles the gate checks, and no other");
    }

    [Fact]
    public async Task Roles_held_by_a_second_identity_are_listed_too()
    {
        // IsInRole asks every identity of the principal; the line does the same, each by its own claim type.
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false);
        host.Auth.SignInAs(new ClaimsPrincipal(
        [
            Identity("roles", new Claim("roles", "Manager")),
            Identity(ClaimTypes.Role, new Claim(ClaimTypes.Role, "Support"), new Claim(ClaimTypes.Role, "Manager")),
        ]));

        (await RolesLineAsync(host)).Should().Be("- Roles: Manager, Support");
    }

    [Fact]
    public async Task A_windows_identitys_group_sids_are_not_listed()
    {
        // A Windows identity's role claims are its groups' SIDs: IsInRole turns DOMAIN\Group into one, the
        // model could do nothing with "S-1-5-32-545".
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false);
        host.Auth.SignInAs(new ClaimsPrincipal(Identity(ClaimTypes.GroupSid,
            new Claim(ClaimTypes.GroupSid, "S-1-5-32-545"), new Claim(ClaimTypes.GroupSid, "S-1-1-0"))));

        (await RolesLineAsync(host)).Should().BeNull();
        host.Chat!.Calls[0].Instructions.Should().NotContain("S-1-5-32-545");
    }

    [Fact]
    public async Task Roles_in_ClaimTypes_Role_are_listed_as_before()
    {
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false);
        host.Auth.SignIn("dana", "Manager", "Auditor");

        (await RolesLineAsync(host)).Should().Be("- Roles: Manager, Auditor");
    }
}
