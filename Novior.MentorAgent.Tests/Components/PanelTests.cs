using Bunit;
using FluentAssertions;
using Novior.MentorAgent.Abstractions.Components;
using Novior.MentorAgent.Abstractions.Models;
using Xunit;

namespace Novior.MentorAgent.Tests.Components;

/// <summary>
/// The RAG citation panel, the welcome panel and the confirmation banner — the three components a
/// user sees before, during and at the end of a turn.
/// </summary>
public sealed class PanelTests : ComponentTestBase
{
    private static MentorRagResult Source(string? title, string? url, string content = "body text")
        => new(content, url, title, 0.8f);

    private IRenderedComponent<RagSourcePanel> RenderRag(params MentorRagResult[] sources)
    {
        Widget.ShowRagSources = true;
        return Render<RagSourcePanel>(p => p.Add(c => c.Sources, (IReadOnlyList<MentorRagResult>)sources));
    }

    // ── RAG source panel ────────────────────────────────────────────────────────

    [Fact]
    public void Shows_at_most_three_chips_and_a_counter_for_the_rest()
    {
        var cut = RenderRag(
            Source("A", "https://x.test/a"), Source("B", "https://x.test/b"),
            Source("C", "https://x.test/c"), Source("D", "https://x.test/d"),
            Source("E", "https://x.test/e"));

        cut.FindAll(".bm-rag-chip:not(.bm-rag-chip--more)").Should().HaveCount(3);
        cut.Find(".bm-rag-chip--more").TextContent.Trim().Should().Be("+2 more");
    }

    [Fact]
    public void Exactly_three_sources_show_no_more_button()
    {
        var cut = RenderRag(Source("A", "https://x.test/a"), Source("B", "https://x.test/b"), Source("C", "https://x.test/c"));

        cut.FindAll(".bm-rag-chip--more").Should().BeEmpty("+0 more is a button that does nothing");
    }

    [Fact]
    public void Clicking_more_reveals_every_source_and_removes_the_button()
    {
        var cut = RenderRag(
            Source("A", "https://x.test/a"), Source("B", "https://x.test/b"),
            Source("C", "https://x.test/c"), Source("D", "https://x.test/d"));

        cut.Find(".bm-rag-chip--more").Click();

        cut.FindAll(".bm-rag-chip").Should().HaveCount(4);
        cut.FindAll(".bm-rag-chip--more").Should().BeEmpty();
    }

    [Fact]
    public void A_source_with_a_url_is_a_link_that_opens_safely_in_a_new_tab()
    {
        var cut = RenderRag(Source("Handbook", "https://x.test/handbook.pdf"));

        var chip = cut.Find(".bm-rag-chip");
        chip.GetAttribute("href").Should().Be("https://x.test/handbook.pdf");
        chip.GetAttribute("target").Should().Be("_blank");
        chip.GetAttribute("rel").Should().Be("noopener noreferrer",
            "without noopener the opened page gets a handle on the host window");
    }

    [Fact]
    public void A_source_without_a_url_is_a_plain_chip_not_a_link_to_nowhere()
    {
        // Provider-hosted vector stores name a document without giving it an address. An href of
        // "#" would look like a broken download.
        var cut = RenderRag(Source("Internal doc", null));

        var chip = cut.Find(".bm-rag-chip");
        (chip.GetAttribute("class") ?? "").Should().Contain("bm-rag-chip--plain");
        chip.HasAttribute("href").Should().BeFalse();
        chip.HasAttribute("target").Should().BeFalse();
    }

    [Fact]
    public void A_whitespace_only_url_counts_as_no_url()
    {
        var cut = RenderRag(Source("Doc", "   "));

        cut.Find(".bm-rag-chip").HasAttribute("href").Should().BeFalse();
    }

