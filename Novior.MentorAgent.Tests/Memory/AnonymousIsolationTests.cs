using FluentAssertions;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Memory;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Novior.MentorAgent.Tests.Memory;

/// <summary>
/// Who an unauthenticated visitor is (BUG-064, fixed properly in rc.5).
///
/// <para>
/// Measured live on rc.4: one anonymous circuit said <i>"il mio indirizzo di consegna è Via Roma 10,
/// Milano"</i>; a second circuit, told nothing, asked for its address and was given that one. Both
/// resolved to the literal key <c>"anonymous"</c> — and so did the rate-limit counter, so the sample's
/// 20 messages a minute were 20 for the whole site rather than 20 per visitor.
/// </para>
///
/// <para>
/// The default is now a key per session. The shared bucket stays available for the host it is right
/// for — a single-user desktop application, where it is what makes memory survive a restart — and
/// that choice is now explicit instead of inherited.
/// </para>
/// </summary>
public sealed class AnonymousIsolationTests
{
    private static async Task<string> UserIdInNewSessionAsync(MentorHostFixture host)
    {
        await using var scope = host.Get<IServiceScopeFactory>().CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<MentorMemoryService>().GetUserIdAsync();
    }

    [Fact]
    public async Task Per_session_is_the_default()
    {
        // The default is the whole fix: a host that never reads this option is safe.
        new MentorOptions { AppName = "x", ScanAssemblies = [typeof(MentorOptions).Assembly] }
            .AnonymousIdentity.Should().Be(MentorAnonymousIdentity.PerSession);
    }

    [Fact]
    public async Task Two_anonymous_sessions_get_different_keys()
    {
        await using var host = MentorHostFixture.Create(o => o.UseMemoryContext = true);

        var first = await host.Get<MentorMemoryService>().GetUserIdAsync();
        var second = await UserIdInNewSessionAsync(host);

        first.Should().StartWith("anonymous:");
        second.Should().StartWith("anonymous:");
        second.Should().NotBe(first, "two visitors are two visitors");
    }

    [Fact]
    public async Task One_session_keeps_its_own_key_across_turns()
    {
        // The other half: isolated must not mean forgetful *within* the visit, or memory would be
        // dead on arrival for anonymous users.
        await using var host = MentorHostFixture.Create(o => o.UseMemoryContext = true);
        var memory = host.Get<MentorMemoryService>();

        var first = await memory.GetUserIdAsync();
        var again = await memory.GetUserIdAsync();

        again.Should().Be(first);
    }

    [Fact]
    public async Task What_one_anonymous_visitor_says_is_not_recalled_for_the_next()
    {
        // The live defect, reproduced through the store the model reads from.
        await using var host = MentorHostFixture.Create(o => o.UseMemoryContext = true);

        var mine = await host.Get<MentorMemoryService>().GetUserIdAsync();
        await host.Get<IMentorMemoryStore>().SaveAsync(mine, "user_address", "Via Roma 10, Milano");

        var theirs = await UserIdInNewSessionAsync(host);
        (await host.Get<IMentorMemoryStore>().GetAsync(theirs, "user_address"))
            .Should().BeNull("the second visitor never said anything");
    }

    [Fact]
    public async Task Shared_restores_the_previous_behaviour_for_a_single_user_host()
    {
        await using var host = MentorHostFixture.Create(o =>
        {
            o.UseMemoryContext  = true;
            o.AnonymousIdentity = MentorAnonymousIdentity.Shared;
        });

        var first = await host.Get<MentorMemoryService>().GetUserIdAsync();
        var second = await UserIdInNewSessionAsync(host);

        first.Should().Be("anonymous");
        second.Should().Be("anonymous", "which is exactly what a desktop app wants");
    }

    [Fact]
    public async Task A_signed_in_user_is_unaffected_by_the_option()
    {
        // Their claims are the identity; keying by session would give the same person a different
        // memory in every tab.
        await using var host = MentorHostFixture.Create(o => o.UseMemoryContext = true);
        host.Auth.SignIn("u-42");

        (await host.Get<MentorMemoryService>().GetUserIdAsync()).Should().Be("u-42");
        (await UserIdInNewSessionAsync(host)).Should().Be("u-42");
    }

    [Fact]
    public async Task A_provider_that_throws_is_reported_once_and_the_session_stays_anonymous()
    {
        // BUG-072, found by reading rather than by running: the catch around
        // GetAuthenticationStateAsync was silent. On rc.4 a signed-in user whose provider threw fell
        // back to the stable key "anonymous"; since rc.5 the fallback is a key per session, so that
        // user gets a different memory in every circuit — and nothing said so. The answer is still
        // anonymous (fail closed), but it is now said, once, without a stack trace per visitor.
        await using var host = MentorHostFixture.Create(o => o.UseMemoryContext = true);
        host.Auth.ThrowOnRead = new InvalidOperationException(
            "GetAuthenticationStateAsync was called outside the render context");

        var svc    = host.Get<MentorMemoryService>();
        var first  = await svc.GetUserIdAsync();
        var again  = await svc.GetUserIdAsync();
        var second = await UserIdInNewSessionAsync(host);

        first.Should().StartWith("anonymous:");
        again.Should().Be(first, "the fallback is stable within one session");
        second.Should().StartWith("anonymous:").And.NotBe(first);

        host.Logs.Records.Count(r => r.Level == LogLevel.Warning
                                     && r.Message.Contains("AuthenticationStateProvider threw"))
            .Should().Be(2, "once per session, and there were two sessions — not once per call");
        host.Logs.Exceptions.Should().BeEmpty("a warning per visitor must never be a trace per visitor");
    }

    [Fact]
    public async Task A_provider_that_answers_is_not_reported()
    {
        // The control: the warning is about a provider that fails, not about there being one.
        await using var host = MentorHostFixture.Create(o => o.UseMemoryContext = true);
        host.Auth.SignIn("u-42");

        (await host.Get<MentorMemoryService>().GetUserIdAsync()).Should().Be("u-42");
        host.Logs.WarningText.Should().NotContain("AuthenticationStateProvider threw");
    }

    [Fact]
    public async Task The_rate_limit_counter_follows_the_same_key()
    {
        // One busy visitor used to exhaust the allowance for everyone: same key, same bucket. The
        // limiter reads whatever GetUserIdAsync returns, so the fix reaches it for free — asserted
        // here so a future change to either side cannot quietly re-couple them.
        await using var host = MentorHostFixture.Create(o =>
        {
            o.UseMemoryContext   = true;
            o.RateLimitPerUser   = 2;
            o.RateLimitWindowSecs = 60;
        });

        var limiter = host.Get<MentorRateLimiter>();
        var mine = await host.Get<MentorMemoryService>().GetUserIdAsync();
        var theirs = await UserIdInNewSessionAsync(host);

        limiter.CheckAndIncrement(mine).Allowed.Should().BeTrue();
        limiter.CheckAndIncrement(mine).Allowed.Should().BeTrue();
        limiter.CheckAndIncrement(mine).Allowed.Should().BeFalse("this visitor spent their two");
        limiter.CheckAndIncrement(theirs).Allowed.Should().BeTrue("the next visitor still has theirs");
    }
}
