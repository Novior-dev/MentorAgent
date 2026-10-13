using FluentAssertions;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Options;
using Microsoft.Extensions.Options;
using Xunit;

namespace Novior.MentorAgent.Tests.Metrics;

/// <summary>
/// <see cref="MentorMetrics"/> — what the admin dashboard is told about tokens, latency and cost.
///
/// <para>
/// This is the only place in the library that produces a number a human will act on. Everything else
/// either works or reports itself; a metric that is quietly wrong looks exactly like a metric that is
/// right, and the reader has no way to tell. The rule that matters most is the one about cost: a
/// model with no configured price must report <see langword="null"/>, never zero. Zero is a claim —
/// "this model cost nothing" — and it is the one number somebody might budget against.
/// </para>
///
/// <para>
/// The per-model split is the second half. Routing (#11) means a single conversation is served by two
/// different models at two different prices, and an aggregate that mixes them answers no useful
/// question at all.
/// </para>
/// </summary>
public sealed class MentorMetricsTests
{
    private const string Cheap = "gpt-4o-mini";
    private const string Strong = "gpt-4o";
    private const string Embedding = "text-embedding-3-small";

    private static MentorMetrics New(Action<MentorOptions>? configure = null)
    {
        var options = new MentorOptions
        {
            AppName = "TestApp",
            ScanAssemblies = [typeof(MentorMetricsTests).Assembly],
        };
        configure?.Invoke(options);

        return new MentorMetrics(new MentorTokenUsage(), new OptionsWrapper<MentorOptions>(options))
        {
            ModelId = Cheap,
            StrongModelId = Strong,
            EmbeddingModelId = Embedding,
        };
    }

    private static MentorModelUsage ModelIn(MentorMetricsSnapshot snapshot, string id)
        => snapshot.Models.Single(m => m.ModelId == id);

    // ── Conversation counters ───────────────────────────────────────────────────

    [Fact]
    public void The_conversation_counters_are_independent_of_each_other()
    {
        var metrics = New();

        metrics.RecordTurn();
        metrics.RecordTurn();
        metrics.RecordServed();
        metrics.RecordError();
        metrics.RecordGuardrailBlock();

        var snapshot = metrics.GetSnapshot();
        snapshot.TurnCount.Should().Be(2);
        snapshot.ServedCount.Should().Be(1);
        snapshot.ErrorCount.Should().Be(1);
        snapshot.GuardrailBlockCount.Should().Be(1);
    }

    [Fact]
    public void Turns_and_served_turns_are_counted_separately()
    {
        // Their ratio is the deflection rate the dashboard reports. Incrementing both together
        // would make it a constant 100% and nobody would notice.
        var metrics = New();

        metrics.RecordTurn();
        metrics.RecordTurn();
        metrics.RecordServed();

        var snapshot = metrics.GetSnapshot();
        snapshot.TurnCount.Should().Be(2);
        snapshot.ServedCount.Should().Be(1);
    }

    [Fact]
    public void Tool_calls_are_counted_per_tool()
    {
        var metrics = New();

        metrics.RecordToolCall("cancel_order");
        metrics.RecordToolCall("cancel_order");
        metrics.RecordToolCall("get_orders");

        var actions = metrics.GetSnapshot().TopActions;
        actions.Single(a => a.Action == "cancel_order").Count.Should().Be(2);
        actions.Single(a => a.Action == "get_orders").Count.Should().Be(1);
    }

    [Fact]
    public void Tool_names_differing_only_in_case_are_the_same_tool()
    {
        // Tool names are snake_case by construction, but a hand-written call site or an MCP server
        // can supply anything. Two rows for one tool halves both counts.
        var metrics = New();

        metrics.RecordToolCall("cancel_order");
        metrics.RecordToolCall("Cancel_Order");

        metrics.GetSnapshot().TopActions.Should().ContainSingle().Which.Count.Should().Be(2);
    }

    [Fact]
    public void A_nameless_tool_call_is_still_counted()
    {
        // Losing the count would be worse than an "unknown" row: the top-actions total would stop
        // matching the turn count and nothing would say why.
        var metrics = New();

        metrics.RecordToolCall("");
        metrics.RecordToolCall("   ");

        metrics.GetSnapshot().TopActions.Should().ContainSingle()
               .Which.Should().BeEquivalentTo(new MentorActionCount("unknown", 2));
    }

