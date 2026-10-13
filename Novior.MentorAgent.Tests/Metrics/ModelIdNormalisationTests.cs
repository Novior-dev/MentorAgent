using FluentAssertions;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Options;
using Xunit;

namespace Novior.MentorAgent.Tests.Metrics;

/// <summary>
/// What happens when the provider names the model more precisely than the deployment does.
///
/// <para>
/// Found in Part D. One live session against Azure OpenAI reported no response model at all
/// (BUG-030); the next, against the same endpoint and the same <c>chat_completions</c> API, reported
/// <c>gpt-4.1-2025-04-14</c> for a deployment called <c>gpt-4.1</c>. The dashboard then showed this:
/// </para>
///
/// <code>
/// gpt-4.1              Economico   0 req        0 tokens   €0,0000
/// o3                   Forte       0 req        0 tokens   €0,0000
/// gpt-4.1-2025-04-14   Altro       4 req   8.794 tokens   n/d
/// Totale                           8 req  10.432 tokens   €0,0000
/// </code>
///
/// <para>
/// Four things wrong at once, and the last is the serious one: the real traffic lands in a separate
/// row; it is classified <c>Altro</c>, so the cheap/strong split that justifies routing is lost; its
/// cost is unknown because <c>ModelPricing</c> is keyed on the deployment name; the configured model
/// reads <b>0 requests</b>, which is the first row an operator checks; and the **total reads
/// €0,0000 for a session that genuinely cost money**, because the aggregate sums the costs it knows
/// and the only ones it knows are zero.
/// </para>
///
/// <para>
/// This predates BUG-030's fix — <c>RecordModelUsage</c> has always preferred the provider's id when
/// present. It simply could not be seen while the provider was returning blanks.
/// </para>
///
/// <para>
/// <b>Prefix matching is not the fix and must never be used here:</b> <c>gpt-4.1-mini</c> begins with
/// <c>gpt-4.1</c> and would be priced as the larger model, overstating the bill. The rule is exact:
/// strip a trailing ISO date (<c>-YYYY-MM-DD</c>) and retry, which folds
/// <c>gpt-4.1-2025-04-14</c> onto <c>gpt-4.1</c> and leaves <c>gpt-4.1-mini</c> alone because it has
/// no date suffix.
/// </para>
/// </summary>
public sealed class ModelIdNormalisationTests
{
    private const string Cheap  = "gpt-4.1";
    private const string Strong = "o3";

    private static MentorMetrics Build() =>
        new(new MentorTokenUsage(),
            Microsoft.Extensions.Options.Options.Create(new MentorOptions
            {
                AppName = "x",
                ScanAssemblies = [],
                ModelPricing = new Dictionary<string, ModelPrice>
                {
                    [Cheap]  = new(2m, 8m),
                    [Strong] = new(10m, 40m),
                },
            }))
        { ModelId = Cheap, StrongModelId = Strong };

    private static MentorModelUsage? Row(MentorMetrics m, string id)
        => m.GetSnapshot().Models.FirstOrDefault(x => x.ModelId == id);

    // ── The defect ──────────────────────────────────────────────────────────────

    [Fact]
    public void A_dated_provider_id_lands_in_the_configured_models_own_row()
    {
        // One model, one row. Two rows for one model splits every number the dashboard shows.
        var metrics = Build();

        metrics.RecordModelUsage("gpt-4.1-2025-04-14", 1_000, 100, latencyMs: 500);

        Row(metrics, "gpt-4.1-2025-04-14").Should().BeNull("it is the same model as the deployment");
        var cheap = Row(metrics, Cheap);
        cheap!.RequestCount.Should().Be(1);
        cheap.InputTokens.Should().Be(1_000);
    }

    [Fact]
    public void A_dated_id_keeps_its_kind_so_the_cheap_strong_split_survives()
    {
        // The split is the entire justification for routing. Classified as `Other`, an escalation is
        // indistinguishable from an unknown model and the dashboard's selector loses both.
        var metrics = Build();

        metrics.RecordModelUsage("o3-2025-04-16", 500, 700, latencyMs: 9_000);

        var strong = Row(metrics, Strong)!;
        // The Kind alone proves nothing: `EnsureConfiguredModels` pre-creates a zero-usage row for
        // every configured model, so this assertion passed even before the fix. The usage has to be
        // *in* that row.
        strong.RequestCount.Should().Be(1, "the escalated call belongs to the strong model's row");
        strong.OutputTokens.Should().Be(700);
        strong.Kind.Should().Be(MentorModelKind.Strong);
        Row(metrics, "o3-2025-04-16").Should().BeNull();
    }

    [Fact]
    public void A_dated_id_is_priced_rather_than_reported_as_unknown()
    {
        // The headline failure: real spend shown as €0,00 because the pricing key did not match.
        var metrics = Build();

        metrics.RecordModelUsage("gpt-4.1-2025-04-14", 1_000_000, 1_000_000, latencyMs: 500);

        var cheap = Row(metrics, Cheap)!;
        cheap.InputCost.Should().Be(2m);
        cheap.OutputCost.Should().Be(8m);
        metrics.GetSnapshot().TotalCost.Should().Be(10m,
            "a session that cost money must not total to zero");
    }

    // ── The hazard the fix must not introduce ───────────────────────────────────

    [Fact]
    public void A_smaller_sibling_model_is_never_folded_into_its_larger_namesake()
    {
        // `gpt-4.1-mini` starts with `gpt-4.1`. Prefix matching would price it at the big model's
        // rate — five times too high here — which is the one error worse than reporting nothing.
        var metrics = Build();

        metrics.RecordModelUsage("gpt-4.1-mini", 1_000_000, 0, latencyMs: 100);

        Row(metrics, "gpt-4.1-mini").Should().NotBeNull("it is a different model and keeps its own row");
        Row(metrics, "gpt-4.1-mini")!.InputCost.Should().BeNull("nobody configured a price for it");
        Row(metrics, Cheap)!.RequestCount.Should().Be(0, "the larger model served nothing");
    }

    [Fact]
    public void An_unrelated_model_still_reports_an_unknown_cost()
    {
        // The never-fabricate rule, unchanged: normalisation must not become a way to guess a price
        // by finding the nearest configured neighbour.
        var metrics = Build();

        metrics.RecordModelUsage("claude-sonnet-5", 1_000, 100, latencyMs: 300);

        var row = Row(metrics, "claude-sonnet-5")!;
        row.Kind.Should().Be(MentorModelKind.Other);
        row.InputCost.Should().BeNull();
    }

    [Fact]
    public void A_date_suffix_on_a_model_nobody_configured_is_left_alone()
    {
        // Stripping the suffix must only ever *redirect to something known*. With no match, the id
        // stays exactly as the provider reported it — inventing `claude-sonnet` here would create a
        // row for a model that was never used.
        var metrics = Build();

        metrics.RecordModelUsage("claude-sonnet-2026-01-01", 10, 10, latencyMs: 10);

        Row(metrics, "claude-sonnet-2026-01-01").Should().NotBeNull();
        Row(metrics, "claude-sonnet").Should().BeNull();
    }
}
