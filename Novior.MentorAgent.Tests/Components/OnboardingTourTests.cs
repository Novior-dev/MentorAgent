using Bunit;
using FluentAssertions;
using Novior.MentorAgent.Abstractions.Components;
using Novior.MentorAgent.Abstractions.Models;
using Xunit;

namespace Novior.MentorAgent.Tests.Components;

/// <summary>
/// The onboarding tour is a small state machine, and the defects it has already shipped were all
/// state defects rather than markup ones — a tour that would not start, and a replay that did
/// nothing. Its whole reachable state space is walked here: first step, middle, last, and the
/// resume paths including a tour that shrank between deployments.
/// </summary>
public sealed class OnboardingTourTests : ComponentTestBase
{
    private static readonly IReadOnlyList<MentorTourStep> ThreeSteps =
    [
        new("Welcome", "This is the assistant.", TryAsking: "What can you do?"),
        new("Orders", "Track and cancel orders.", Url: "/orders"),
        new("Reports", "Export the monthly ledger."),
    ];

    private IRenderedComponent<OnboardingTour> RenderTour(
        IReadOnlyList<MentorTourStep>? steps = null,
        int startIndex = 0,
        Action? onClose = null,
        Action<string>? onNavigate = null,
        Action<string>? onAsk = null,
        Action<int>? onIndexChanged = null)
        => Render<OnboardingTour>(p =>
        {
            p.Add(c => c.Steps, steps ?? ThreeSteps);
            p.Add(c => c.StartIndex, startIndex);
            if (onClose is not null) p.Add(c => c.OnClose, onClose);
            if (onNavigate is not null) p.Add(c => c.OnNavigate, onNavigate);
            if (onAsk is not null) p.Add(c => c.OnAsk, onAsk);
            if (onIndexChanged is not null) p.Add(c => c.OnIndexChanged, onIndexChanged);
        });

    // ── Rendering ───────────────────────────────────────────────────────────────

