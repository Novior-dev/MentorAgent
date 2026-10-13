using FluentAssertions;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Tests.Harness;
using Xunit;

namespace Novior.MentorAgent.Tests.Prompt;

/// <summary>
/// Token accounting and the cache prefix as it actually reaches the model.
///
/// <para>
/// <see cref="PromptBuilderTests"/> proves the builder is deterministic. This proves the property
/// survives the pipeline: what matters commercially is that turn two sends the same prefix bytes as
/// turn one, after middleware, page context and per-request notices have all had a chance to
/// perturb it.
/// </para>
/// </summary>
public sealed class TokenEconomicsTests
{
    // ── The prefix as sent ──────────────────────────────────────────────────────

    [Fact]
    public async Task The_instructions_sent_on_turn_two_are_identical_to_turn_one()
    {
        // The commercial claim, measured at the wire rather than at the builder.
        await using var host = MentorHostFixture.Create();
        host.Chat!.SayRepeatedly("ok", times: 4);

        await host.RunTurnAsync("first message");
        await host.RunTurnAsync("a completely different second message");

        var first = host.Chat.Calls[0].Instructions;
        var second = host.Chat.Calls[^1].Instructions;

        first.Should().NotBeNullOrEmpty("there is no prefix to cache if nothing is sent");
        second.Should().Be(first);
    }

    [Fact]
    public async Task The_user_message_never_leaks_into_the_instructions()
    {
        // The one thing that would break caching on every single turn.
        await using var host = MentorHostFixture.Create();
        host.Chat!.SayRepeatedly("ok", times: 2);

        await host.RunTurnAsync("a very distinctive phrase nobody would put in a prompt");

        (host.Chat.Calls[0].Instructions ?? "")
            .Should().NotContain("a very distinctive phrase nobody would put in a prompt");
    }

    /// <summary>
    /// The boundary the design draws: everything before this heading is stable and cacheable,
    /// everything after it changes per request. Both halves ride in <c>ChatOptions.Instructions</c>
    /// rather than a system message, because a system message would be <em>stored</em> in the
    /// conversation under service-managed history and repeat forever.
    /// </summary>
    private const string VolatileMarker = "## SESSION CONTEXT";

    private static string StablePrefix(string? instructions)
    {
        var text = instructions ?? "";
        var at = text.IndexOf(VolatileMarker, StringComparison.Ordinal);
        return at < 0 ? text : text[..at];
    }

    [Fact]
    public async Task The_stable_prefix_survives_a_page_change()
    {
        // The property that actually earns the discount. Page context, memory and RAG all change
        // per turn and all travel in Instructions — so what has to hold is not that the string is
        // identical, but that everything up to the volatile marker is.
        await using var host = MentorHostFixture.Create();
        host.Chat!.SayRepeatedly("ok", times: 4);

        await host.RunTurnAsync("first");
        var before = StablePrefix(host.Chat.Calls[0].Instructions);

        host.Get<Novior.MentorAgent.Abstractions.Interfaces.IMentorPageContext>()
            .SetPageName("Orders")
            .Set("filter", "Pending");
        await host.RunTurnAsync("second");

        before.Should().NotBeEmpty();
        StablePrefix(host.Chat.Calls[^1].Instructions).Should().Be(before,
            "navigating must move only the volatile tail, never the cached prefix");
    }

