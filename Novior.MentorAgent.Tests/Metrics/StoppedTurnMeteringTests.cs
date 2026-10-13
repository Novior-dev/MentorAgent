using FluentAssertions;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Middleware;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.AI;
using Xunit;

namespace Novior.MentorAgent.Tests.Metrics;

/// <summary>
/// What the dashboard records for a turn the user stopped.
///
/// <para>
/// Found in Part D by arithmetic, not by suspicion: after three live turns the admin dashboard
/// reported <b>two</b> chat requests. The missing one was the turn I had stopped mid-stream. The
/// mechanism is in plain sight once you look — <see cref="TokenUsageChatClient"/> calls its
/// <c>Record</c> <em>after</em> the <c>await foreach</c>, so a cancellation propagates out of the
/// loop and nothing at all is written: not the tokens, not the latency, not even the request count.
/// The same happens if a consumer simply stops enumerating early.
/// </para>
///
/// <para>
/// The provider billed for that call regardless — and it billed for the <b>whole input prompt</b>,
/// which on this sample is several thousand tokens, plus whatever was generated before the stream
/// was cut. So the omission runs in the same direction as BUG-030: spend that happened, missing from
/// the place built to show spend.
/// </para>
///
/// <para>
/// The tokens genuinely cannot be known — providers send usage in the final update, which a cancelled
/// stream never delivers, and counting them client-side would mean tokenising. So this suite pins the
/// honest answer rather than a convenient one: <b>count the request, mark its usage as unmeasured,
/// and invent no numbers.</b> Recording zero tokens would be a fabricated measurement that quietly
/// drags every per-request average down; that is the failure B13 and BUG-020 already established a
/// rule against.
/// </para>
/// </summary>
public sealed class StoppedTurnMeteringTests
{
    private const string Model = "gpt-4.1";

    private sealed record Rig(IChatClient Pipeline, MentorMetrics Metrics);

    private static Rig Build(TimeSpan chunkDelay)
    {
        var chat = new FakeChatClient { ModelId = Model, ChunkDelay = chunkDelay };
        chat.SayWithUsage("one two three four five six seven eight nine ten eleven twelve", 500, 40);

        var metrics = new MentorMetrics(
            new MentorTokenUsage(),
            Microsoft.Extensions.Options.Options.Create(
                new MentorOptions { AppName = "x", ScanAssemblies = [] }))
        { ModelId = Model };

        var pipeline = new TokenUsageChatClient(
            chat, new MentorTokenUsage(), metrics,
            new TestLoggerProvider(new TestLogCollector()).CreateLogger(nameof(TokenUsageChatClient)));

        return new Rig(pipeline, metrics);
    }

    private static MentorModelUsage BucketOf(MentorMetrics metrics)
        => metrics.GetSnapshot().Models.First(m => m.ModelId == Model);

    /// <summary>
    /// Streams until <paramref name="stopAfter"/> updates have arrived, then cancels — the Stop
    /// button, in miniature. The 10-second guard is the standing rule: a test that drives a stream
    /// must be able to end it.
    /// </summary>
    private static async Task<Rig> StopMidStreamAsync(int stopAfter = 2)
    {
        var rig = Build(TimeSpan.FromMilliseconds(30));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var seen = 0;
        try
        {
            await foreach (var _ in rig.Pipeline.GetStreamingResponseAsync(
                               [new ChatMessage(ChatRole.User, "write me a long guide")],
                               cancellationToken: cts.Token))
            {
                if (++seen >= stopAfter) await cts.CancelAsync();
            }
        }
        catch (OperationCanceledException) { /* the point of the test */ }

        seen.Should().BeGreaterThan(0, "the stream has to start before stopping it means anything");
        return rig;
    }

    // ── The defect ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_stopped_turn_still_counts_as_a_request()
    {
        // It left, the provider served part of it, and the bill will include it. A dashboard that
        // omits it is not being cautious — it is wrong about how many calls were made.
        var rig = await StopMidStreamAsync();

        BucketOf(rig.Metrics).RequestCount.Should().Be(1,
            "the request reached the provider and was billed, whatever the user did afterwards");
    }

    [Fact]
    public async Task A_stopped_turn_is_marked_as_having_unmeasured_usage()
    {
        // Counting the request without saying its tokens are unknown would be the other half of the
        // same lie: an operator would read a real request with zero tokens and conclude it was free.
        var rig = await StopMidStreamAsync();

        BucketOf(rig.Metrics).RequestsWithoutUsage.Should().Be(1,
            "the usage chunk never arrived, and the dashboard has to be able to say so");
    }

    // ── What must not be invented ───────────────────────────────────────────────

    [Fact]
    public async Task A_stopped_turn_reports_no_token_counts_it_never_received()
    {
        // The scripted response carries 500/40, and the fake would have delivered them in the final
        // update. The stream was cut first, so those numbers were never observed and must not appear
        // — neither guessed from the text nor borrowed from the response object.
        var rig = await StopMidStreamAsync();

        var bucket = BucketOf(rig.Metrics);
        bucket.InputTokens.Should().Be(0);
        bucket.OutputTokens.Should().Be(0);
    }

    [Fact]
    public async Task Latency_is_recorded_because_it_was_actually_measured()
    {
        // The distinction that makes the rule coherent: the clock ran, so the elapsed time is a
        // measurement rather than a guess, and withholding it would lose information we hold.
        var rig = await StopMidStreamAsync();

        BucketOf(rig.Metrics).AvgLatencyMs.Should().NotBeNull(
            "time to cancellation is measured, unlike the token counts");
    }

    // ── The control ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_completed_stream_records_its_real_usage_and_is_not_marked_unmeasured()
    {
        // A fix that flagged every streaming call would make the new field meaningless.
        var rig = Build(TimeSpan.Zero);

        await foreach (var _ in rig.Pipeline.GetStreamingResponseAsync(
                           [new ChatMessage(ChatRole.User, "hello")])) { }

        var bucket = BucketOf(rig.Metrics);
        bucket.RequestCount.Should().Be(1);
        bucket.InputTokens.Should().Be(500);
        bucket.OutputTokens.Should().Be(40);
        bucket.RequestsWithoutUsage.Should().Be(0);
    }
}
