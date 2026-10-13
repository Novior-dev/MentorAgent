using Bunit;
using FluentAssertions;
using Novior.MentorAgent.Abstractions.Components;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Tests.Components;
using Novior.MentorAgent.Tests.Fakes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Novior.MentorAgent.Tests.Theming;

/// <summary>
/// BUG-150 — what the widget asks of a page's Content-Security-Policy.
///
/// <para>
/// Without <c>style-src 'unsafe-inline'</c> the browser refuses every <c>style</c> attribute and
/// every <c>&lt;style&gt;</c> element, including the ones Blazor sets from script, and reports each in
/// the console. The avatar carried its size in a style attribute, so under <c>default-src 'self'</c>
/// the default widget logged errors and showed the image at its own size; an aligned Markdown table
/// lost its alignment the same way. With the default options the widget now renders neither; the
/// theme block is the documented exception.
/// </para>
/// </summary>
public sealed class InlineStyleTests : ComponentTestBase
{
    public InlineStyleTests()
    {
        Services.AddSingleton<IMentorStateService>(new RecordingStateService());
        Services.AddSingleton<IMentorOrchestrator>(new StubOrchestrator());
        Services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
    }

    private static string InlineStyles<T>(IRenderedComponent<T> cut) where T : Microsoft.AspNetCore.Components.IComponent
        => string.Join(" | ", cut.FindAll("[style], style").Select(e => e.OuterHtml));

    [Fact]
    public void With_the_default_options_the_widget_renders_no_inline_style()
    {
        var widget = Render<ChatWidget>();
        widget.Find(".bm-fab").Click();
        var bubble = Render<MessageBubble>(p => p.Add(c => c.Message, new ChatMessage(ChatRole.Assistant, """
            | Order | Total |
            | :---: | ---: |
            | 1002 | 40 |
            """)));
        var typing = Render<TypingIndicator>();

        // The header and the welcome panel, then an answer and the typing indicator: the four places
        // the avatar appears. The answer is an aligned table, whose alignment was a style attribute.
        widget.FindAll("img.bm-avatar-img").Should().HaveCount(2);
        bubble.FindAll("img.bm-avatar-img").Should().ContainSingle();
        typing.FindAll("img.bm-avatar-img").Should().ContainSingle();

        const string because = "the default widget must work under default-src 'self'";
        InlineStyles(widget).Should().BeEmpty(because);
        InlineStyles(bubble).Should().BeEmpty(because);
        InlineStyles(typing).Should().BeEmpty(because);
    }

    [Theory]
    [InlineData(MentorTheme.Dark, null)]
    [InlineData(MentorTheme.Minimal, null)]
    [InlineData(MentorTheme.Default, "#7c3aed")]
    public void A_theme_or_a_primary_colour_still_needs_unsafe_inline(MentorTheme theme, string? primaryColor)
    {
        // The documented exception: these write their variables in a <style> block on :root. If this
        // starts failing because the block is gone, the README's CSP note can lose the exception too.
        Widget.Theme = theme;
        Widget.PrimaryColor = primaryColor;

        var widget = Render<ChatWidget>();

        widget.FindAll("style").Should().ContainSingle().Which.TextContent.Should().Contain(":root");
        widget.FindAll("[style]").Should().BeEmpty();
    }

    /// <summary>Does nothing; this file is about markup, not turns.</summary>
    private sealed class StubOrchestrator : IMentorOrchestrator
    {
        public Task SendMessageAsync(string userMessage, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task SendMessageAsync(string userMessage, IReadOnlyList<MentorAttachment>? attachments,
                                     CancellationToken ct = default)
            => Task.CompletedTask;

        public void CancelCurrentRequest() { }

        public Task ResetSessionAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<System.Text.Json.JsonElement?> SerializeSessionAsync(CancellationToken ct = default)
            => Task.FromResult<System.Text.Json.JsonElement?>(null);

        public Task RestoreSessionAsync(System.Text.Json.JsonElement snapshot, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task RespondToApprovalAsync(ConfirmationRequest req, bool approved, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