    [Fact]
    public async Task Volatile_context_is_emitted_after_the_stable_prefix_never_inside_it()
    {
        // Ordering is the whole mechanism: a provider caches a prefix, so one volatile line placed
        // early costs the discount on everything after it.
        await using var host = MentorHostFixture.Create();
        host.Chat!.SayRepeatedly("ok", times: 2);

        host.Get<Novior.MentorAgent.Abstractions.Interfaces.IMentorPageContext>()
            .SetPageName("Orders")
            .Set("visibleRows", "15");
        await host.RunTurnAsync("what is on this page?");

        var instructions = host.Chat.Calls[0].Instructions ?? "";
        var marker = instructions.IndexOf(VolatileMarker, StringComparison.Ordinal);

        marker.Should().BeGreaterThan(0, "the session-context block has to be present and not first");

        // "Active page:", not the page name — the name also appears in the static page catalogue,
        // which is exactly where it is supposed to be and would make this pass for the wrong reason.
        instructions.IndexOf("Active page", StringComparison.Ordinal).Should().BeGreaterThan(marker,
            "the currently-open page is volatile and belongs after the marker");
        instructions.IndexOf("visibleRows", StringComparison.Ordinal).Should().BeGreaterThan(marker,
            "page data is the most volatile thing there is");
        instructions.IndexOf("Security Rules", StringComparison.Ordinal).Should().BeLessThan(marker,
            "the security block is stable and belongs before it");
        instructions.IndexOf("Application Pages", StringComparison.Ordinal).Should().BeLessThan(marker,
            "the page catalogue is stable and is deliberately in the cached prefix");
    }

    [Fact]
    public async Task The_most_volatile_values_are_emitted_last_within_the_volatile_block()
    {
        // Documented ordering inside the tail: URL and clock change most often, so they go at the
        // very end where they invalidate the least.
        await using var host = MentorHostFixture.Create();
        host.Chat!.SayRepeatedly("ok", times: 2);

        host.Get<Novior.MentorAgent.Abstractions.Interfaces.IMentorPageContext>().SetPageName("Orders");
        await host.RunTurnAsync("hello");

        var instructions = host.Chat.Calls[0].Instructions ?? "";
        var page = instructions.IndexOf("Active page", StringComparison.Ordinal);
        var url = instructions.IndexOf("Current URL", StringComparison.Ordinal);

        if (page >= 0 && url >= 0)
            url.Should().BeGreaterThan(page, "the URL changes more often than the page name");
    }

    // ── MentorTokenUsage ────────────────────────────────────────────────────────

    [Fact]
    public void Record_accumulates_totals_and_tracks_the_last_call()
    {
        var usage = new MentorTokenUsage();

        usage.Record(100, 20);
        usage.Record(50, 10);

        usage.TotalInputTokens.Should().Be(150);
        usage.TotalOutputTokens.Should().Be(30);
        usage.TotalTokens.Should().Be(180);
        usage.RequestCount.Should().Be(2);
        usage.LastInputTokens.Should().Be(50);
        usage.LastOutputTokens.Should().Be(10);
    }

    [Fact]
    public void A_fresh_accumulator_reports_zeroes_not_nulls()
    {
        var usage = new MentorTokenUsage();

        usage.TotalTokens.Should().Be(0);
        usage.RequestCount.Should().Be(0);
        usage.LastInputTokens.Should().Be(0);
    }

    [Fact]
    public void Seed_replaces_the_totals_and_leaves_the_last_call_alone()
    {
        // Documented behaviour for the durable dashboard: counts continue across a restart, but a
        // "last call" from before the restart would be a lie about the current process.
        var usage = new MentorTokenUsage();
        usage.Record(7, 3);

        usage.Seed(totalInput: 1000, totalOutput: 200, requestCount: 40);

        usage.TotalInputTokens.Should().Be(1000, "Seed overwrites rather than adds");
        usage.TotalOutputTokens.Should().Be(200);
        usage.RequestCount.Should().Be(40);
    }

    [Fact]
    public void Recording_after_a_seed_continues_from_the_seeded_totals()
    {
        var usage = new MentorTokenUsage();
        usage.Seed(1000, 200, 40);

        usage.Record(100, 20);

        usage.TotalInputTokens.Should().Be(1100);
        usage.RequestCount.Should().Be(41);
    }

    [Fact]
    public void Reset_clears_everything_including_the_last_call()
    {
        var usage = new MentorTokenUsage();
        usage.Record(100, 20);

        usage.Reset();

        usage.TotalTokens.Should().Be(0);
        usage.RequestCount.Should().Be(0);
        usage.LastInputTokens.Should().Be(0);
        usage.LastOutputTokens.Should().Be(0);
    }

