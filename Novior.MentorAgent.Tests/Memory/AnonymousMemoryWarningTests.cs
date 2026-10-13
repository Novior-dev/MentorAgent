using FluentAssertions;
using Novior.MentorAgent.Memory;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Novior.MentorAgent.Tests.Memory;

/// <summary>
/// Every unauthenticated session shares the memory key <c>"anonymous"</c> — documented, and fine for
/// a single-user application. For a website it means one visitor's facts are recalled for the next
/// (BUG-064). BUG-060 put a warning on the server transports; nothing warned on the in-process path,
/// which is where a Blazor Server host puts the widget in its layout for every visitor. Since rc.5
/// the shared bucket is opt-in (<c>AnonymousIdentity = Shared</c>), which is what these tests ask
/// for: a host that chooses it for a desktop app and then ships it as a website still gets told the
/// moment a second session arrives.
///
/// <para>
/// Found live on rc.4, <c>MentorAgentNuget</c>: one anonymous circuit said <i>"il mio indirizzo di
/// consegna è Via Roma 10, Milano"</i>, a second circuit asked <i>"qual è il mio indirizzo?"</i> and
/// was told. The boot log was clean.
/// </para>
///
/// <para>
/// <b>When to say it is the whole design.</b> Not at startup — nothing then can tell a MAUI app (one
/// user, sharing is exactly right) from a website. The moment the leak becomes possible is the moment
/// a <em>second</em> session reads the shared bucket, so that is when it is said, once per process.
/// A single-user host never reaches it.
/// </para>
/// </summary>
public sealed class AnonymousMemoryWarningTests
{
    private static int SharedBucketWarnings(MentorHostFixture host) => host.Logs.Records.Count(r =>
        r.Level >= LogLevel.Warning && r.Message.Contains("share the memory key \"anonymous\""));

    /// <summary>A second session: a sibling DI scope, which is what a new circuit is.</summary>
    private static async Task ReadMemoryInAnotherSessionAsync(MentorHostFixture host)
    {
        using var scope = host.Get<IServiceScopeFactory>().CreateScope();
        await scope.ServiceProvider.GetRequiredService<MentorMemoryService>().GetMemoryContextAsync();
    }

    [Fact]
    public async Task A_second_anonymous_session_reading_memory_is_warned_about_once()
    {
        await using var host = MentorHostFixture.Create(o => { o.UseMemoryContext = true; o.AnonymousIdentity = Novior.MentorAgent.Options.MentorAnonymousIdentity.Shared; });

        await host.Get<MentorMemoryService>().GetMemoryContextAsync();
        await ReadMemoryInAnotherSessionAsync(host);
        await ReadMemoryInAnotherSessionAsync(host);

        SharedBucketWarnings(host).Should().Be(1,
            "the leak became possible when the second session arrived — said then, and only once");
        host.Logs.WarningText.Should().Contain("RateLimitPerUser",
            "the same key is the rate limiter's, so one visitor spends everybody's allowance");
    }

    [Fact]
    public async Task One_session_alone_is_never_warned()
    {
        // The single-user case — a MAUI app, a desktop tool. Sharing the bucket with yourself is the
        // point, and a warning here would be one people learn to ignore.
        await using var host = MentorHostFixture.Create(o => { o.UseMemoryContext = true; o.AnonymousIdentity = Novior.MentorAgent.Options.MentorAnonymousIdentity.Shared; });
        var memory = host.Get<MentorMemoryService>();

        for (var i = 0; i < 5; i++)
            await memory.GetMemoryContextAsync();

        SharedBucketWarnings(host).Should().Be(0);
    }

    [Fact]
    public async Task Signed_in_sessions_are_not_warned()
    {
        await using var host = MentorHostFixture.Create(o => { o.UseMemoryContext = true; o.AnonymousIdentity = Novior.MentorAgent.Options.MentorAnonymousIdentity.Shared; });
        host.Auth.SignIn("ada");

        await host.Get<MentorMemoryService>().GetMemoryContextAsync();
        await ReadMemoryInAnotherSessionAsync(host);

        SharedBucketWarnings(host).Should().Be(0, "each has its own key");
    }

    [Fact]
    public async Task With_memory_off_there_is_no_bucket_to_share()
    {
        // The rate limiter resolves the same id; on its own that is not a privacy question.
        await using var host = MentorHostFixture.Create(o => { o.UseMemoryContext = false; o.AnonymousIdentity = Novior.MentorAgent.Options.MentorAnonymousIdentity.Shared; });

        await host.Get<MentorMemoryService>().GetUserIdAsync();
        using (var scope = host.Get<IServiceScopeFactory>().CreateScope())
            await scope.ServiceProvider.GetRequiredService<MentorMemoryService>().GetUserIdAsync();

        SharedBucketWarnings(host).Should().Be(0);
    }
}
