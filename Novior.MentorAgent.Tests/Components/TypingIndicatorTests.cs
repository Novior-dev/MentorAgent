using Bunit;
using FluentAssertions;
using Novior.MentorAgent.Abstractions.Components;
using Xunit;

namespace Novior.MentorAgent.Tests.Components;

/// <summary>
/// The smallest component there is, which makes it the right place to pin the two things every
/// other component test depends on: that the render pipeline works at all, and that the avatar
/// fallback resolves to the packaged icon rather than an empty <c>src</c>.
/// </summary>
public sealed class TypingIndicatorTests : ComponentTestBase
{
    [Fact]
    public void Renders_the_three_dots_the_css_animates()
    {
        var cut = Render<TypingIndicator>();

        cut.Find(".bm-typing-dots").Children.Should().HaveCount(3,
            "the CSS animates the three spans with staggered delays; a different count breaks it silently");
    }

    [Fact]
    public void Falls_back_to_the_packaged_avatar_when_none_is_configured()
    {
        var cut = Render<TypingIndicator>();

        cut.Find(".bm-bubble__avatar img").GetAttribute("src")
            .Should().Be("_content/Novior.MentorAgent.Abstractions/IconMA.png",
                "an empty src makes the browser re-request the page itself as an image");
    }

    [Fact]
    public void Uses_the_configured_avatar_when_one_is_set()
    {
        Widget.AvatarUrl = "https://cdn.example.com/bot.png";

        var cut = Render<TypingIndicator>();

        cut.Find(".bm-bubble__avatar img").GetAttribute("src").Should().Be("https://cdn.example.com/bot.png");
    }

    [Fact]
    public void Carries_the_assistant_modifier_so_it_aligns_with_assistant_bubbles()
    {
        var cut = Render<TypingIndicator>();

        var classes = cut.Find(".bm-bubble").GetAttribute("class") ?? "";
        classes.Should().Contain("bm-bubble--assistant").And.Contain("bm-bubble--typing");
    }
}
