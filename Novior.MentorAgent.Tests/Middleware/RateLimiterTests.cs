using FluentAssertions;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.App;
using Xunit;

namespace Novior.MentorAgent.Tests.Middleware;

/// <summary>
/// The per-user rate limiter — a sliding-window counter registered as a singleton, so it is written
/// from every connection at once.
///
/// <para>
/// The interesting cases are the two ends. A limiter that is off must be genuinely free (no
/// bookkeeping, no ceiling), and a limiter that is on must not let a user slip an extra message
/// through at the window boundary. Both are the sort of thing that works in a hand test with one
/// user and fails under load.
/// </para>
/// </summary>
public sealed class RateLimiterTests
{
    private static MentorRateLimiter Limiter(int perUser, int windowSecs = 60)
        => new(Microsoft.Extensions.Options.Options.Create(new MentorOptions
        {
            AppName = "RateLimitTest",
            ScanAssemblies = [typeof(TestOrderService).Assembly],
            RateLimitPerUser = perUser,
            RateLimitWindowSecs = windowSecs,
        }));

    // ── Disabled ────────────────────────────────────────────────────────────────

    [Fact]
    public void A_limit_of_zero_disables_the_limiter()
    {
        // Documented as the off switch. It has to be genuinely unlimited, not "zero messages".
        var limiter = Limiter(0);

        for (var i = 0; i < 1_000; i++)
            limiter.CheckAndIncrement("ada").Allowed.Should().BeTrue();
    }

    [Fact]
    public void A_disabled_limiter_reports_an_unbounded_remainder()
    {
        Limiter(0).CheckAndIncrement("ada").Remaining.Should().Be(int.MaxValue);
    }

    [Fact]
    public void A_negative_limit_also_disables_it_rather_than_blocking_everything()
    {
        // Reachable from configuration binding. Treating -1 as "minus one messages allowed" would
        // make the assistant refuse every message with no explanation.
        Limiter(-5).CheckAndIncrement("ada").Allowed.Should().BeTrue();
    }

    // ── Counting ────────────────────────────────────────────────────────────────

    [Fact]
    public void The_first_message_is_allowed_and_the_remainder_counts_down()
    {
        var limiter = Limiter(3);

        limiter.CheckAndIncrement("ada").Should().Be((true, 2));
        limiter.CheckAndIncrement("ada").Should().Be((true, 1));
        limiter.CheckAndIncrement("ada").Should().Be((true, 0));
    }

    [Fact]
    public void The_message_after_the_limit_is_refused()
    {
        // The boundary: exactly `limit` messages get through, the next does not. Off-by-one here
        // means either a free extra message or one fewer than the developer configured.
        var limiter = Limiter(3);
        for (var i = 0; i < 3; i++) limiter.CheckAndIncrement("ada");

        limiter.CheckAndIncrement("ada").Should().Be((false, 0));
    }

    [Fact]
    public void A_refused_message_does_not_consume_further_budget()
    {
        // Repeatedly hitting a blocked limiter must not push the window out or change the count.
        var limiter = Limiter(2);
        limiter.CheckAndIncrement("ada");
        limiter.CheckAndIncrement("ada");

        for (var i = 0; i < 5; i++)
            limiter.CheckAndIncrement("ada").Should().Be((false, 0));
    }

    [Fact]
    public void A_limit_of_one_allows_exactly_one_message()
    {
        var limiter = Limiter(1);

        limiter.CheckAndIncrement("ada").Should().Be((true, 0));
        limiter.CheckAndIncrement("ada").Allowed.Should().BeFalse();
    }

    // ── Per user ────────────────────────────────────────────────────────────────

    [Fact]
    public void Users_have_separate_budgets()
    {
        // The limiter is a singleton shared across every connection, so this is the property that
        // stops one busy user silencing everybody else.
        var limiter = Limiter(2);
        limiter.CheckAndIncrement("ada");
        limiter.CheckAndIncrement("ada");

        limiter.CheckAndIncrement("grace").Should().Be((true, 1));
    }

    [Fact]
    public void User_ids_are_matched_exactly()
    {
        var limiter = Limiter(1);
        limiter.CheckAndIncrement("ada");

        limiter.CheckAndIncrement("Ada").Allowed.Should().BeTrue(
            "the id comes from the identity provider and is not case-folded anywhere else either");
    }