    [Fact]
    public void The_busiest_ten_tools_are_reported_most_used_first()
    {
        var metrics = New();
        for (var i = 0; i < 15; i++)
            for (var n = 0; n <= i; n++)
                metrics.RecordToolCall($"tool_{i:00}");

        var actions = metrics.GetSnapshot().TopActions;

        actions.Should().HaveCount(10);
        actions[0].Action.Should().Be("tool_14");
        actions.Select(a => a.Count).Should().BeInDescendingOrder();
    }

    // ── The per-model split ─────────────────────────────────────────────────────

    [Fact]
    public void Usage_lands_under_the_model_that_served_it()
    {
        var metrics = New();

        metrics.RecordModelUsage(Cheap, 100, 50);
        metrics.RecordModelUsage(Strong, 1000, 500);

        var snapshot = metrics.GetSnapshot();
        ModelIn(snapshot, Cheap).InputTokens.Should().Be(100);
        ModelIn(snapshot, Strong).InputTokens.Should().Be(1000);
    }

    [Fact]
    public void Several_calls_to_one_model_accumulate()
    {
        var metrics = New();

        metrics.RecordModelUsage(Cheap, 100, 50);
        metrics.RecordModelUsage(Cheap, 20, 10);

        var model = ModelIn(metrics.GetSnapshot(), Cheap);
        model.InputTokens.Should().Be(120);
        model.OutputTokens.Should().Be(60);
        model.RequestCount.Should().Be(2);
    }

    [Fact]
    public void A_model_id_that_differs_only_in_case_is_the_same_model()
    {
        // Providers are not consistent about casing, and two rows for one model splits both the
        // token totals and the cost.
        var metrics = New();

        metrics.RecordModelUsage(Cheap, 100, 50);
        metrics.RecordModelUsage(Cheap.ToUpperInvariant(), 100, 50);

        metrics.GetSnapshot().Models.Count(m => string.Equals(m.ModelId, Cheap, StringComparison.OrdinalIgnoreCase))
               .Should().Be(1);
    }

    [Fact]
    public void Usage_with_no_model_id_is_attributed_to_the_configured_chat_model()
    {
        // Some clients do not echo a model id. Attributing it to the model that is actually
        // configured is better than an "unknown" row that quietly grows.
        var metrics = New();

        metrics.RecordModelUsage(null, 100, 50);

        ModelIn(metrics.GetSnapshot(), Cheap).InputTokens.Should().Be(100);
    }

    [Fact]
    public void Usage_with_no_model_id_and_nothing_configured_is_labelled_unknown()
    {
        var metrics = New();
        metrics.ModelId = null;
        metrics.StrongModelId = null;
        metrics.EmbeddingModelId = null;

        metrics.RecordModelUsage(null, 100, 50);

        metrics.GetSnapshot().Models.Should().ContainSingle().Which.ModelId.Should().Be("unknown");
    }

    [Fact]
    public void Each_model_is_classified_by_the_role_it_plays()
    {
        var metrics = New();

        metrics.RecordModelUsage(Cheap, 1, 1);
        metrics.RecordModelUsage(Strong, 1, 1);
        metrics.RecordModelUsage(Embedding, 1, 0);
        metrics.RecordModelUsage("some-other-model", 1, 1);

        var snapshot = metrics.GetSnapshot();
        ModelIn(snapshot, Cheap).Kind.Should().Be(MentorModelKind.Cheap);
        ModelIn(snapshot, Strong).Kind.Should().Be(MentorModelKind.Strong);
        ModelIn(snapshot, Embedding).Kind.Should().Be(MentorModelKind.Embedding);
        ModelIn(snapshot, "some-other-model").Kind.Should().Be(MentorModelKind.Other);
    }

    [Fact]
    public void Models_are_listed_in_a_stable_order()
    {
        // The dashboard's model selector reads this list. An order that changed between refreshes
        // would move the row under the reader's cursor.
        var metrics = New();
        metrics.RecordModelUsage("some-other-model", 1, 1);
        metrics.RecordModelUsage(Embedding, 1, 0);
        metrics.RecordModelUsage(Strong, 1, 1);
        metrics.RecordModelUsage(Cheap, 1, 1);

        metrics.GetSnapshot().Models.Select(m => m.Kind)
               .Should().BeInAscendingOrder();
    }