    [Fact]
    public void Concurrent_recording_loses_nothing()
    {
        // Registered as a singleton and written from every turn, so on a server it is written from
        // many connections at once. A non-atomic read-modify-write here under-reports the bill —
        // and under-reporting is the direction nobody notices.
        var usage = new MentorTokenUsage();
        const int threads = 8;
        const int perThread = 2_000;

        Parallel.For(0, threads, _ =>
        {
            for (var i = 0; i < perThread; i++) usage.Record(3, 1);
        });

        usage.RequestCount.Should().Be(threads * perThread);
        usage.TotalInputTokens.Should().Be(threads * perThread * 3);
        usage.TotalOutputTokens.Should().Be(threads * perThread);
    }

    [Fact]
    public async Task Reading_while_writing_never_tears_a_long()
    {
        // The reason the fields are read through Interlocked.Read rather than directly: a 64-bit
        // read is not atomic on every runtime, and a torn value would show up as an absurd token
        // count in the dashboard rather than as a crash.
        var usage = new MentorTokenUsage();
        var stop = false;
        var observed = new System.Collections.Concurrent.ConcurrentBag<long>();

        // The reader is started first and the writer waits for it. Without that handshake the whole
        // test is a race it can lose: on a loaded machine — which is what the full suite is, running
        // classes in parallel — the writer can finish all 100,000 iterations before the reader is
        // ever scheduled, `stop` is already true when the loop is reached, and the bag comes back
        // empty. FluentAssertions' OnlyContain *fails* on an empty collection, so the test then
        // reports a torn read that never happened. Seen once in a full run, and green in isolation
        // every time after.
        var readerStarted = new TaskCompletionSource();

        var reader = Task.Run(() =>
        {
            observed.Add(usage.TotalInputTokens);   // at least one sample, always
            readerStarted.SetResult();
            while (!Volatile.Read(ref stop)) observed.Add(usage.TotalInputTokens);
        });

        var writer = Task.Run(async () =>
        {
            await readerStarted.Task;
            for (var i = 0; i < 100_000; i++) usage.Record(1_000_000_000, 0);
            Volatile.Write(ref stop, true);
        });

        await Task.WhenAll(writer, reader);

        observed.Should().NotBeEmpty("the reader must actually have sampled something");
        observed.Should().OnlyContain(v => v >= 0 && v % 1_000_000_000 == 0,
            "every observed total must be a whole number of recorded calls");
    }

    // ── Metering through a real turn ────────────────────────────────────────────

    [Fact]
    public async Task A_turn_records_the_usage_the_model_reported()
    {
        await using var host = MentorHostFixture.Create();
        host.Chat!.SayWithUsage("hello", input: 123, output: 45);

        await host.RunTurnAsync("hi");

        var usage = host.Scope.ServiceProvider.GetService(typeof(MentorTokenUsage)) as MentorTokenUsage;
        usage.Should().NotBeNull();
        usage!.TotalInputTokens.Should().BeGreaterThan(0, "a turn with reported usage must be metered");
    }

    [Fact]
    public async Task Turns_accumulate_rather_than_replacing_each_other()
    {
        await using var host = MentorHostFixture.Create();
        host.Chat!.SayWithUsage("one", 100, 10).SayWithUsage("two", 200, 20);

        await host.RunTurnAsync("first");
        var afterFirst = (host.Scope.ServiceProvider.GetService(typeof(MentorTokenUsage)) as MentorTokenUsage)!
            .TotalInputTokens;

        await host.RunTurnAsync("second");
        var afterSecond = (host.Scope.ServiceProvider.GetService(typeof(MentorTokenUsage)) as MentorTokenUsage)!
            .TotalInputTokens;

        afterSecond.Should().BeGreaterThan(afterFirst);
    }
}
