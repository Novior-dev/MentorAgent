using FluentAssertions;
using Novior.MentorAgent.Rag;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Novior.MentorAgent.Tests.Boundaries;

/// <summary>
/// Part C2.4 — the numeric options at their edges.
///
/// <para>
/// Eighteen options on <c>MentorOptions</c> carry a number, and the interesting values are never the
/// typical ones: they are zero, negative, the exact threshold, and one past it. Several of these are
/// documented as having a *special meaning* at zero — disable the limiter, disable the filter — which
/// makes "0" a mode rather than a quantity, and a mode that nothing exercised.
/// </para>
///
/// <para>
/// The B-suites each pinned the boundaries of their own feature (B3b did four compaction thresholds,
/// B12 the non-positive hosted-tool cap, B13 retention). What is left is the options nobody owned,
/// and the one question a per-feature suite is badly placed to ask: <b>is the default value itself
/// sensible for the way the feature is normally used?</b>
/// </para>
/// </summary>
public sealed class NumericBoundaryTests
{
    private static string Warnings(MentorHostFixture host)
        => string.Join("\n", host.Logs.Records
            .Where(r => r.Level >= LogLevel.Warning)
            .Select(r => r.Message));

    // ── RagMinScore: the default is a threshold, and thresholds have a scale ────

    [Fact]
    public async Task A_threshold_that_discards_every_document_says_so()
    {
        // RagMinScore defaults to 2. The option's own documentation says the scale depends on the
        // IMentorRagSource: integer-ish for keyword search, but **[0,1] for vector/cosine search**,
        // where it recommends 0.5 to 0.75. So on any cosine source — which is what a vector store
        // is, and what most people mean by RAG — no document can ever reach the default threshold.
        //
        // The search still runs and is still paid for. Every document is then dropped, and the only
        // trace is a Debug line nobody reads in production. This is BUG-016's lesson again: an
        // unconsulted knowledge base and a working one produce the same fluent answer.
        await using var host = MentorHostFixture.Create(configure: o =>
        {
            o.UseRag = true;
            o.ShowRagSources = true;
        }, configureServices: s => s.AddSingleton<IMentorRagSource>(
            new ScriptedRagSource()
                .Returns("Refunds are processed within 14 days.", 0.82f, "Refund policy")
                .Returns("Shipping is free above 50 euro.", 0.77f, "Shipping")));

        host.Chat!.Say("ok");
        await host.RunTurnAsync("what is the refund policy");

        var warned = Warnings(host);

        warned.Should().Contain("RagMinScore",
            "a threshold that discarded everything the source returned is a configuration mistake, " +
            "not a quiet no-op");
        warned.Should().Contain("0.82",
            "the highest score actually seen is what tells the developer they are on the wrong scale");
    }

    [Fact]
    public async Task A_threshold_that_lets_something_through_stays_quiet()
    {
        // The control. A warning that fired whenever any document was dropped would be noise on
        // every single turn — filtering is the feature.
        await using var host = MentorHostFixture.Create(configure: o =>
        {
            o.UseRag = true;
            o.RagMinScore = 0.5f;
        }, configureServices: s => s.AddSingleton<IMentorRagSource>(
            new ScriptedRagSource()
                .Returns("Refunds are processed within 14 days.", 0.82f, "Refund policy")
                .Returns("Unrelated marketing copy.", 0.11f, "Newsletter")));

        host.Chat!.Say("ok");
        await host.RunTurnAsync("what is the refund policy");

        Warnings(host).Should().NotContain("RagMinScore");
    }

