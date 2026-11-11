using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using FluentAssertions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Rag;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Novior.MentorAgent.Tests.Rag;

/// <summary>
/// Story 195 — a knowledge-base search that failed left a Warning line and nothing else to monitor. It is now counted on
/// <c>mentoragent.rag_failures</c>, with the source's name and the exception's type, never its message; its
/// <c>mentor.rag.search</c> span says the same; and the Warning names the source. A search that works, or a Stop, counts
/// nothing, and without <c>EnableObservability</c> there is no measurement.
/// </summary>
public sealed class RagFailureSignalTests
{
    private const string Secret = "connection string Server=db;Password=hunter2";

    /// <summary>What MentorAgent's instruments and spans of one test recorded.</summary>
    private sealed class Recorded : IDisposable
    {
        private readonly MeterListener _meters;
        private readonly ActivityListener _spans;
        private readonly ConcurrentQueue<(string Instrument, Dictionary<string, object?> Tags)> _measured = new();
        private readonly ConcurrentQueue<Activity> _stopped = new();

        internal string Source { get; } = "MentorAgent.RagFailure." + Guid.NewGuid().ToString("N");

        internal IReadOnlyList<Dictionary<string, object?>> Failures
            => [.. _measured.Where(m => m.Instrument == "mentoragent.rag_failures").Select(m => m.Tags)];

        internal IReadOnlyList<Activity> Searches => [.. _stopped.Where(a => a.OperationName == "mentor.rag.search")];