    // ── The window ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_budget_returns_when_the_window_expires()
    {
        // A one-second window keeps this honest without making the suite slow.
        var limiter = Limiter(perUser: 1, windowSecs: 1);
        limiter.CheckAndIncrement("ada");
        limiter.CheckAndIncrement("ada").Allowed.Should().BeFalse();

        await Task.Delay(1_100);

        limiter.CheckAndIncrement("ada").Allowed.Should().BeTrue(
            "the window is the whole point — a limiter that never resets is a ban");
    }

    [Fact]
    public async Task The_window_restarts_from_the_first_message_after_the_reset()
    {
        var limiter = Limiter(perUser: 2, windowSecs: 1);
        limiter.CheckAndIncrement("ada");
        limiter.CheckAndIncrement("ada");

        await Task.Delay(1_100);

        limiter.CheckAndIncrement("ada").Should().Be((true, 1),
            "the counter starts again at one, not at whatever it reached before");
    }

    [Fact]
    public void An_unknown_user_has_no_reset_to_wait_for()
    {
        Limiter(5).GetSecondsUntilReset("nobody").Should().Be(0);
    }

    [Fact]
    public void The_reset_countdown_is_within_the_configured_window()
    {
        var limiter = Limiter(perUser: 5, windowSecs: 60);
        limiter.CheckAndIncrement("ada");

        limiter.GetSecondsUntilReset("ada").Should().BeInRange(0, 60);
    }

    [Fact]
    public async Task The_reset_countdown_reaches_zero_rather_than_going_negative()
    {
        // The subtraction can produce a negative TimeSpan once the window has passed, and a
        // negative "try again in N seconds" shown to a user is nonsense.
        var limiter = Limiter(perUser: 1, windowSecs: 1);
        limiter.CheckAndIncrement("ada");

        await Task.Delay(1_100);

        limiter.GetSecondsUntilReset("ada").Should().Be(0);
    }

    // ── Concurrency and housekeeping ────────────────────────────────────────────

    [Fact]
    public void Concurrent_callers_never_exceed_the_limit_in_total()
    {
        // The check and the increment have to be one atomic step. Splitting them lets N threads
        // all read "one left" and all be allowed — which is exactly how a rate limiter fails in
        // production and never in a hand test.
        const int limit = 50;
        var limiter = Limiter(limit);
        var allowed = 0;

        Parallel.For(0, 500, _ =>
        {
            if (limiter.CheckAndIncrement("ada").Allowed) Interlocked.Increment(ref allowed);
        });

        allowed.Should().Be(limit);
    }

    [Fact]
    public void Concurrent_callers_on_different_ids_each_get_their_own_budget()
    {
        var limiter = Limiter(2);
        var allowed = 0;

        Parallel.For(0, 100, i =>
        {
            if (limiter.CheckAndIncrement($"user-{i}").Allowed) Interlocked.Increment(ref allowed);
        });

        allowed.Should().Be(100, "a hundred distinct users each have their first message");
    }

    [Fact]
    public async Task Expired_counters_are_cleaned_up_so_the_dictionary_does_not_grow_forever()
    {
        // A long-running server sees a new id per visitor. Without the lazy cleanup the dictionary
        // is a slow memory leak that only shows up weeks into a deployment.
        var limiter = Limiter(perUser: 5, windowSecs: 1);
        for (var i = 0; i < 100; i++) limiter.CheckAndIncrement($"visitor-{i}");

        await Task.Delay(1_100);
        limiter.Cleanup();

        // Nothing is observable except through behaviour: every counter is gone, so every visitor
        // starts fresh.
        limiter.CheckAndIncrement("visitor-0").Should().Be((true, 4));
        limiter.GetSecondsUntilReset("visitor-50").Should().Be(0);
    }

    [Fact]
    public void Cleanup_leaves_live_counters_alone()
    {
        var limiter = Limiter(perUser: 3, windowSecs: 60);
        limiter.CheckAndIncrement("ada");
        limiter.CheckAndIncrement("ada");

        limiter.Cleanup();

        limiter.CheckAndIncrement("ada").Should().Be((true, 0),
            "a counter inside its window must survive housekeeping");
    }
}