    [Fact]
    public async Task A_threshold_on_the_sources_own_scale_that_discards_an_off_topic_query_stays_quiet()
    {
        // Found live on rc.4 (BUG-065): RagMinScore = 0.35 against a cosine source, an off-topic
        // question, and the warning fired — "can never reach a threshold above 1", about a threshold
        // of 0.35, once for every visitor's first off-topic question. Discarding everything is the
        // right answer to an off-topic question; only a threshold the source's scale cannot reach
        // is the mistake the warning exists for.
        await using var host = MentorHostFixture.Create(configure: o =>
        {
            o.UseRag = true;
            o.RagMinScore = 0.5f;
        }, configureServices: s => s.AddSingleton<IMentorRagSource>(
            new ScriptedRagSource()
                .Returns("Refunds are processed within 14 days.", 0.28f, "Refund policy")
                .Returns("Shipping is free above 50 euro.", 0.21f, "Shipping")));

        host.Chat!.Say("ok");
        await host.RunTurnAsync("scrivimi una poesia sul mare");

        Warnings(host).Should().NotContain("RagMinScore");
    }

    [Fact]
    public async Task A_search_that_found_nothing_at_all_is_not_blamed_on_the_threshold()
    {
        // "No documents" and "no documents that cleared the bar" are different problems with
        // different fixes, and pointing at the threshold when the source simply had no match would
        // send the reader to change a setting that was never involved.
        await using var host = MentorHostFixture.Create(configure: o => o.UseRag = true,
            configureServices: s => s.AddSingleton<IMentorRagSource>(new ScriptedRagSource()));

        host.Chat!.Say("ok");
        await host.RunTurnAsync("what is the refund policy");

        Warnings(host).Should().NotContain("RagMinScore");
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    public async Task Disabling_the_filter_keeps_everything_including_the_dross(float minScore)
    {
        // Documented: "Set to 0 to disable filtering". Negative goes the same way rather than
        // becoming a threshold nothing can fail — and with the filter off there is nothing for the
        // discard warning to fire about.
        var source = new ScriptedRagSource()
            .Returns("Barely related.", 0.01f, "Fringe")
            .Returns("Refunds within 14 days.", 0.82f, "Refund policy");

        await using var host = MentorHostFixture.Create(configure: o =>
        {
            o.UseRag = true;
            o.RagMinScore = minScore;
            o.ShowRagSources = true;
        }, configureServices: s => s.AddSingleton<IMentorRagSource>(source));

        host.Chat!.Say("ok");
        await host.RunTurnAsync("what is the refund policy");

        var prompt = host.Chat!.LastCall!.Instructions ?? "";
        prompt.Should().Contain("Barely related.", "filtering is off, so nothing is discarded");
        Warnings(host).Should().NotContain("RagMinScore");
    }

    [Fact]
    public async Task A_document_exactly_on_the_threshold_is_kept()
    {
        // The filter is `>=`. "Exceeds" and "reaches" are one character apart in the source and a
        // whole release apart in consequence — the same boundary B14 pinned for the eval gates.
        await using var host = MentorHostFixture.Create(configure: o =>
        {
            o.UseRag = true;
            o.RagMinScore = 0.75f;
        }, configureServices: s => s.AddSingleton<IMentorRagSource>(
            new ScriptedRagSource().Returns("Exactly at the bar.", 0.75f, "Edge")));

        host.Chat!.Say("ok");
        await host.RunTurnAsync("anything");

        (host.Chat!.LastCall!.Instructions ?? "").Should().Contain("Exactly at the bar.");
    }

    // ── RagResultCount ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(50)]
    public async Task The_result_count_is_what_the_source_is_asked_for(int count)
    {
        // The source is a third party's code and the count is the only budget MentorAgent gives it.
        // Asking for the wrong number is invisible from the outside — a vector store charges per
        // query, not per result.
        var source = new ScriptedRagSource().Returns("A document.", 9f);

        await using var host = MentorHostFixture.Create(configure: o =>
        {
            o.UseRag = true;
            o.RagResultCount = count;
        }, configureServices: s => s.AddSingleton<IMentorRagSource>(source));

        host.Chat!.Say("ok");
        await host.RunTurnAsync("anything");

        source.Searches.Should().ContainSingle().Which.MaxResults.Should().Be(count);
    }

    // ── MaxMessageLength ───────────────────────────────────────────────────────

    [Fact]
    public async Task A_message_of_exactly_the_maximum_length_is_accepted()
    {
        // At the boundary, not near it — the standing rule from the SignalR size limit.
        await using var host = MentorHostFixture.Create(configure: o => o.MaxMessageLength = 100);
        host.Chat!.Say("ok");

        await host.RunTurnAsync(new string('a', 100));

        host.Chat!.CallCount.Should().BeGreaterThan(0, "exactly the limit is within the limit");
    }

    [Fact]
    public async Task One_character_past_the_maximum_is_refused_before_the_model_is_called()
    {
        // Refusing after the call would cost a request and still refuse.
        await using var host = MentorHostFixture.Create(configure: o => o.MaxMessageLength = 100);
        host.Chat!.Say("ok");

        await host.RunTurnAsync(new string('a', 101));

        host.Chat!.CallCount.Should().Be(0);
    }

    // ── RateLimitPerUser ───────────────────────────────────────────────────────

    [Fact]
    public async Task A_limit_of_zero_means_no_limit_rather_than_no_messages()
    {
        // The default, and a mode rather than a quantity: read literally, zero messages per window
        // would refuse the very first turn of a default installation.
        await using var host = MentorHostFixture.Create(configure: o => o.RateLimitPerUser = 0);
        host.Chat!.Say("ok");

        for (var i = 0; i < 5; i++)
            await host.RunTurnAsync($"message {i}");

        host.Chat!.CallCount.Should().Be(5);
    }

    [Fact]
    public async Task A_limit_of_one_allows_exactly_one()
    {
        await using var host = MentorHostFixture.Create(configure: o =>
        {
            o.RateLimitPerUser = 1;
            o.RateLimitWindowSecs = 60;
        });
        host.Chat!.Say("ok");

        await host.RunTurnAsync("first");
        await host.RunTurnAsync("second");

        host.Chat!.CallCount.Should().Be(1, "the second message is inside the same window");
    }

    // ── MaxImagesPerMessage / MaxImageBytes ────────────────────────────────────

    [Fact]
    public void The_image_defaults_are_the_documented_ones()
    {
        // Both are quoted as fact in the README's option table and sized against the SignalR
        // message ceiling, which B19 pins from the other side.
        var options = new Novior.MentorAgent.Options.MentorOptions { AppName = "x", ScanAssemblies = [] };

        options.MaxImageBytes.Should().Be(4 * 1024 * 1024);
        options.MaxImagesPerMessage.Should().Be(4);
    }

    // ── VoiceRate ──────────────────────────────────────────────────────────────

    [Fact]
    public void The_voice_rate_default_is_normal_speed()
    {
        // The JS side treats a non-positive rate as "use 1.0" (pinned in dom.test.js); the default
        // has to be a real speed rather than zero, or every host would rely on that fallback.
        new Novior.MentorAgent.Options.MentorOptions { AppName = "x", ScanAssemblies = [] }
            .VoiceRate.Should().Be(1.0);
    }

    // ── MaxSessionMessages ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_non_positive_history_cap_does_not_take_the_assistant_down(int cap)
    {
        // Zero is a natural thing to write here, because two neighbouring options on the same class
        // use it to mean "no limit": RateLimitPerUser = 0 disables the limiter and
        // MaxHostedToolCallsPerSession = 0 is documented as "no cap". It is also what configuration
        // binding produces from a missing or empty value.
        //
        // Whatever it ends up meaning, it must not reach the framework's reducer unguarded: a
        // coordinator that fails to build returns **no error to the caller** (BUG-011), so the
        // assistant simply never answers and the only trace names an internal type.
        await using var host = MentorHostFixture.Create(configure: o => o.MaxSessionMessages = cap);
        host.Chat!.Say("ok");

        var error = await host.RunTurnAsync("hello");

        // Checked first, because it names the cause: the standing rule is to look for this line
        // before theorising about anything else when a turn produces nothing.
        string.Join("\n", host.Logs.Records.Select(r => r.Message))
            .Should().NotContain("Initialization failed",
                "a coordinator that fails to build answers nothing and reports nothing (BUG-011)");

        error.Should().BeNull();
        host.Chat!.CallCount.Should().Be(1, "the turn has to reach the model");
    }

    [Fact]
    public async Task A_positive_history_cap_is_accepted_as_given()
    {
        // The control for the pair above.
        await using var host = MentorHostFixture.Create(configure: o => o.MaxSessionMessages = 4);
        host.Chat!.Say("ok");

        (await host.RunTurnAsync("hello")).Should().BeNull();
    }

    // ── MaxHostedToolCallsPerSession ───────────────────────────────────────────

    [Fact]
    public void The_hosted_call_cap_is_documented_as_off_at_zero()
    {
        // Documented as "0 (default) means no cap", and the code guards every use with `> 0`. Pinned
        // because it is the opposite reading from MaxSessionMessages above, in the same class.
        new Novior.MentorAgent.Options.MentorOptions { AppName = "x", ScanAssemblies = [] }
            .MaxHostedToolCallsPerSession.Should().Be(0);
    }

    // ── ToolFilterMaxTools ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task A_non_positive_tool_cap_does_not_leave_the_model_with_no_tools(int cap)
    {
        // Filtering exists to send fewer tools, not none. A cap of zero taken literally would strip
        // every tool from every call — and a model with no tools just talks, which is the invisible
        // failure BUG-013 was.
        await using var host = MentorHostFixture.Create(withEmbeddings: true, configure: o =>
        {
            o.EnableToolFiltering = true;
            o.ToolFilterMaxTools = cap;
        });
        host.Chat!.Say("ok");

        await host.RunTurnAsync("how many open orders");

        host.LastToolsSent.Should().NotBeEmpty(
            "a non-positive cap must not be read as 'send zero tools'");
    }

    // ── MemoryContextCount ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_non_positive_memory_count_injects_nothing_and_still_answers(int count)
    {
        // Zero is the honest way to say "remember, but do not inject" — the tool stays, the block
        // does not. What it must not do is throw out of Take(-1), which is an argument exception on
        // the coordinator's path.
        await using var host = MentorHostFixture.Create(configure: o =>
        {
            o.UseMemoryContext = true;
            o.MemoryContextCount = count;
        });
        host.Chat!.Say("ok");

        (await host.RunTurnAsync("hello")).Should().BeNull();
    }

    // ── The remaining defaults, as published ───────────────────────────────────

    [Fact]
    public void Every_documented_numeric_default_is_the_one_the_class_carries()
    {
        // The README's option table quotes each of these as fact and hosts size real decisions
        // around them — a window, a retention period, a cap. Cheap to pin, and the only thing that
        // would catch a default changed in passing.
        var o = new Novior.MentorAgent.Options.MentorOptions { AppName = "x", ScanAssemblies = [] };

        o.MaxSessionMessages.Should().Be(50);
        o.MemoryContextCount.Should().Be(10);
        o.RagResultCount.Should().Be(5);
        o.MaxMessageLength.Should().Be(4000);
        o.RateLimitPerUser.Should().Be(0);
        o.RateLimitWindowSecs.Should().Be(60);
        o.CompactionTokenThreshold.Should().Be(4000);
        o.CompactionMaxTurns.Should().Be(8);
        o.ToolFilterMaxTools.Should().Be(12);
        o.ToolFilterMinScore.Should().Be(0.35f);
        o.RoutingThreshold.Should().Be(0.35f);
        o.MetricsRetention.Should().Be(TimeSpan.FromDays(7));
        o.MetricsPersistenceInterval.Should().Be(TimeSpan.FromSeconds(30));
    }
}