    [Fact]
    public void A_titleless_source_falls_back_to_a_shortened_url()
    {
        var cut = RenderRag(Source(null, "https://www.example.test/docs/policies/refund-policy.pdf"));

        var label = cut.Find(".bm-rag-chip__label").TextContent;
        label.Should().Be("example.test/…/refund-policy.pdf",
            "the full URL would blow the chip out of the panel");
    }

    [Fact]
    public void A_malformed_url_still_produces_a_label_rather_than_throwing()
    {
        var cut = RenderRag(Source(null, "not a url at all"));

        cut.Find(".bm-rag-chip__label").TextContent.Should().NotBeEmpty();
    }

    [Fact]
    public void A_source_with_neither_title_nor_url_is_labelled_source()
    {
        var cut = RenderRag(Source(null, null));

        cut.Find(".bm-rag-chip__label").TextContent.Should().Be("Source");
    }

    [Fact]
    public void The_tooltip_of_an_untitled_unlinked_source_is_a_content_preview()
    {
        var cut = RenderRag(Source(null, null, new string('x', 200)));

        (cut.Find(".bm-rag-chip").GetAttribute("title") ?? "").Length.Should().Be(80,
            "the preview is capped at 80 characters; an uncapped tooltip covers the panel");
    }

    [Fact]
    public void Short_content_is_not_over_trimmed_by_the_preview_cap()
    {
        // Math.Min guards the other direction: content shorter than 80 characters must not throw.
        var cut = RenderRag(Source(null, null, "short"));

        cut.Find(".bm-rag-chip").GetAttribute("title").Should().Be("short");
    }

