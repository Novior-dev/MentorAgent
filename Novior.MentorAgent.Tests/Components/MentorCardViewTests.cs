using Bunit;
using FluentAssertions;
using Novior.MentorAgent.Abstractions.Components;
using Novior.MentorAgent.Abstractions.Models;
using Xunit;

namespace Novior.MentorAgent.Tests.Components;

/// <summary>
/// Generative UI Level 2. A card is built by a tool and rendered with buttons, so it carries the
/// same encoding obligation as the message bubble — with the extra wrinkle that a card's buttons
/// carry URLs. The safety argument for giving a card buttons at all is that the values come from
/// application code, not the model; these tests hold the renderer to the other half of that
/// bargain by proving it never turns a value into markup.
/// </summary>
public sealed class MentorCardViewTests : ComponentTestBase
{
    private IRenderedComponent<MentorCardView> RenderCard(
        MentorCard card, Action<MentorCardAction>? onAction = null)
        => Render<MentorCardView>(p =>
        {
            p.Add(c => c.Card, card);
            if (onAction is not null) p.Add(c => c.OnAction, onAction);
        });

    // ── Structure ───────────────────────────────────────────────────────────────

    [Fact]
    public void Renders_title_subtitle_and_every_field_in_order()
    {
        var cut = RenderCard(new MentorCard("order")
        {
            Title = "Order #1001",
            Subtitle = "Ada Lovelace",
            Fields = [new("Total", "€ 42,00"), new("Status", "Shipped")],
        });

        cut.Find(".bm-card__title").TextContent.Should().Be("Order #1001");
        cut.Find(".bm-card__subtitle").TextContent.Should().Be("Ada Lovelace");
        cut.FindAll(".bm-card__label").Select(e => e.TextContent).Should().Equal(["Total", "Status"]);
        cut.FindAll(".bm-card__value").Select(e => e.TextContent).Should().Equal(["€ 42,00", "Shipped"]);
    }

    [Fact]
    public void A_card_with_only_a_kind_renders_without_an_empty_header()
    {
        // The minimum a tool can return. An empty <div class="bm-card__head"> would still take
        // its padding and leave a visible gap at the top of the card.
        var cut = RenderCard(new MentorCard("bare"));

        cut.FindAll(".bm-card__head").Should().BeEmpty();
        cut.FindAll(".bm-card__fields").Should().BeEmpty();
        cut.FindAll(".bm-card__actions").Should().BeEmpty();
        cut.FindAll(".bm-card").Should().HaveCount(1);
    }

    [Fact]
    public void A_field_with_a_null_value_renders_an_empty_cell_not_the_word_null()
    {
        var cut = RenderCard(new MentorCard("x") { Fields = [new("Delivered", null)] });

        cut.Find(".bm-card__value").TextContent.Should().BeEmpty();
    }

    [Fact]
    public void An_image_url_renders_a_lazy_image_and_no_url_renders_none()
    {
        var withImage = RenderCard(new MentorCard("p") { ImageUrl = "https://cdn.test/p.png", Title = "P" });
        withImage.Find("img.bm-card__img").GetAttribute("src").Should().Be("https://cdn.test/p.png");
        withImage.Find("img.bm-card__img").GetAttribute("loading").Should().Be("lazy");

        RenderCard(new MentorCard("p") { Title = "P" }).FindAll("img.bm-card__img").Should().BeEmpty();
    }

    [Fact]
    public void The_image_alt_falls_back_to_the_kind_when_there_is_no_title()
    {
        var cut = RenderCard(new MentorCard("invoice") { ImageUrl = "https://cdn.test/i.png" });

        cut.Find("img.bm-card__img").GetAttribute("alt").Should().Be("invoice",
            "an empty alt on a content image fails accessibility review");
    }

    // ── Accents ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(MentorCardAccent.Default, "bm-card--default")]
    [InlineData(MentorCardAccent.Success, "bm-card--success")]
    [InlineData(MentorCardAccent.Warning, "bm-card--warning")]
    [InlineData(MentorCardAccent.Danger, "bm-card--danger")]
    [InlineData(MentorCardAccent.Info, "bm-card--info")]
    public void Every_accent_maps_to_its_own_class(MentorCardAccent accent, string expected)
    {
        // Enumerated exhaustively on purpose: a new accent added to the enum without a switch arm
        // falls into the default branch and silently renders grey.
        var cut = RenderCard(new MentorCard("x") { Accent = accent });

        (cut.Find(".bm-card").GetAttribute("class") ?? "").Should().Contain(expected);
    }

    // ── Actions ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Every_action_becomes_a_button_labelled_with_its_label()
    {
        var cut = RenderCard(new MentorCard("order")
        {
            Actions =
            [
                new("Open", MentorCardActionKind.Navigate, "/orders/1"),
                new("Track", MentorCardActionKind.SendMessage, "where is order 1?"),
            ],
        });

        cut.FindAll(".bm-card__actions button").Select(e => e.TextContent).Should().Equal(["Open", "Track"]);
    }

    [Fact]
    public void Pressing_a_button_raises_that_exact_action()
    {
        // The closure captures the loop variable, which is the classic way for every button in a
        // list to end up raising the last item.
        MentorCardAction? raised = null;
        var cut = RenderCard(
            new MentorCard("order")
            {
                Actions =
                [
                    new("First", MentorCardActionKind.Navigate, "/a"),
                    new("Second", MentorCardActionKind.SendMessage, "b"),
                    new("Third", MentorCardActionKind.Navigate, "/c"),
                ],
            },
            a => raised = a);

        cut.FindAll(".bm-card__actions button")[0].Click();
        raised!.Value.Should().Be("/a");

        cut.FindAll(".bm-card__actions button")[1].Click();
        raised!.Label.Should().Be("Second");
        raised.Kind.Should().Be(MentorCardActionKind.SendMessage);
    }

    [Fact]
    public void Buttons_are_type_button_so_they_never_submit_a_surrounding_form()
    {
        var cut = RenderCard(new MentorCard("x")
        {
            Actions = [new("Go", MentorCardActionKind.Navigate, "/x")],
        });

        cut.Find(".bm-card__actions button").GetAttribute("type").Should().Be("button",
            "a card can be rendered inside a host page's form, where a default-type button submits it");
    }

    // ── XSS ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Markup_in_any_card_string_is_rendered_as_text()
    {
        const string payload = "<img src=x onerror=alert(1)>";

        var cut = RenderCard(new MentorCard("x")
        {
            Title = payload,
            Subtitle = payload,
            Fields = [new(payload, payload)],
            Actions = [new(payload, MentorCardActionKind.SendMessage, payload)],
        });

        // The image url is deliberately excluded: it is an attribute, and Blazor encodes those.
        cut.Find(".bm-card").QuerySelectorAll("img").Should().BeEmpty(
            "every card string must be encoded, not just the title");
        cut.Find(".bm-card__title").TextContent.Should().Be(payload);
        cut.Find(".bm-card__value").TextContent.Should().Be(payload);
        cut.Find(".bm-card__actions button").TextContent.Should().Be(payload);
    }
}
