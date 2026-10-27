using Bunit;
using FluentAssertions;
using Novior.MentorAgent.Abstractions.Components;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Tests.Components;
using Novior.MentorAgent.Tests.Docs;
using Novior.MentorAgent.Tests.Fakes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Novior.MentorAgent.Tests.Theming;

/// <summary>
/// BUG-150, BUG-213 — what the widget asks of a page's Content-Security-Policy.
///
/// <para>
/// Without <c>style-src 'unsafe-inline'</c> the browser refuses every <c>style</c> attribute and
/// every <c>&lt;style&gt;</c> element, including the ones Blazor sets from script, and reports each in
/// the console. The avatar carried its size in a style attribute, so under <c>default-src 'self'</c>
/// the default widget logged errors and showed the image at its own size; an aligned Markdown table
/// lost its alignment the same way (BUG-150). The Dark and Minimal themes and a <c>PrimaryColor</c>
/// wrote their variables in a <c>&lt;style&gt;</c> block, which such a page dropped, and the dashboard
/// drew its legend and bars with style attributes (BUG-213). Now neither renders either, with any option.
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

    public static TheoryData<MentorTheme, string?> EveryThemeWithAndWithoutAColour()
    {
        var data = new TheoryData<MentorTheme, string?>();
        foreach (var theme in Enum.GetValues<MentorTheme>())
            foreach (var colour in new[] { null, "#7c3aed", "rebeccapurple" })
                data.Add(theme, colour);
        return data;
    }

    [Theory]
    [MemberData(nameof(EveryThemeWithAndWithoutAColour))]
    public void No_theme_and_no_primary_colour_renders_an_inline_style(MentorTheme theme, string? primaryColor)
    {
        // Every theme, a hex colour whose shades are derived and a named one that is applied as it is: the
        // README promised default-src 'self' only for the default options, and kept these as the exception.
        Widget.Theme = theme;
        Widget.PrimaryColor = primaryColor;

        var widget = Render<ChatWidget>();
        widget.Find(".bm-fab").Click();

        InlineStyles(widget).Should().BeEmpty("the widget must work under default-src 'self' with any option");
    }

    [Fact]
    public void No_component_writes_a_style_attribute_or_a_style_element()
    {
        // The render tests see the components and the options they render. This reads every component and
        // script of the five packages, so a style attribute in one no test renders, or a script that writes
        // one, fails here and not on a page with a strict policy. style.setProperty is allowed: a policy
        // without 'unsafe-inline' refuses markup, not the CSSOM.
        var markup = new System.Text.RegularExpressions.Regex(
            @"\bstyle\s*=\s*\\?[""']|<style[\s>]|""style""|setAttribute\(\s*['""]style['""]|\.cssText\b");

        var found = RepoFiles.SourceFiles
            .Where(f => !f.EndsWith(".css", StringComparison.OrdinalIgnoreCase))
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (f, n: i + 1, line)))
            .Where(x => !IsComment(x.line) && markup.IsMatch(x.line))
            .Select(x => $"{Path.GetRelativePath(RepoFiles.Root, x.f)}:{x.n}: {x.line.Trim()}");

        string.Join(Environment.NewLine, found).Should().BeEmpty();

        static bool IsComment(string line)
        {
            var t = line.TrimStart();
            return t.StartsWith("//", StringComparison.Ordinal) || t.StartsWith('*')
                || t.StartsWith("@*", StringComparison.Ordinal) || t.StartsWith("/*", StringComparison.Ordinal);
        }
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