    [Fact]
    public void The_top_line_totals_are_the_sum_across_models()
    {
        var metrics = New();

        metrics.RecordModelUsage(Cheap, 100, 50);
        metrics.RecordModelUsage(Strong, 1000, 500);

        var snapshot = metrics.GetSnapshot();
        snapshot.TotalInputTokens.Should().Be(1100);
        snapshot.TotalOutputTokens.Should().Be(550);
        snapshot.RequestCount.Should().Be(2);
    }

    // ── Latency ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Average_latency_is_weighted_by_request_not_by_call_site()
    {
        // A mean of means would let one fast call cancel out ten slow ones.
        var metrics = New();

        metrics.RecordModelUsage(Cheap, 1, 1, latencyMs: 100);
        metrics.RecordModelUsage(Cheap, 1, 1, latencyMs: 200);
        metrics.RecordModelUsage(Cheap, 1, 1, latencyMs: 300);

        ModelIn(metrics.GetSnapshot(), Cheap).AvgLatencyMs.Should().Be(200);
    }

    [Fact]
    public void A_model_with_no_timing_reports_null_latency_not_zero()
    {
        // Zero milliseconds is a claim about performance. Null says "not measured", which is what
        // is true when a client does not report timings.
        var metrics = New();

        metrics.RecordModelUsage(Cheap, 100, 50);

        var model = ModelIn(metrics.GetSnapshot(), Cheap);
        model.AvgLatencyMs.Should().BeNull();
        model.AvgTimeToFirstByteMs.Should().BeNull();
    }

    [Fact]
    public void Time_to_first_byte_is_averaged_separately_from_total_latency()
    {
        // They answer different questions — how fast it starts talking, and how long it takes in
        // total — and only streaming calls report the first.
        var metrics = New();

        metrics.RecordModelUsage(Cheap, 1, 1, latencyMs: 1000, timeToFirstByteMs: 200);
        metrics.RecordModelUsage(Cheap, 1, 1, latencyMs: 2000);

        var model = ModelIn(metrics.GetSnapshot(), Cheap);
        model.AvgLatencyMs.Should().Be(1500, "both calls reported a total");
        model.AvgTimeToFirstByteMs.Should().Be(200, "only one reported a first byte");
    }

    // ── Cost ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Cost_is_computed_per_million_tokens_from_the_configured_price()
    {
        var metrics = New(o => o.ModelPricing = new Dictionary<string, ModelPrice>
        {
            [Cheap] = new(InputPer1M: 0.15m, OutputPer1M: 0.60m),
        });

        metrics.RecordModelUsage(Cheap, 1_000_000, 500_000);

        var model = ModelIn(metrics.GetSnapshot(), Cheap);
        model.InputCost.Should().Be(0.15m);
        model.OutputCost.Should().Be(0.30m);
    }

    [Fact]
    public void An_unpriced_model_reports_no_cost_rather_than_a_cost_of_zero()
    {
        // The rule this whole file exists for. Zero is a statement — "this model was free" — and it
        // is the number somebody budgets against. Null is the truth: nobody told us the price.
        var metrics = New(o => o.ModelPricing = new Dictionary<string, ModelPrice>
        {
            [Cheap] = new(0.15m, 0.60m),
        });

        metrics.RecordModelUsage(Strong, 1_000_000, 500_000);

        var model = ModelIn(metrics.GetSnapshot(), Strong);
        model.InputCost.Should().BeNull();
        model.OutputCost.Should().BeNull();
    }

    [Fact]
    public void With_no_pricing_configured_at_all_no_cost_is_reported()
    {
        var metrics = New();

        metrics.RecordModelUsage(Cheap, 1_000_000, 500_000);

        var snapshot = metrics.GetSnapshot();
        ModelIn(snapshot, Cheap).InputCost.Should().BeNull();
        snapshot.InputCost.Should().BeNull();
        snapshot.OutputCost.Should().BeNull();
    }

    [Fact]
    public void The_aggregate_cost_counts_only_the_models_that_have_a_price()
    {
        // A partially-priced deployment is the normal case — a new model gets added and its price
        // is not in configuration yet. The total must not silently treat it as free, but it must
        // also still report what it does know.
        var metrics = New(o => o.ModelPricing = new Dictionary<string, ModelPrice>
        {
            [Cheap] = new(1m, 2m),
        });

        metrics.RecordModelUsage(Cheap, 1_000_000, 1_000_000);
        metrics.RecordModelUsage(Strong, 1_000_000, 1_000_000);

        var snapshot = metrics.GetSnapshot();
        snapshot.InputCost.Should().Be(1m);
        snapshot.OutputCost.Should().Be(2m);
    }