        internal Recorded()
        {
            _meters = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == Source) listener.EnableMeasurementEvents(instrument);
                },
            };
            _meters.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            {
                var read = new Dictionary<string, object?>();
                foreach (var tag in tags) read[tag.Key] = tag.Value;
                _measured.Enqueue((instrument.Name, read));
            });
            _meters.Start();

            _spans = new ActivityListener
            {
                ShouldListenTo = s => s.Name == Source,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = _stopped.Enqueue,
            };
            ActivitySource.AddActivityListener(_spans);
        }

        public void Dispose()
        {
            _meters.Dispose();
            _spans.Dispose();
        }
    }

    private static MentorHostFixture Host(Recorded telemetry, ScriptedRagSource source, bool observability = true,
        bool resolver = false)
        => MentorHostFixture.Create(o =>
        {
            o.UseRag = true;
            o.MemoryAutoCapture = false;
            o.CheckAnnouncedActions = false;   // its question would take the reply scripted for the turn
            o.EnableObservability = observability;
            o.ObservabilitySourceName = telemetry.Source;
        }, s =>
        {
            s.AddSingleton<IMentorRagSource>(source);
            if (resolver) s.AddMentorTenantResolver<TestTenantResolver>();
        });

    private static readonly string SourceName = typeof(ScriptedRagSource).FullName!;

    [Fact]
    public async Task A_source_that_throws_is_counted_with_its_name_and_the_exceptions_type_never_its_message()
    {
        using var telemetry = new Recorded();
        var source = new ScriptedRagSource { ThrowOnNextSearch = new TimeoutException(Secret) };
        await using var host = Host(telemetry, source);
        host.Chat!.Say("Ok.");

        (await host.RunTurnAsync("what is the return policy?")).Should().BeNull("the turn goes on without documents");

        var failure = telemetry.Failures.Should().ContainSingle().Subject;
        failure["mentoragent.rag.source"].Should().Be(SourceName);
        failure["mentoragent.error.type"].Should().Be(typeof(TimeoutException).FullName);
        failure.Values.OfType<string>().Should().NotContain(v => v.Contains("hunter2"), "never the exception's message");
        failure.Should().NotContainKey("mentoragent.tenant", "no resolver, no tenant");
    }

    [Fact]
    public async Task A_source_that_answers_with_no_list_is_counted_by_name()
    {
        using var telemetry = new Recorded();
        var source = new ScriptedRagSource { ReturnNull = true };
        await using var host = Host(telemetry, source);
        host.Chat!.Say("Ok.");

        await host.RunTurnAsync("what is the return policy?");

        telemetry.Failures.Should().ContainSingle()
            .Which["mentoragent.error.type"].Should().Be(typeof(InvalidOperationException).FullName);
        host.Logs.HasWarning($"The search of {SourceName} failed").Should().BeTrue(host.Logs.WarningText);
    }

    [Fact]
    public async Task With_a_resolver_the_failure_names_the_tenant()
    {
        using var telemetry = new Recorded();
        var source = new ScriptedRagSource { ThrowOnNextSearch = new InvalidOperationException("down") };
        await using var host = Host(telemetry, source, resolver: true);
        ((TestTenantResolver)host.Get<IMentorTenantResolver>()).Resolve = _ => "contoso";
        host.Chat!.Say("Ok.");

        await host.RunTurnAsync("what is the return policy?");

        telemetry.Failures.Should().ContainSingle().Which["mentoragent.tenant"].Should().Be("contoso");
    }

    [Fact]
    public async Task A_search_that_works_counts_nothing_even_when_it_finds_nothing()
    {
        using var telemetry = new Recorded();
        var source = new ScriptedRagSource();
        await using var host = Host(telemetry, source);
        host.Chat!.Say("Ok.").Say("Ok.");

        await host.RunTurnAsync("what is the return policy?");
        source.Returns("Returns are accepted within 30 days.", 9f, "Returns");
        await host.RunTurnAsync("and the return policy?");

        telemetry.Failures.Should().BeEmpty();
        telemetry.Searches.Should().HaveCount(2).And.OnlyContain(s => s.Status != ActivityStatusCode.Error);
    }

    /// <summary>A source the user stops while it searches: Stop is pressed, and the search ends on the turn's token.</summary>
    private sealed class StoppedSource : IMentorRagSource
    {
        internal Action? OnSearch { get; set; }
        internal int Searches;

        public Task<IReadOnlyList<MentorRagResult>> SearchAsync(MentorRagRequest request, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Searches);
            OnSearch?.Invoke();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<MentorRagResult>>([]);
        }

        public Task<IReadOnlyList<MentorRagResult>> SearchAsync(string query, int maxResults, CancellationToken ct = default)
            => throw new InvalidOperationException("MentorAgent calls the request overload");
    }

    [Fact]
    public async Task A_stop_during_the_search_is_no_failure_of_the_source()
    {
        using var telemetry = new Recorded();
        var source = new StoppedSource();
        await using var host = MentorHostFixture.Create(o =>
        {
            o.UseRag = true;
            o.MemoryAutoCapture = false;
            o.CheckAnnouncedActions = false;
            o.EnableObservability = true;
            o.ObservabilitySourceName = telemetry.Source;
        }, s => s.AddSingleton<IMentorRagSource>(source));
        source.OnSearch = host.Orchestrator.CancelCurrentRequest;
        host.Chat!.Say("Ok.");

        await host.RunTurnAsync("what is the return policy?");

        source.Searches.Should().Be(1, "the search ran, and was stopped");
        telemetry.Failures.Should().BeEmpty();
    }

    [Fact]
    public async Task Without_observability_nothing_is_measured_and_the_turn_goes_on()
    {
        using var telemetry = new Recorded();
        var source = new ScriptedRagSource { ThrowOnNextSearch = new InvalidOperationException("down") };
        await using var host = Host(telemetry, source, observability: false);
        host.Chat!.Say("Ok.");

        (await host.RunTurnAsync("what is the return policy?")).Should().BeNull();

        telemetry.Failures.Should().BeEmpty();
        telemetry.Searches.Should().BeEmpty();
        host.Logs.HasWarning($"The search of {SourceName} failed").Should().BeTrue("the Warning stays");
    }

    [Fact]
    public async Task The_search_span_names_the_source_and_is_an_error_only_when_the_search_failed()
    {
        using var telemetry = new Recorded();
        var source = new ScriptedRagSource { ThrowOnNextSearch = new TimeoutException(Secret) };
        await using var host = Host(telemetry, source);
        host.Chat!.Say("Ok.").Say("Ok.");

        await host.RunTurnAsync("what is the return policy?");
        await host.RunTurnAsync("and now?");

        telemetry.Searches.Should().HaveCount(2).And.OnlyContain(s => (string?)s.GetTagItem("mentoragent.rag.source") == SourceName);
        var failed = telemetry.Searches[0];
        failed.Status.Should().Be(ActivityStatusCode.Error);
        failed.GetTagItem("mentoragent.error.type").Should().Be(typeof(TimeoutException).FullName);
        (failed.StatusDescription ?? "").Should().NotContain("hunter2");
        telemetry.Searches[1].Status.Should().NotBe(ActivityStatusCode.Error, "the next search worked");
    }

    [Fact]
    public async Task The_warning_names_the_source()
    {
        using var telemetry = new Recorded();
        var source = new ScriptedRagSource { ThrowOnNextSearch = new InvalidOperationException("down") };
        await using var host = Host(telemetry, source);
        host.Chat!.Say("Ok.");

        await host.RunTurnAsync("what is the return policy?");

        host.Logs.Records.Should().Contain(r => r.Level == LogLevel.Warning
            && r.Message.Contains($"The search of {SourceName} failed — RAG context skipped."));
    }
}
