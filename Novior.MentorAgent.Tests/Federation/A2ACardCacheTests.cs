using FluentAssertions;
using Novior.MentorAgent.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Novior.MentorAgent.Tests.Federation;

/// <summary>
/// A remote agent's card, fetched once per process instead of once per visitor (C2).
///
/// <para>
/// Measured live on rc.4: every new browser circuit re-fetched every configured card before it could
/// answer its first message, and every circuit whose agent was down produced a <b>full stack trace</b>
/// of its own — the same one, for the same agent, for as long as the agent stayed down.
/// </para>
///
/// <para>
/// What is <b>not</b> shared is the client built from the card: it carries the remote conversation's
/// context id, and sharing that would splice two users' conversations into one.
/// </para>
/// </summary>
public sealed class A2ACardCacheTests
{
    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private static A2A.AgentCard Card(string name = "billing") => new() { Name = name, Version = "1.0.0" };

    [Fact]
    public async Task A_card_is_fetched_once_for_two_sessions()
    {
        var cache = new A2ACardCache(NullLogger<A2ACardCache>.Instance);
        var fetches = 0;

        Task<A2A.AgentCard> Fetch(CancellationToken _) { fetches++; return Task.FromResult(Card()); }

        await cache.GetAsync("k", "billing", "http://x", Fetch, default);
        await cache.GetAsync("k", "billing", "http://x", Fetch, default);

        fetches.Should().Be(1);
    }

    [Fact]
    public async Task The_card_is_fetched_again_after_the_ttl()
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var cache = new A2ACardCache(NullLogger<A2ACardCache>.Instance, clock) { Ttl = TimeSpan.FromMinutes(15) };
        var fetches = 0;

        Task<A2A.AgentCard> Fetch(CancellationToken _) { fetches++; return Task.FromResult(Card()); }

        await cache.GetAsync("k", "billing", "http://x", Fetch, default);
        clock.Advance(TimeSpan.FromMinutes(16));
        await cache.GetAsync("k", "billing", "http://x", Fetch, default);

        fetches.Should().Be(2, "a remote agent can change what it offers");
    }

    [Fact]
    public async Task A_dead_agent_is_not_dialled_again_inside_the_backoff()
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var cache = new A2ACardCache(NullLogger<A2ACardCache>.Instance, clock)
        { FailureBackoff = TimeSpan.FromMinutes(1) };
        var fetches = 0;

        Task<A2A.AgentCard> Fetch(CancellationToken _)
        {
            fetches++;
            throw new HttpRequestException("connection refused");
        }

        for (var i = 0; i < 3; i++)
            await Assert.ThrowsAnyAsync<Exception>(() => cache.GetAsync("k", "billing", "http://x", Fetch, default));

        fetches.Should().Be(1, "three sessions, one timeout — that is the whole point");
    }

    [Fact]
    public async Task After_the_backoff_the_next_session_retries()
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var cache = new A2ACardCache(NullLogger<A2ACardCache>.Instance, clock)
        { FailureBackoff = TimeSpan.FromMinutes(1) };
        var fetches = 0;

        Task<A2A.AgentCard> Fetch(CancellationToken _)
        {
            fetches++;
            throw new HttpRequestException("connection refused");
        }

        await Assert.ThrowsAnyAsync<Exception>(() => cache.GetAsync("k", "billing", "http://x", Fetch, default));
        clock.Advance(TimeSpan.FromMinutes(2));
        await Assert.ThrowsAnyAsync<Exception>(() => cache.GetAsync("k", "billing", "http://x", Fetch, default));

        fetches.Should().Be(2, "an agent that comes back must be picked up");
    }

    [Fact]
    public async Task A_recovered_agent_is_served_from_the_new_card()
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var cache = new A2ACardCache(NullLogger<A2ACardCache>.Instance, clock)
        { FailureBackoff = TimeSpan.FromSeconds(30) };
        var up = false;

        Task<A2A.AgentCard> Fetch(CancellationToken _)
            => up ? Task.FromResult(Card("billing-v2")) : throw new HttpRequestException("down");

        await Assert.ThrowsAnyAsync<Exception>(() => cache.GetAsync("k", "billing", "http://x", Fetch, default));
        up = true;
        clock.Advance(TimeSpan.FromMinutes(1));

        (await cache.GetAsync("k", "billing", "http://x", Fetch, default)).Name.Should().Be("billing-v2");
    }

    [Fact]
    public async Task Two_agents_that_share_a_url_but_not_their_headers_do_not_share_an_entry()
    {
        // The key carries the headers because they carry the credential: serving tenant A's card to
        // tenant B would be a cross-tenant leak dressed up as a cache hit.
        var cache = new A2ACardCache(NullLogger<A2ACardCache>.Instance);
        var fetches = 0;

        Task<A2A.AgentCard> Fetch(CancellationToken _) { fetches++; return Task.FromResult(Card()); }

        await cache.GetAsync("billing|http://x|Authorization=A", "billing", "http://x", Fetch, default);
        await cache.GetAsync("billing|http://x|Authorization=B", "billing", "http://x", Fetch, default);

        fetches.Should().Be(2);
    }
}