    [Fact]
    public void A_priced_model_that_served_nothing_costs_nothing()
    {
        // Zero is right here, because the price *is* known and the usage *is* zero.
        var metrics = New(o => o.ModelPricing = new Dictionary<string, ModelPrice>
        {
            [Strong] = new(1m, 2m),
        });

        metrics.RecordModelUsage(Cheap, 100, 50);

        ModelIn(metrics.GetSnapshot(), Strong).InputCost.Should().Be(0m);
    }

    // ── The time series ─────────────────────────────────────────────────────────

    [Fact]
    public void Calls_within_the_same_hour_share_one_point()
    {
        var metrics = New();

        metrics.RecordModelUsage(Cheap, 100, 50);
        metrics.RecordModelUsage(Cheap, 100, 50);

        var series = ModelIn(metrics.GetSnapshot(), Cheap).Series;
        series.Should().ContainSingle();
        series[0].InputTokens.Should().Be(200);
        series[0].RequestCount.Should().Be(2);
    }

    [Fact]
    public void The_bucket_size_is_published_with_the_snapshot()
    {
        // The chart needs it to space its x-axis; hard-coding 60 in the component would silently
        // mis-scale the day the bucket changes.
        New().GetSnapshot().SeriesBucketMinutes.Should().Be(60);
    }

    [Fact]
    public void Restored_points_older_than_the_retention_window_are_pruned()
    {
        // Without the trim a long-lived process grows a point per hour per model forever, and the
        // dashboard payload grows with it.
        var metrics = New(o => o.MetricsRetention = TimeSpan.FromHours(2));
        metrics.Seed(SnapshotWith(Cheap,
            (DateTimeOffset.UtcNow.AddHours(-48), 10),
            (DateTimeOffset.UtcNow.AddHours(-1), 20)));

        var series = ModelIn(metrics.GetSnapshot(), Cheap).Series;

        series.Should().ContainSingle("only the point inside the window survives");
        series[0].InputTokens.Should().Be(20);
    }

    [Fact]
    public void Points_inside_the_window_are_kept_and_ordered_oldest_first()
    {
        var metrics = New(o => o.MetricsRetention = TimeSpan.FromDays(7));
        metrics.Seed(SnapshotWith(Cheap,
            (DateTimeOffset.UtcNow.AddHours(-3), 30),
            (DateTimeOffset.UtcNow.AddHours(-1), 10),
            (DateTimeOffset.UtcNow.AddHours(-2), 20)));

        var series = ModelIn(metrics.GetSnapshot(), Cheap).Series;

        series.Select(p => p.InputTokens).Should().Equal(30, 20, 10);
        series.Select(p => p.Timestamp).Should().BeInAscendingOrder();
    }

    // ── Models that have not served yet ─────────────────────────────────────────

    [Fact]
    public void A_configured_model_appears_before_it_has_served_anything()
    {
        // The strong routing model may never be reached in a quiet hour. A dashboard that hides it
        // until it is used cannot answer "is routing configured at all".
        var metrics = New();

        var snapshot = metrics.GetSnapshot();

        snapshot.Models.Select(m => m.ModelId).Should().Contain([Cheap, Strong, Embedding]);
        ModelIn(snapshot, Strong).RequestCount.Should().Be(0);
    }

    [Fact]
    public void Registering_the_configured_models_never_resets_live_counters()
    {
        var metrics = New();
        metrics.RecordModelUsage(Cheap, 100, 50);

        metrics.EnsureConfiguredModels();
        metrics.EnsureConfiguredModels();

        ModelIn(metrics.GetSnapshot(), Cheap).InputTokens.Should().Be(100);
    }

    [Fact]
    public void A_restore_cannot_hide_a_configured_model()
    {
        // Seed replaces the whole model map. Re-registering on every snapshot is what stops a
        // restored process from losing the row for a model that had not served before the restart.
        var metrics = New();
        metrics.Seed(SnapshotWith(Cheap, (DateTimeOffset.UtcNow, 10)));

        metrics.GetSnapshot().Models.Select(m => m.ModelId).Should().Contain(Strong);
    }

    // ── Restore and reset ───────────────────────────────────────────────────────

