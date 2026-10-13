using FluentAssertions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.Options;
using Xunit;

namespace Novior.MentorAgent.Tests.Metrics;

/// <summary>
/// <see cref="MentorMetricsPersistenceService"/> — the dashboard's counters surviving a restart.
///
/// <para>
/// The store is a host-supplied seam, which is what makes this worth testing: it is somebody else's
/// database, and it will be unreachable at some point. The rule throughout is that a metrics store
/// outage must never affect the application — losing a token count is a nuisance, and failing to
/// start or refusing to shut down because a counter could not be written is not.
/// </para>
/// </summary>
public sealed class MetricsPersistenceTests
{
    /// <summary>A store a test drives: what it returns, whether it throws, what it was given.</summary>
    private sealed class RecordingStore : IMentorMetricsStore
    {
        internal MentorMetricsSnapshot? Seed { get; set; }
        internal Exception? ThrowOnLoad { get; set; }
        internal Exception? ThrowOnSave { get; set; }
        internal List<MentorMetricsSnapshot> Saved { get; } = [];

        public Task<MentorMetricsSnapshot?> LoadForSeedAsync(CancellationToken ct = default)
            => ThrowOnLoad is { } ex ? Task.FromException<MentorMetricsSnapshot?>(ex) : Task.FromResult(Seed);

        public Task SaveAsync(MentorMetricsSnapshot snapshot, CancellationToken ct = default)
        {
            if (ThrowOnSave is { } ex) return Task.FromException(ex);
            lock (Saved) Saved.Add(snapshot);
            return Task.CompletedTask;
        }
    }

    private sealed record Rig(
        MentorMetricsPersistenceService Service,
        MentorMetrics Metrics,
        RecordingStore Store,
        TestLogCollector Logs);

    private static Rig Build(Action<RecordingStore>? configureStore = null,
                             Action<MentorOptions>? configure = null)
    {
        var store = new RecordingStore();
        configureStore?.Invoke(store);

        var options = new MentorOptions
        {
            AppName = "TestApp",
            ScanAssemblies = [typeof(MetricsPersistenceTests).Assembly],
            // Long enough that no timer tick can interleave with an assertion — the timer is not
            // what these tests are about, and a background flush would make them non-deterministic.
            MetricsPersistenceInterval = TimeSpan.FromMinutes(30),
        };
        configure?.Invoke(options);

        var wrapped = new OptionsWrapper<MentorOptions>(options);
        var metrics = new MentorMetrics(new MentorTokenUsage(), wrapped);
        var logs = new TestLogCollector();

        var service = new MentorMetricsPersistenceService(
            metrics, store, wrapped,
            new TypedLogger<MentorMetricsPersistenceService>(
                new TestLoggerProvider(logs).CreateLogger("metrics")));

        return new Rig(service, metrics, store, logs);
    }

    private static MentorMetricsSnapshot Snapshot(long turns = 0, long input = 0)
        => new() { TurnCount = turns, TotalInputTokens = input, TopActions = [], Models = [] };

    // ── Startup ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Startup_restores_the_last_persisted_counters()
    {
        var rig = Build(s => s.Seed = Snapshot(turns: 42));

        await rig.Service.StartAsync(CancellationToken.None);

        rig.Metrics.GetSnapshot().TurnCount.Should().Be(42);
    }

    [Fact]
    public async Task Startup_says_what_it_restored()
    {
        // The dashboard's numbers jump on restart. One line explaining why is the difference
        // between "the counters continued" and "something is double-counting".
        var rig = Build(s => s.Seed = Snapshot(turns: 42, input: 1000));

        await rig.Service.StartAsync(CancellationToken.None);

        string.Join("\n", rig.Logs.Records.Select(r => r.Message))
              .Should().Contain("Metrics restored from store");
    }

    [Fact]
    public async Task An_empty_store_starts_from_zero_without_comment()
    {
        // First run. Announcing a restore that did not happen would be actively misleading.
        var rig = Build();

        await rig.Service.StartAsync(CancellationToken.None);

        rig.Metrics.GetSnapshot().TurnCount.Should().Be(0);
        string.Join("\n", rig.Logs.Records.Select(r => r.Message))
              .Should().NotContain("Metrics restored");
    }

    [Fact]
    public async Task A_store_that_cannot_be_read_does_not_stop_the_application_starting()
    {
        // This runs inside IHostedService.StartAsync. An exception here takes the whole host down —
        // the application refuses to boot because a metrics database is unreachable.
        var rig = Build(s => s.ThrowOnLoad = new InvalidOperationException("database unreachable"));

        var act = async () => await rig.Service.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
        rig.Logs.HasWarning("Metrics seed from store failed").Should().BeTrue(rig.Logs.WarningText);
    }