    [Fact]
    public void An_empty_tour_renders_nothing()
    {
        // The generated tour can legitimately come back empty — no pages, no actions to describe.
        // An empty dialog with "Step 1 of 0" is worse than no tour.
        RenderTour([]).Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public void The_first_step_shows_its_title_body_and_position()
    {
        var cut = RenderTour();

        cut.Find(".bm-tour__title").TextContent.Should().Be("Welcome");
        cut.Find(".bm-tour__text").TextContent.Should().Be("This is the assistant.");
        cut.Find(".bm-tour__count").TextContent.Should().Be("Step 1 of 3");
    }

    [Fact]
    public void One_dot_per_step_with_exactly_one_marked_current()
    {
        var cut = RenderTour();

        cut.FindAll(".bm-tour__dot").Should().HaveCount(3);
        cut.FindAll(".bm-tour__dot--on").Should().HaveCount(1);
    }

    [Fact]
    public void It_is_announced_as_a_modal_dialog_labelled_with_the_current_step()
    {
        var cut = RenderTour();

        var dialog = cut.Find(".bm-tour");
        dialog.GetAttribute("role").Should().Be("dialog");
        dialog.GetAttribute("aria-modal").Should().Be("true");
        dialog.GetAttribute("aria-label").Should().Be("Welcome");
    }

    // ── Navigation ──────────────────────────────────────────────────────────────

    [Fact]
    public void Back_is_disabled_on_the_first_step_and_enabled_after_it()
    {
        var cut = RenderTour();
        cut.Find(".bm-btn--ghost").HasAttribute("disabled").Should().BeTrue();

        cut.Find(".bm-btn--primary").Click();
        cut.Find(".bm-btn--ghost").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void Next_advances_and_back_returns_to_the_same_step()
    {
        var cut = RenderTour();

        cut.Find(".bm-btn--primary").Click();
        cut.Find(".bm-tour__title").TextContent.Should().Be("Orders");

        cut.Find(".bm-btn--ghost").Click();
        cut.Find(".bm-tour__title").TextContent.Should().Be("Welcome");
    }

    [Fact]
    public void The_primary_button_says_next_until_the_last_step_where_it_says_done()
    {
        var cut = RenderTour();
        cut.Find(".bm-btn--primary").TextContent.Trim().Should().Be("Next");

        cut.Find(".bm-btn--primary").Click();
        cut.Find(".bm-btn--primary").TextContent.Trim().Should().Be("Next");

        cut.Find(".bm-btn--primary").Click();
        cut.Find(".bm-btn--primary").TextContent.Trim().Should().Be("Done",
            "a Next that ends the tour is why people click past the last step looking for more");
    }

    [Fact]
    public void Done_on_the_last_step_closes_the_tour()
    {
        var closed = 0;
        var cut = RenderTour(startIndex: 2, onClose: () => closed++);

        cut.Find(".bm-btn--primary").Click();

        closed.Should().Be(1);
    }

    [Fact]
    public void Skip_closes_the_tour_from_any_step()
    {
        // Skip and Done are deliberately the same outcome: both mean "do not show me this again".
        var closed = 0;
        var cut = RenderTour(startIndex: 1, onClose: () => closed++);

        cut.Find(".bm-tour__skip").Click();

        closed.Should().Be(1);
    }

    [Fact]
    public void Back_on_the_first_step_neither_moves_nor_notifies()
    {
        var indices = new List<int>();
        var cut = RenderTour(onIndexChanged: indices.Add);

        cut.Find(".bm-btn--ghost").Click();

        cut.Find(".bm-tour__count").TextContent.Should().Be("Step 1 of 3");
        indices.Should().BeEmpty("a no-op must not write a resume point");
    }

    // ── Resume ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Every_step_change_reports_the_new_index_so_the_host_can_persist_it()
    {
        var indices = new List<int>();
        var cut = RenderTour(onIndexChanged: indices.Add);

        cut.Find(".bm-btn--primary").Click();
        cut.Find(".bm-btn--primary").Click();
        cut.Find(".bm-btn--ghost").Click();

        string.Join(",", indices).Should().Be("1,2,1");
    }

    [Fact]
    public void Finishing_the_tour_does_not_report_an_index_past_the_end()
    {
        // Reaching Done must not persist index 3, or the next visit resumes out of range.
        var indices = new List<int>();
        var cut = RenderTour(startIndex: 2, onClose: () => { }, onIndexChanged: indices.Add);

        cut.Find(".bm-btn--primary").Click();

        indices.Should().BeEmpty();
    }

    [Fact]
    public void A_saved_position_reopens_on_that_step()
    {
        var cut = RenderTour(startIndex: 1);

        cut.Find(".bm-tour__count").TextContent.Should().Be("Step 2 of 3");
        cut.Find(".bm-tour__title").TextContent.Should().Be("Orders");
    }

    [Fact]
    public void A_saved_position_past_the_end_clamps_to_the_last_step()
    {
        // The documented case: the tour shrank between deployments and the stored index no longer
        // exists. Clamping is right — an onboarding tour is not worth an exception.
        var cut = RenderTour(startIndex: 99);

        cut.Find(".bm-tour__count").TextContent.Should().Be("Step 3 of 3");
    }

    [Fact]
    public void A_negative_saved_position_clamps_to_the_first_step()
    {
        var cut = RenderTour(startIndex: -5);

        cut.Find(".bm-tour__count").TextContent.Should().Be("Step 1 of 3");
    }

    [Fact]
    public void A_single_step_tour_offers_done_immediately_and_disables_back()
    {
        var cut = RenderTour([new("Only", "Just this one.")]);

        cut.Find(".bm-btn--primary").TextContent.Trim().Should().Be("Done");
        cut.Find(".bm-btn--ghost").HasAttribute("disabled").Should().BeTrue();
        cut.FindAll(".bm-tour__dot").Should().HaveCount(1);
    }

    // ── The two call-to-action buttons ──────────────────────────────────────────

    [Fact]
    public void The_ask_button_appears_only_for_a_step_that_supplies_a_question()
    {
        RenderTour().FindAll(".bm-tour__ask").Should().HaveCount(1, "step 1 has TryAsking");

        var cut = RenderTour(startIndex: 2);
        cut.FindAll(".bm-tour__ask").Should().BeEmpty("step 3 has none");
    }

    [Fact]
    public void The_ask_button_sends_that_steps_question()
    {
        string? asked = null;
        var cut = RenderTour(onAsk: q => asked = q);

        cut.Find(".bm-tour__ask").Click();

        asked.Should().Be("What can you do?");
    }

    [Fact]
    public void The_open_button_appears_only_for_a_step_with_a_url_and_navigates_to_it()
    {
        RenderTour().FindAll(".bm-tour__link").Should().BeEmpty("step 1 has no Url");

        string? navigated = null;
        var cut = RenderTour(startIndex: 1, onNavigate: u => navigated = u);
        cut.Find(".bm-tour__link").Click();

        navigated.Should().Be("/orders");
    }

    [Fact]
    public void Both_call_to_action_buttons_can_appear_on_the_same_step()
    {
        var cut = RenderTour([new("Both", "Body", Url: "/x", TryAsking: "how?")]);

        cut.FindAll(".bm-tour__ask").Should().HaveCount(1);
        cut.FindAll(".bm-tour__link").Should().HaveCount(1);
    }

    [Fact]
    public void A_whitespace_only_url_or_question_does_not_produce_a_dead_button()
    {
        var cut = RenderTour([new("Blank", "Body", Url: "   ", TryAsking: "  ")]);

        cut.FindAll(".bm-tour__link").Should().BeEmpty();
        cut.FindAll(".bm-tour__ask").Should().BeEmpty();
    }

    // ── Localisation and encoding ───────────────────────────────────────────────

    [Fact]
    public void The_chrome_follows_the_configured_language()
    {
        Widget.Language = MentorLanguage.Italian;

        var cut = RenderTour();

        cut.Find(".bm-tour__count").TextContent.Should().Be("Passo 1 di 3");
        cut.Find(".bm-tour__skip").TextContent.Trim().Should().Be("Salta il tour");
        cut.Find(".bm-btn--primary").TextContent.Trim().Should().Be("Avanti");
    }

    [Fact]
    public void A_generated_step_containing_markup_is_rendered_as_text()
    {
        // Tour steps are generated by the model from the app's own pages and tools, so the title
        // and body are model output like any other.
        var cut = RenderTour([new("<script>alert(1)</script>", "<img src=x onerror=alert(1)>")]);

        cut.Find(".bm-tour").QuerySelectorAll("script, img").Should().BeEmpty();
        cut.Find(".bm-tour__title").TextContent.Should().Be("<script>alert(1)</script>");
    }
}