    [Fact]
    public void A_restored_snapshot_continues_the_counters_across_a_restart()
    {
        var metrics = New();

        metrics.Seed(new MentorMetricsSnapshot
        {
            TurnCount = 7, ServedCount = 6, ErrorCount = 1, GuardrailBlockCount = 2,
            TopActions = [new("cancel_order", 4)],
            Models = [],
        });

        var snapshot = metrics.GetSnapshot();
        snapshot.TurnCount.Should().Be(7);
        snapshot.ServedCount.Should().Be(6);
        snapshot.ErrorCount.Should().Be(1);
        snapshot.GuardrailBlockCount.Should().Be(2);
        snapshot.TopActions.Single().Count.Should().Be(4);
    }

    [Fact]
    public void A_restored_snapshot_brings_back_the_per_model_totals()
    {
        var metrics = New();

        metrics.Seed(SnapshotWith(Cheap, (DateTimeOffset.UtcNow, 10)));

        var model = ModelIn(metrics.GetSnapshot(), Cheap);
        model.InputTokens.Should().Be(10);
        model.Series.Should().ContainSingle();
    }

    [Fact]
    public void New_usage_after_a_restore_adds_to_what_was_restored()
    {
        var metrics = New();
        metrics.Seed(SnapshotWith(Cheap, (DateTimeOffset.UtcNow, 10)));

        metrics.RecordModelUsage(Cheap, 5, 0);

        ModelIn(metrics.GetSnapshot(), Cheap).InputTokens.Should().Be(15);
    }

    [Fact]
    public void A_restore_does_not_overwrite_a_model_id_the_orchestrator_already_set()
    {
        // The live configuration wins over a snapshot written by a previous deployment, which may
        // have run a different model.
        var metrics = New();

        metrics.Seed(new MentorMetricsSnapshot { ModelId = "an-old-deployment", Models = [], TopActions = [] });

        metrics.GetSnapshot().ModelId.Should().Be(Cheap);
    }

    [Fact]
    public void Reset_clears_everything_the_snapshot_reports()
    {
        var metrics = New();
        metrics.RecordTurn();
        metrics.RecordError();
        metrics.RecordToolCall("cancel_order");
        metrics.RecordModelUsage(Cheap, 100, 50);

        metrics.Reset();

        var snapshot = metrics.GetSnapshot();
        snapshot.TurnCount.Should().Be(0);
        snapshot.ErrorCount.Should().Be(0);
        snapshot.TopActions.Should().BeEmpty();
        snapshot.TotalInputTokens.Should().Be(0);
        ModelIn(snapshot, Cheap).RequestCount.Should().Be(0);
    }

    // ── Concurrency ─────────────────────────────────────────────────────────────

    [Fact]
    public void Concurrent_recording_loses_nothing()
    {
        // One process-wide instance serves every connection, so this is the normal operating mode
        // rather than a stress case. A lost increment here is a number nobody can reconcile later.
        var metrics = New();
        var models = new[] { Cheap, Strong, Embedding };

        Parallel.For(0, 3_000, i =>
        {
            metrics.RecordModelUsage(models[i % 3], 10, 5, latencyMs: 100);
            metrics.RecordTurn();
            metrics.RecordToolCall("cancel_order");
        });

        var snapshot = metrics.GetSnapshot();
        snapshot.TurnCount.Should().Be(3_000);
        snapshot.TotalInputTokens.Should().Be(30_000);
        snapshot.RequestCount.Should().Be(3_000);
        snapshot.TopActions.Single().Count.Should().Be(3_000);
        foreach (var id in models)
            ModelIn(snapshot, id).AvgLatencyMs.Should().Be(100);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    /// <summary>A persisted snapshot with one model and the given series points.</summary>
    private static MentorMetricsSnapshot SnapshotWith(
        string modelId, params (DateTimeOffset When, long Input)[] points)
        => new()
        {
            TopActions = [],
            Models =
            [
                new MentorModelUsage
                {
                    ModelId = modelId,
                    Kind = MentorModelKind.Cheap,
                    InputTokens = points.Sum(p => p.Input),
                    RequestCount = points.Length,
                    Series = [.. points.Select(p => new MentorUsagePoint
                    {
                        Timestamp = p.When,
                        InputTokens = p.Input,
                        RequestCount = 1,
                    })],
                },
            ],
        };
}