    [Fact]
    public void The_panel_hides_when_the_option_is_off_or_there_is_nothing_to_cite()
    {
        Widget.ShowRagSources = false;
        Render<RagSourcePanel>(p => p.Add(c => c.Sources, (IReadOnlyList<MentorRagResult>)[Source("A", "https://x.test/a")]))
            .Markup.Trim().Should().BeEmpty();

        Widget.ShowRagSources = true;
        Render<RagSourcePanel>(p => p.Add(c => c.Sources, (IReadOnlyList<MentorRagResult>)[]))
            .Markup.Trim().Should().BeEmpty();
        Render<RagSourcePanel>().Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public void A_source_title_containing_markup_is_rendered_as_text()
    {
        // Titles come from retrieved documents, which is untrusted content by definition.
        var cut = RenderRag(Source("<script>alert(1)</script>", "https://x.test/a"));

        cut.Find(".bm-rag-panel").QuerySelectorAll("script").Should().BeEmpty();
        cut.Find(".bm-rag-chip__label").TextContent.Should().Be("<script>alert(1)</script>");
    }

    // ── Welcome panel ───────────────────────────────────────────────────────────

    [Fact]
    public void The_welcome_panel_shows_the_bot_name_and_three_suggestion_chips()
    {
        var cut = Render<WelcomePanel>(p => p
            .Add(c => c.BotName, "Ada")
            .Add(c => c.OnChipClick, (string _) => { }));

        cut.Find(".bm-welcome__title").TextContent.Should().Be("Ada");
        cut.FindAll(".bm-chip").Should().HaveCount(3);
    }

    [Fact]
    public void Clicking_a_chip_sends_that_chips_own_text()
    {
        string? sent = null;
        var cut = Render<WelcomePanel>(p => p.Add(c => c.OnChipClick, (string s) => sent = s));

        var chips = cut.FindAll(".bm-chip");
        var secondLabel = chips[1].TextContent;
        chips[1].Click();

        sent.Should().Be(secondLabel, "each chip must send its own label, not the last one bound");
    }

    [Fact]
    public void Suggestion_chips_are_hidden_when_suggestions_are_off()
    {
        var cut = Render<WelcomePanel>(p => p
            .Add(c => c.EnableSuggestions, false)
            .Add(c => c.OnChipClick, (string _) => { }));

        cut.FindAll(".bm-chip").Should().BeEmpty();
    }

    [Fact]
    public void Suggestion_chips_are_hidden_when_nothing_is_listening()
    {
        // A chip with no handler looks clickable and does nothing, which reads as a broken widget.
        var cut = Render<WelcomePanel>();

        cut.FindAll(".bm-chip").Should().BeEmpty();
    }

    [Fact]
    public void A_custom_welcome_message_replaces_the_localised_default()
    {
        var cut = Render<WelcomePanel>(p => p.Add(c => c.WelcomeMessage, "Ask me about orders."));

        cut.Find(".bm-welcome__subtitle").TextContent.Trim().Should().Be("Ask me about orders.");
    }

    [Fact]
    public void The_chips_follow_the_configured_language()
    {
        Widget.Language = MentorLanguage.Italian;

        var cut = Render<WelcomePanel>(p => p.Add(c => c.OnChipClick, (string _) => { }));

        cut.FindAll(".bm-chip").Select(e => e.TextContent).Should().Contain(t => t.Contains("Cosa puoi fare"));
    }

    // ── Confirmation banner ─────────────────────────────────────────────────────

    [Fact]
    public void The_banner_shows_the_request_message_and_localised_buttons()
    {
        Widget.Language = MentorLanguage.Italian;

        var cut = Render<ConfirmationBanner>(p => p
            .Add(c => c.Request, new ConfirmationRequest { ActionId = Guid.NewGuid(), ToolName = "cancel_order", Message = "Cancel order 1001?" })
            .Add(c => c.OnConfirm, () => { })
            .Add(c => c.OnCancel, () => { }));

        cut.Find(".bm-confirm__desc").TextContent.Should().Be("Cancel order 1001?");
        cut.Find(".bm-confirm__title").TextContent.Should().Be("Conferma richiesta");
        cut.FindAll(".bm-confirm__actions button").Select(e => e.TextContent)
            .Should().Equal(["Annulla", "Conferma"], "cancel must come first, so Confirm is not the default target");
    }

    [Fact]
    public void Confirm_and_cancel_raise_their_own_callbacks_only()
    {
        var confirmed = 0;
        var cancelled = 0;

        var cut = Render<ConfirmationBanner>(p => p
            .Add(c => c.Request, new ConfirmationRequest { ActionId = Guid.NewGuid(), ToolName = "cancel_order", Message = "Delete everything?" })
            .Add(c => c.OnConfirm, () => confirmed++)
            .Add(c => c.OnCancel, () => cancelled++));

        cut.Find(".bm-btn--primary").Click();
        (confirmed, cancelled).Should().Be((1, 0));

        cut.Find(".bm-btn--ghost").Click();
        (confirmed, cancelled).Should().Be((1, 1));
    }

    [Fact]
    public void A_confirmation_message_containing_markup_is_rendered_as_text()
    {
        // The message is built from tool arguments the model chose, so it is the least trusted
        // string that reaches a modal the user is being asked to approve.
        var cut = Render<ConfirmationBanner>(p => p
            .Add(c => c.Request, new ConfirmationRequest { ActionId = Guid.NewGuid(), ToolName = "cancel_order", Message = "<img src=x onerror=alert(1)>" })
            .Add(c => c.OnConfirm, () => { })
            .Add(c => c.OnCancel, () => { }));

        cut.Find(".bm-confirm").QuerySelectorAll("img").Should().BeEmpty();
        cut.Find(".bm-confirm__desc").TextContent.Should().Be("<img src=x onerror=alert(1)>");
    }

    [Fact]
    public void The_full_message_is_available_as_a_tooltip_when_it_is_clipped()
    {
        var cut = Render<ConfirmationBanner>(p => p
            .Add(c => c.Request, new ConfirmationRequest { ActionId = Guid.NewGuid(), ToolName = "cancel_order", Message = "A very long confirmation message" })
            .Add(c => c.OnConfirm, () => { })
            .Add(c => c.OnCancel, () => { }));

        cut.Find(".bm-confirm__desc").GetAttribute("title").Should().Be("A very long confirmation message");
    }
}
