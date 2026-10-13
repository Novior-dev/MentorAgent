using System.Security.Claims;
using FluentAssertions;
using Novior.MentorAgent.Memory;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Novior.MentorAgent.Tests.Memory;

/// <summary>
/// The key memory and <c>RateLimitPerUser</c> give a signed-in user (BUG-142).
///
/// <para>
/// Memory read <c>NameIdentifier</c>, then the name, while the owner checks and the MCP rate limit read
/// <c>NameIdentifier</c>, <c>sub</c>, <c>oid</c>, then the name. A JwtBearer principal with
/// <c>MapInboundClaims = false</c> keeps <c>sub</c> and often has no name: it was keyed as anonymous, a new key
/// per session, so neither its memory nor its rate limit followed the person. Now the order is the same
/// everywhere, a <c>NameIdentifier</c> is still the key as it is, and any other claim is named in the key.
/// </para>
/// </summary>
public sealed class UserKeyTests
{
    private static async Task<string> UserIdInNewSessionAsync(MentorHostFixture host)
    {
        await using var scope = host.Get<IServiceScopeFactory>().CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<MentorMemoryService>().GetUserIdAsync();
    }

    private static Claim[] Principal(string claim, string value) => claim switch
    {
        "nameid" => [new(ClaimTypes.NameIdentifier, value), new(ClaimTypes.Name, "Ada")],
        "name"   => [new(ClaimTypes.Name, value)],
        _        => [new(claim, value)],
    };

    [Theory]
    [InlineData("nameid", "u-42", "u-42")]
    [InlineData("sub", "s-1", "sub:s-1")]
    [InlineData("oid", "o-1", "oid:o-1")]
    [InlineData("name", @"CONTOSO\bob", @"name:CONTOSO\bob")]
    public async Task A_signed_in_user_keeps_one_key_across_sessions(string claim, string value, string expected)
    {
        await using var host = MentorHostFixture.Create(o => o.UseMemoryContext = true);
        host.Auth.SignInWith(Principal(claim, value));

        (await host.Get<MentorMemoryService>().GetUserIdAsync()).Should().Be(expected,
            "a NameIdentifier is the key as before; any other claim is named in it");
        (await UserIdInNewSessionAsync(host)).Should().Be(expected,
            "the key follows the person, not the session — up to rc.14 sub and oid alone were anonymous per session");
    }

    [Fact]
    public async Task Sub_comes_before_the_name()
    {
        await using var host = MentorHostFixture.Create(o => o.UseMemoryContext = true);
        host.Auth.SignInWith(new Claim("sub", "s-1"), new Claim(ClaimTypes.Name, "Ada"));

        (await host.Get<MentorMemoryService>().GetUserIdAsync()).Should().Be("sub:s-1");
    }

    [Fact]
    public async Task A_name_never_reads_the_memories_of_a_user_whose_identifier_it_matches()
    {
        await using var host = MentorHostFixture.Create(o => o.UseMemoryContext = true);
        var store = host.Get<IMentorMemoryStore>();
        await store.SaveAsync("bob", "codice_pratica", "PRATICA-8891");   // what user "bob" (NameIdentifier) remembered

        host.Auth.SignInWith(new Claim(ClaimTypes.Name, "bob"));   // another user, whose name is "bob"
        var memory = host.Get<MentorMemoryService>();

        (await memory.GetUserIdAsync()).Should().Be("name:bob");
        (await memory.GetMemoryContextAsync()).Should().NotContain("PRATICA-8891");
    }

    [Fact]
    public async Task A_signed_in_user_with_no_id_is_keyed_per_session_not_in_the_shared_bucket()
    {
        // Authenticated, but with no NameIdentifier, sub, oid or name. rc.14 keyed it "anonymous" — the bucket
        // every Shared visitor writes to — whatever AnonymousIdentity said.
        await using var host = MentorHostFixture.Create(o => o.UseMemoryContext = true);
        host.Auth.SignInWith(new Claim("tenant", "contoso"));

        var first = await host.Get<MentorMemoryService>().GetUserIdAsync();

        first.Should().StartWith("anonymous:");
        (await UserIdInNewSessionAsync(host)).Should().NotBe(first);
    }
}
