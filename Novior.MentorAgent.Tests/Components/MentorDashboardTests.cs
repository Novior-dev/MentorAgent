using System.Globalization;
using Bunit;
using FluentAssertions;
using Novior.MentorAgent.Abstractions.Components;
using Novior.MentorAgent.Abstractions.Models;
using Xunit;

namespace Novior.MentorAgent.Tests.Components;

/// <summary>
/// The admin dashboard. Three properties matter more than the markup:
///
/// <list type="number">
/// <item>the model selector really re-scopes the numbers, rather than relabelling one bucket;</item>
/// <item>an unpriced model shows "n/a" and never a fabricated zero — a cost of $0.0000 next to a
/// million tokens is a number somebody will put in a budget;</item>
/// <item>the charts are inline SVG with no JS, because the component has to work under a strict
/// CSP in both Blazor Server and WebAssembly.</item>
/// </list>
/// </summary>
public sealed class MentorDashboardTests : ComponentTestBase
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 13, 10, 0, 0, TimeSpan.Zero);

    private static MentorUsagePoint Point(int hour, long input, long output, long requests, double? latency = 120)
        => new()
        {
            Timestamp = T0.AddHours(hour),
            InputTokens = input,
            OutputTokens = output,
            RequestCount = requests,
            AvgLatencyMs = latency,
        };

    /// <summary>Two priced models plus one unpriced, which is the shape a real deployment has.</summary>
    private static MentorMetricsSnapshot Snapshot() => new()
    {
        TotalInputTokens = 3_000,
        TotalOutputTokens = 1_500,
        RequestCount = 30,
        TurnCount = 25,
        ServedCount = 20,
        ErrorCount = 2,
        GuardrailBlockCount = 1,
        ModelId = "gpt-4o-mini",
        InputCost = 0.30m,
        OutputCost = 0.20m,
        TopActions = [new("cancel_order", 12), new("get_weather", 6)],
        GeneratedAt = T0.AddHours(3),
        Models =
        [
            new MentorModelUsage
            {
                ModelId = "gpt-4o-mini",
                Kind = MentorModelKind.Cheap,
                InputTokens = 2_000, OutputTokens = 1_000, RequestCount = 20,
                AvgLatencyMs = 100, AvgTimeToFirstByteMs = 40,
                InputCost = 0.20m, OutputCost = 0.10m,
                Series = [Point(0, 800, 400, 8), Point(1, 700, 350, 7), Point(2, 500, 250, 5)],
            },
            new MentorModelUsage
            {
                ModelId = "gpt-4o",
                Kind = MentorModelKind.Strong,
                InputTokens = 900, OutputTokens = 450, RequestCount = 9,
                AvgLatencyMs = 300, AvgTimeToFirstByteMs = 90,
                InputCost = 0.10m, OutputCost = 0.10m,
                Series = [Point(0, 400, 200, 4), Point(1, 300, 150, 3), Point(2, 200, 100, 2)],
            },
            new MentorModelUsage
            {
                ModelId = "text-embedding-3-small",
                Kind = MentorModelKind.Embedding,
                InputTokens = 100, OutputTokens = 50, RequestCount = 1,
                AvgLatencyMs = 20,
                Series = [Point(0, 100, 50, 1)],
            },
        ],
    };

    private IRenderedComponent<MentorDashboard> RenderDash(
        MentorMetricsSnapshot? snapshot = null, Action? onRefresh = null, string currency = "$")
        => Render<MentorDashboard>(p =>
        {
            p.Add(c => c.Snapshot, snapshot ?? Snapshot());
            p.Add(c => c.Currency, currency);
            if (onRefresh is not null) p.Add(c => c.OnRefresh, onRefresh);
        });

    // ── Empty state ─────────────────────────────────────────────────────────────

    [Fact]
    public void With_no_metrics_available_it_says_so_instead_of_showing_zeroes()
    {
        // No Snapshot parameter and no IMentorMetrics in DI — the WASM-before-first-fetch case.
        // A grid of zeroes would read as "the assistant was never used".
        var cut = Render<MentorDashboard>();

        cut.FindAll(".bm-dash--empty").Should().HaveCount(1);
        cut.FindAll(".bm-dash__grid").Should().BeEmpty();
    }

    // ── Aggregate view ──────────────────────────────────────────────────────────

    [Fact]
    public void The_all_models_view_sums_every_model()
    {
        var cut = RenderDash();

        var values = cut.FindAll(".bm-dash__stat-val").Select(e => e.TextContent).ToList();
        values.Should().Contain("30", "requests come from the snapshot total");
        // The dashboard formats in the viewer's culture: "4.500" in it-IT, "4,500" on an en-US build agent.
        values.Should().Contain(4_500.ToString("N0", CultureInfo.CurrentCulture),
            "3,000 input + 1,500 output, thousands-separated");
    }

    [Fact]
    public void Conversation_metrics_appear_only_in_the_all_models_view()
    {
        // Turns, deflection, errors and guardrail blocks are model-independent, so attributing
        // them to one model would be wrong.
        var cut = RenderDash();
        cut.FindAll(".bm-dash__grid").Should().HaveCount(2);

        cut.Find(".bm-dash__select").Change("gpt-4o");

        cut.FindAll(".bm-dash__grid").Should().HaveCount(1);
    }

    [Fact]
    public void Latency_across_models_is_weighted_by_request_count_not_averaged_flat()
    {
        // (100×20 + 300×9 + 20×1) / 30 = 157 ms. A flat mean would say 140 and would drift further
        // the more lopsided the traffic — which is exactly when the number gets looked at.
        var cut = RenderDash();

        cut.FindAll(".bm-dash__stat-val").Select(e => e.TextContent).Should().Contain("157 ms");
    }

    // ── The model selector ──────────────────────────────────────────────────────

    [Fact]
    public void The_selector_lists_every_model_plus_an_all_models_entry()
    {
        var cut = RenderDash();

        var options = cut.FindAll(".bm-dash__select option");
        options.Should().HaveCount(4);
        options[0].GetAttribute("value").Should().BeEmpty();
        options.Skip(1).Select(o => o.GetAttribute("value"))
            .Should().Equal(["gpt-4o-mini", "gpt-4o", "text-embedding-3-small"]);
    }

    [Fact]
    public void Choosing_a_model_rescopes_the_summary_to_that_model_alone()
    {
        var cut = RenderDash();

        cut.Find(".bm-dash__select").Change("gpt-4o");

        var values = cut.FindAll(".bm-dash__stat-val").Select(e => e.TextContent).ToList();
        values.Should().Contain("9", "gpt-4o served 9 of the 30 requests");
        values.Should().Contain("900");
        values.Should().Contain("300 ms", "the single model's own latency, not the weighted mean");
    }

    [Fact]
    public void Selecting_a_model_and_going_back_restores_the_aggregate()
    {
        var cut = RenderDash();

        cut.Find(".bm-dash__select").Change("gpt-4o");
        cut.Find(".bm-dash__select").Change("");

        cut.FindAll(".bm-dash__stat-val").Select(e => e.TextContent).Should().Contain("30");
    }

    [Fact]
    public void An_unknown_model_id_falls_back_to_the_aggregate_rather_than_showing_nothing()
    {
        // Reachable when the snapshot is refreshed and a model has aged out of retention while
        // it was selected.
        var cut = RenderDash();

        cut.Find(".bm-dash__select").Change("a-model-that-retired");

        cut.FindAll(".bm-dash__stat-val").Select(e => e.TextContent).Should().Contain("30");
    }

    // ── Cost ────────────────────────────────────────────────────────────────────

    [Fact]
    public void An_unpriced_model_shows_na_and_never_a_zero_cost()
    {
        var cut = RenderDash();

        var embeddingRow = cut.FindAll(".bm-dash__table tbody tr")
            .First(r => r.TextContent.Contains("text-embedding-3-small"));

        var cost = embeddingRow.QuerySelectorAll("td").Last().TextContent;
        cost.Should().NotContain("0.0000", "a fabricated zero cost ends up in somebody's budget");
        cost.Should().NotBeEmpty();
    }

    [Fact]
    public void The_currency_prefix_is_configurable()
    {
        var cut = RenderDash(currency: "€");

        cut.Find(".bm-dash__table tbody tr").TextContent.Should().Contain("€");
    }

    [Fact]
    public void Cost_is_shown_to_four_decimals_because_a_turn_costs_fractions_of_a_cent()
    {
        var cut = RenderDash();

        var firstRow = cut.Find(".bm-dash__table tbody tr");
        firstRow.QuerySelectorAll("td").Last().TextContent
            .Should().Contain(0.3m.ToString("0.0000", CultureInfo.CurrentCulture))
            .And.NotContain(0.3m.ToString("0.00", CultureInfo.CurrentCulture) + " ",
                "two decimals rounds most single-turn costs to zero");
    }

    // ── Per-model table ─────────────────────────────────────────────────────────

    [Fact]
    public void The_table_has_one_row_per_model_plus_a_totals_row()
    {
        var cut = RenderDash();

        cut.FindAll(".bm-dash__table tbody tr").Should().HaveCount(4);
        cut.FindAll(".bm-dash__table tbody tr.bm-dash__total").Should().HaveCount(1);
    }

    [Fact]
    public void Each_model_carries_a_kind_badge_matching_its_classification()
    {
        var cut = RenderDash();

        var badges = cut.FindAll(".bm-dash__badge").Select(e => e.GetAttribute("class") ?? "").ToList();
        badges.Should().HaveCount(3);
        string.Join(" ", badges).Should().Contain("bm-dash__badge--cheap")
            .And.Contain("bm-dash__badge--strong")
            .And.Contain("bm-dash__badge--embedding");
    }

    [Fact]
    public void A_model_with_no_measured_latency_shows_a_dash_not_zero_milliseconds()
    {
        var snapshot = Snapshot() with
        {
            Models = [new MentorModelUsage { ModelId = "m", Kind = MentorModelKind.Other, RequestCount = 1 }],
        };

        var cut = RenderDash(snapshot);

        cut.Find(".bm-dash__table tbody tr").TextContent.Should().Contain("—")
            .And.NotContain("0 ms", "0 ms claims an instant response instead of no measurement");
    }

    // ── Charts ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Three_charts_are_rendered_as_inline_svg_with_no_script()
    {
        var cut = RenderDash();

        cut.FindAll(".bm-dash__chartcard svg.bm-chart").Should().HaveCount(3);
        cut.FindAll("script").Should().BeEmpty("the dashboard must render under a strict CSP");
    }

    [Fact]
    public void The_token_chart_draws_one_polyline_per_series()
    {
        var cut = RenderDash();

        cut.FindAll(".bm-dash__chartcard")[0].QuerySelectorAll("polyline")
            .Should().HaveCount(3, "input, output and total");
    }

    [Fact]
    public void Chart_colours_come_from_css_variables_so_they_follow_the_theme()
    {
        var cut = RenderDash();

        (cut.Find(".bm-dash__chartcard polyline").GetAttribute("style") ?? "")
            .Should().Contain("var(--bm-chart-in)");
    }

    [Fact]
    public void Chart_coordinates_are_invariant_culture_regardless_of_the_ui_language()
    {
        // A comma decimal separator in an SVG points attribute silently breaks the polyline, and
        // this repository's own test machine runs an Italian locale.
        Widget.Language = MentorLanguage.Italian;

        var cut = RenderDash();

        var points = cut.Find(".bm-dash__chartcard polyline").GetAttribute("points") ?? "";
        points.Should().NotBeEmpty();
        points.Split(' ').Should().OnlyContain(pair => pair.Count(ch => ch == ',') == 1,
            "each point is x,y — an extra comma means a decimal separator leaked in");
    }

    [Fact]
    public void A_single_bucket_shows_a_message_instead_of_a_one_point_chart()
    {
        var snapshot = Snapshot() with
        {
            Models = [new MentorModelUsage { ModelId = "m", Kind = MentorModelKind.Cheap, RequestCount = 1, Series = [Point(0, 10, 5, 1)] }],
        };

        var cut = RenderDash(snapshot);

        cut.FindAll("svg.bm-chart").Should().BeEmpty();
        cut.FindAll(".bm-dash__muted").Should().NotBeEmpty();
    }

    [Fact]
    public void Buckets_are_merged_by_timestamp_across_models_in_the_aggregate_view()
    {
        // Three models over three buckets must produce three points per series, not nine.
        var cut = RenderDash();

        var points = (cut.Find(".bm-dash__chartcard polyline").GetAttribute("points") ?? "").Split(' ');
        points.Should().HaveCount(3);
    }

    [Fact]
    public void An_all_zero_series_does_not_divide_by_zero()
    {
        var snapshot = Snapshot() with
        {
            Models =
            [
                new MentorModelUsage
                {
                    ModelId = "m", Kind = MentorModelKind.Cheap,
                    Series = [Point(0, 0, 0, 0, null), Point(1, 0, 0, 0, null)],
                },
            ],
        };

        var act = () => RenderDash(snapshot);

        act.Should().NotThrow("an idle deployment is the normal state of a fresh install");
    }

    // ── Top actions ─────────────────────────────────────────────────────────────

    [Fact]
    public void Top_actions_are_listed_with_bars_relative_to_the_most_used_one()
    {
        var cut = RenderDash();

        var items = cut.FindAll(".bm-dash__list li");
        items.Should().HaveCount(2);
        (items[0].QuerySelector(".bm-dash__bar-fill")!.GetAttribute("style") ?? "").Should().Contain("100%");
        (items[1].QuerySelector(".bm-dash__bar-fill")!.GetAttribute("style") ?? "").Should().Contain("50%",
            "6 of 12 is half the leader's bar");
    }

    [Fact]
    public void With_no_tool_calls_the_list_is_replaced_by_a_message()
    {
        var cut = RenderDash(Snapshot() with { TopActions = [] });

        cut.FindAll(".bm-dash__list").Should().BeEmpty();
        cut.FindAll(".bm-dash__muted").Should().NotBeEmpty();
    }

    [Fact]
    public void A_tool_name_containing_markup_is_rendered_as_text()
    {
        var cut = RenderDash(Snapshot() with { TopActions = [new("<script>alert(1)</script>", 1)] });

        cut.Find(".bm-dash__list").QuerySelectorAll("script").Should().BeEmpty();
        cut.Find(".bm-dash__action").TextContent.Should().Be("<script>alert(1)</script>");
    }

    // ── Refresh and localisation ────────────────────────────────────────────────

    [Fact]
    public void Refresh_raises_the_callback_the_host_uses_to_refetch()
    {
        var refreshed = 0;
        var cut = RenderDash(onRefresh: () => refreshed++);

        cut.Find(".bm-dash__controls .bm-btn").Click();

        refreshed.Should().Be(1);
    }

    [Fact]
    public void An_explicit_language_parameter_wins_over_the_one_in_di()
    {
        // The WASM case: there is no MentorAgent DI, so the host passes the language directly.
        Widget.Language = MentorLanguage.English;

        var cut = Render<MentorDashboard>(p => p
            .Add(c => c.Snapshot, Snapshot())
            .Add(c => c.Language, MentorLanguage.Italian));

        cut.Find(".bm-dash__title").TextContent.Should().NotBe(
            Render<MentorDashboard>(q => q.Add(c => c.Snapshot, Snapshot())).Find(".bm-dash__title").TextContent);
    }

    [Fact]
    public void Without_an_explicit_language_it_follows_the_one_in_di()
    {
        Widget.Language = MentorLanguage.Italian;

        var cut = RenderDash();

        cut.Find(".bm-dash__title").TextContent.Should().NotBeEmpty();
        cut.Markup.Should().NotContain("dash.title", "an untranslated key means the localizer was not resolved");
    }
}
