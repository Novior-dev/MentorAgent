using FluentAssertions;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Rag;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Novior.MentorAgent.Tests.Observability;

/// <summary>
/// One stack trace per cause, per turn (E1).
///
/// <para>
/// Measured live on rc.4 with an expired key: a single 401 produced <b>five</b> stack traces — RAG
/// retrieval, the safety classifier, the tool-filter embeddings, the router, the hosted-tool gate —
/// before the turn's own error. Every one of those components degraded correctly; each simply logged
/// what it caught, and the reader was left to work out that the five were one event.
/// </para>
/// </summary>
public sealed class TurnFaultLogTests
{
    private sealed class AlwaysFailsRagSource : IMentorRagSource
    {
        public Task<IReadOnlyList<MentorRagResult>> SearchAsync(
            string query, int maxResults, CancellationToken ct = default)
            => throw new HttpRequestException("HTTP 401 (invalid_api_key)");
    }

    private static int StackTraces(MentorHostFixture host)
        => host.Logs.Exceptions.Count;

    [Fact]
    public async Task The_same_failure_met_twice_is_logged_once_with_its_trace()
    {
        // RAG fails first, then the coordinator call fails with the same exception: the reader gets
        // one trace and one line saying the second is the same cause.
        var boom = new HttpRequestException("HTTP 401 (invalid_api_key)");

        await using var host = MentorHostFixture.Create(
            o => { o.UseRag = true; o.MemoryAutoCapture = false; },
            services => services.AddSingleton<IMentorRagSource>(new AlwaysFailsRagSource()));

        host.Chat!.ThrowOnNextCall = boom;

        await host.RunTurnAsync("quanti ordini sono aperti?");

        StackTraces(host).Should().Be(1, "five components, one cause, one trace");
        host.Logs.WarningText.Should().Contain("same cause as the first failure in this turn");
    }

    [Fact]
    public async Task Two_different_causes_keep_two_traces()
    {
        // The control. Collapsing everything would hide the second problem, which is the opposite
        // of the point.
        var log = new TurnFaultLog();
        var logger = LoggerFactory.Create(b => { }).CreateLogger("t");

        log.Warn(logger, new InvalidOperationException("first"), "a");
        log.Warn(logger, new HttpRequestException("second"), "b");

        // Asserted through the collector below rather than the null logger above.
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false);
        var faults = host.Get<TurnFaultLog>();
        var hostLogger = host.Get<ILoggerFactory>().CreateLogger("turn");

        faults.Reset();
        faults.Warn(hostLogger, new InvalidOperationException("one thing"), "first failure");
        faults.Warn(hostLogger, new HttpRequestException("another thing"), "second failure");

        host.Logs.Exceptions.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_new_turn_starts_a_new_cause()
    {
        // Otherwise the second turn's first real failure would be filed as a repeat of yesterday's.
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false);
        var faults = host.Get<TurnFaultLog>();
        var logger = host.Get<ILoggerFactory>().CreateLogger("turn");

        faults.Reset();
        faults.Warn(logger, new HttpRequestException("401"), "turn one");
        faults.Reset();
        faults.Warn(logger, new HttpRequestException("401"), "turn two");

        host.Logs.Exceptions.Should().HaveCount(2, "each turn explains itself from scratch");
    }

    [Fact]
    public async Task The_safety_check_failing_first_is_still_the_turns_one_trace()
    {
        // Found live on the packaged rc.5 (BUG-071). An expired key produced TWO full traces, not
        // one: the safety check runs near the top of SendMessageAsync, but Reset() sat further down
        // — after the guardrail, the rate limiter and the out-of-scope refusal — so the check's
        // cause was recorded and then wiped before RAG met the very same exception.
        await using var host = MentorHostFixture.Create(
            o => { o.EnableSafetyCheck = true; o.UseRag = true; o.MemoryAutoCapture = false; },
            services => services.AddSingleton<IMentorRagSource>(new AlwaysFailsRagSource()));

        host.Chat!.ThrowOnNextCall = new HttpRequestException("HTTP 401 (invalid_api_key)");

        await host.RunTurnAsync("quanti ordini sono aperti?");

        StackTraces(host).Should().Be(1,
            "the safety check is part of the turn, not a prologue to it");
        host.Logs.WarningText.Should().Contain("same cause as the first failure in this turn");
    }
}