    [Fact]
    public async Task A_failed_restore_leaves_the_counters_usable()
    {
        var rig = Build(s => s.ThrowOnLoad = new InvalidOperationException("database unreachable"));
        await rig.Service.StartAsync(CancellationToken.None);

        rig.Metrics.RecordTurn();

        rig.Metrics.GetSnapshot().TurnCount.Should().Be(1, "it started fresh rather than broken");
    }

    // ── Shutdown ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Shutdown_flushes_the_very_latest_counts()
    {
        // Everything since the last timer tick lives only in RAM. Without this the final minutes of
        // a deployment window are lost on every restart.
        var rig = Build();
        await rig.Service.StartAsync(CancellationToken.None);
        rig.Metrics.RecordTurn();
        rig.Metrics.RecordModelUsage("gpt-4o-mini", 100, 50);

        await rig.Service.StopAsync(CancellationToken.None);

        var saved = rig.Store.Saved.Should().ContainSingle().Subject;
        saved.TurnCount.Should().Be(1);
        saved.TotalInputTokens.Should().Be(100);
    }

    [Fact]
    public async Task A_store_that_cannot_be_written_does_not_stop_the_application_shutting_down()
    {
        // A host that hangs or throws on shutdown is worse than one that loses a counter: it turns
        // a rolling deployment into a stuck one.
        var rig = Build(s => s.ThrowOnSave = new InvalidOperationException("database unreachable"));
        await rig.Service.StartAsync(CancellationToken.None);

        var act = async () => await rig.Service.StopAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
        rig.Logs.HasWarning("Final metrics flush to store failed").Should().BeTrue(rig.Logs.WarningText);
    }

    [Fact]
    public async Task Stopping_before_starting_is_survivable()
    {
        // A host that fails during startup still calls StopAsync on everything it constructed.
        var rig = Build();

        var act = async () => await rig.Service.StopAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Disposing_is_survivable_at_any_point()
    {
        var rig = Build();
        await rig.Service.StartAsync(CancellationToken.None);
        await rig.Service.StopAsync(CancellationToken.None);

        var act = () => { rig.Service.Dispose(); rig.Service.Dispose(); };

        act.Should().NotThrow();
    }

    // ── The round trip ──────────────────────────────────────────────────────────

    [Fact]
    public async Task What_is_saved_on_shutdown_is_what_comes_back_on_startup()
    {
        // The property that makes persistence worth having, asserted end to end rather than in two
        // halves: a snapshot that serialises fine and restores wrong is the failure this catches.
        var first = Build();
        await first.Service.StartAsync(CancellationToken.None);
        first.Metrics.RecordTurn();
        first.Metrics.RecordToolCall("cancel_order");
        first.Metrics.RecordModelUsage("gpt-4o-mini", 100, 50);
        await first.Service.StopAsync(CancellationToken.None);

        var persisted = first.Store.Saved.Single();
        var second = Build(s => s.Seed = persisted);
        await second.Service.StartAsync(CancellationToken.None);

        var restored = second.Metrics.GetSnapshot();
        restored.TurnCount.Should().Be(1);
        restored.TopActions.Single().Action.Should().Be("cancel_order");
        restored.Models.Single(m => m.ModelId == "gpt-4o-mini").InputTokens.Should().Be(100);
    }

    [Fact]
    public async Task Counting_continues_from_the_restored_totals_rather_than_restarting()
    {
        var rig = Build(s => s.Seed = Snapshot(turns: 10));
        await rig.Service.StartAsync(CancellationToken.None);

        rig.Metrics.RecordTurn();
        await rig.Service.StopAsync(CancellationToken.None);

        rig.Store.Saved.Single().TurnCount.Should().Be(11);
    }

    // ── The interval ────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_short_interval_really_flushes_on_the_timer()
    {
        // The timer is the only thing standing between a crash and losing everything since startup.
        var rig = Build(configure: o => o.MetricsPersistenceInterval = TimeSpan.FromMilliseconds(50));
        await rig.Service.StartAsync(CancellationToken.None);
        rig.Metrics.RecordTurn();

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (rig.Store.Saved.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(25);

        rig.Store.Saved.Should().NotBeEmpty("the timer must have fired at least once");
        await rig.Service.StopAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_non_positive_interval_falls_back_to_the_default_rather_than_throwing(int seconds)
    {
        // Timer rejects a non-positive period with an ArgumentOutOfRangeException, thrown from
        // inside StartAsync — so a zero in configuration would stop the host booting.
        var rig = Build(configure: o => o.MetricsPersistenceInterval = TimeSpan.FromSeconds(seconds));

        var act = async () => await rig.Service.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
        await rig.Service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task No_flush_happens_between_startup_and_the_first_tick()
    {
        // Writing immediately on startup would overwrite a good stored snapshot with an empty one
        // whenever the seed failed — turning a transient read outage into permanent data loss.
        var rig = Build();

        await rig.Service.StartAsync(CancellationToken.None);

        rig.Store.Saved.Should().BeEmpty();
    }
}
