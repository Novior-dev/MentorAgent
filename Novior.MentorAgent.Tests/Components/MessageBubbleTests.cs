using Bunit;
using FluentAssertions;
using Novior.MentorAgent.Abstractions.Components;
using Novior.MentorAgent.Abstractions.Models;
using Microsoft.Extensions.AI;
using Xunit;

namespace Novior.MentorAgent.Tests.Components;

/// <summary>
/// The message bubble is the only place in the library where model output is turned into markup,
/// which makes it the security boundary of the whole widget. Everything the model says arrives
/// here, and the renderer emits a <c>MarkupString</c> — so the guarantee "encode before emitting
/// any tag" is not a style preference, it is what stops a tool result from becoming script.
///
/// <para>
/// These tests are written to fail loudly if anyone ever swaps the hand-rolled renderer for a
/// Markdown library that passes raw HTML through, which is the obvious-looking "improvement" that
/// would silently reopen the hole.
/// </para>
/// </summary>
public sealed class MessageBubbleTests : ComponentTestBase
{
    private static ChatMessage Assistant(string text) => new(ChatRole.Assistant, text);
    private static ChatMessage User(string text) => new(ChatRole.User, text);

    private IRenderedComponent<MessageBubble> RenderBubble(ChatMessage message, bool streaming = false)
        => Render<MessageBubble>(p => p
            .Add(c => c.Message, message)
            .Add(c => c.IsStreaming, streaming));

    // ── XSS: the model supplies data, never markup ──────────────────────────────

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("<img src=x onerror=alert(1)>")]
    [InlineData("<iframe src=\"javascript:alert(1)\"></iframe>")]
    [InlineData("<svg/onload=alert(1)>")]
    [InlineData("<a href=\"javascript:alert(1)\">click</a>")]
    [InlineData("<style>body{display:none}</style>")]
    [InlineData("<body onload=alert(1)>")]
    public void Html_in_model_text_is_rendered_as_text_not_markup(string payload)
    {
        var cut = RenderBubble(Assistant(payload));

        var content = cut.Find(".bm-bubble__content");

        // Two independent checks. The first says no element was created; the second says the
        // characters survived — a renderer that simply stripped the payload would pass the first
        // and fail the second, and stripping is not the documented behaviour.
        content.QuerySelectorAll("script, iframe, img, svg, style, a").Should().BeEmpty(
            "model text must never become an element");
        content.TextContent.Should().Contain(payload,
            "the payload has to be visible as literal text, not silently discarded");
    }

    [Fact]
    public void Html_inside_a_markdown_table_cell_is_encoded_too()
    {
        // The table path builds tags by hand with string concatenation, so it is the one most
        // likely to forget to encode. It goes through RenderInline for every cell — this pins that.
        var cut = RenderBubble(Assistant("""
            | Name | Note |
            | --- | --- |
            | <script>alert(1)</script> | ok |
            """));

        var cell = cut.Find(".bm-table tbody td");
        cell.QuerySelectorAll("script").Should().BeEmpty();
        cell.TextContent.Should().Contain("<script>");
    }

    [Fact]
    public void Html_inside_a_list_item_is_encoded_too()
    {
        var cut = RenderBubble(Assistant("- <img src=x onerror=alert(1)>"));

        var item = cut.Find(".bm-list li");
        item.QuerySelectorAll("img").Should().BeEmpty();
        item.TextContent.Should().Contain("<img");
    }

    [Fact]
    public void Html_inside_a_fenced_code_block_is_encoded_too()
    {
        var cut = RenderBubble(Assistant("```html\n<script>alert(1)</script>\n```"));

        var code = cut.Find("pre.bm-code code");
        code.QuerySelectorAll("script").Should().BeEmpty();
        code.TextContent.Should().Contain("<script>alert(1)</script>");
    }

    [Fact]
    public void Html_inside_a_table_header_is_encoded_too()
    {
        var cut = RenderBubble(Assistant("""
            | <b>bold</b> | x |
            | --- | --- |
            | a | b |
            """));

        var th = cut.Find(".bm-table thead th");
        th.QuerySelectorAll("b").Should().BeEmpty();
        th.TextContent.Should().Contain("<b>bold</b>");
    }

    // ── Markdown, Generative UI Level 1 ─────────────────────────────────────────

    [Fact]
    public void Renders_a_github_table_with_a_header_and_one_row_per_line()
    {
        var cut = RenderBubble(Assistant("""
            | Order | Status |
            | --- | --- |
            | 1001 | Shipped |
            | 1002 | Pending |
            """));

        cut.FindAll(".bm-table thead th").Select(e => e.TextContent).Should().Equal(["Order", "Status"]);
        cut.FindAll(".bm-table tbody tr").Should().HaveCount(2);
        cut.FindAll(".bm-table tbody tr")[1].QuerySelectorAll("td").Select(e => e.TextContent)
            .Should().Equal(["1002", "Pending"]);
    }

    [Fact]
    public void Table_alignment_markers_become_classes()
    {
        // Classes, not style attributes (BUG-150): under a CSP without style-src 'unsafe-inline' a
        // style attribute is refused, so the column lost its alignment and the console an error.
        var cut = RenderBubble(Assistant("""
            | L | C | R |
            | :--- | :---: | ---: |
            | a | b | c |
            """));

        var headers = cut.FindAll(".bm-table thead th");
        (headers[0].GetAttribute("class") ?? "").Should().BeEmpty("left is the default, no class needed");
        headers[1].ClassList.Should().Contain("bm-align-center");
        headers[2].ClassList.Should().Contain("bm-align-right");
        cut.Find(".bm-table tbody td:nth-child(3)").ClassList.Should().Contain("bm-align-right");
        cut.FindAll(".bm-table [style]").Should().BeEmpty();
    }

    [Fact]
    public void A_cell_holding_only_a_number_an_amount_a_date_or_a_time_is_marked_to_stay_on_one_line()
    {
        // BUG-193: in a narrow bubble a browser broke "2026-10-16" after a hyphen. The stylesheet keeps a bm-num
        // cell on one line; a cell with words still wraps between them.
        var cut = RenderBubble(Assistant("""
            | No. | Date | Amount | Time | Employee | When |
            | --- | --- | ---: | --- | --- | --- |
            | 1008 | 2026-10-16 | €1,412.30 | 10:30 | Paolo Marino | 16 Oct 2026 |
            """));

        var cells = cut.FindAll(".bm-table tbody td");
        cells.Take(4).Should().AllSatisfy(c => c.ClassList.Should().Contain("bm-num"));
        cells[2].ClassList.Should().Contain("bm-align-right", "the alignment class stays beside it");
        cells.Skip(4).Should().AllSatisfy(c => c.ClassList.Should().NotContain("bm-num"));
        cut.FindAll(".bm-table thead th.bm-num").Should().BeEmpty("headers are kept on one line by the stylesheet already");
    }

    [Fact]
    public void A_row_with_fewer_cells_than_headers_is_padded_rather_than_dropped()
    {
        // A truncated stream produces exactly this. Losing the row entirely would hide data;
        // an index-out-of-range would take the whole bubble down.
        var cut = RenderBubble(Assistant("""
            | A | B | C |
            | --- | --- | --- |
            | 1 | 2 |
            """));

        cut.FindAll(".bm-table tbody td").Should().HaveCount(3);
        cut.FindAll(".bm-table tbody td")[2].TextContent.Should().BeEmpty();
    }

    [Fact]
    public void A_pipe_line_without_a_separator_row_is_not_a_table()
    {
        // Ordinary prose containing a pipe — "cat | grep", a table being streamed one line at a
        // time — must not flash a malformed table into the DOM.
        var cut = RenderBubble(Assistant("run `ls | grep foo` to filter"));

        cut.FindAll(".bm-table").Should().BeEmpty();
    }

    [Theory]
    [InlineData("- one\n- two", "ul", 2)]
    [InlineData("* one\n* two\n* three", "ul", 3)]
    [InlineData("+ one\n+ two", "ul", 2)]
    [InlineData("1. one\n2. two", "ol", 2)]
    public void Bullet_and_numbered_lists_render_as_the_right_element(string markdown, string tag, int items)
    {
        var cut = RenderBubble(Assistant(markdown));

        cut.FindAll($"{tag}.bm-list").Should().HaveCount(1);
        cut.FindAll($"{tag}.bm-list li").Should().HaveCount(items);
    }

    [Fact]
    public void List_markers_are_stripped_from_the_item_text()
    {
        var cut = RenderBubble(Assistant("- first item"));

        cut.Find(".bm-list li").TextContent.Should().Be("first item");
    }

    [Fact]
    public void Bold_italic_and_inline_code_render_as_elements()
    {
        var cut = RenderBubble(Assistant("**bold** and *italic* and `code`"));

        cut.Find("strong").TextContent.Should().Be("bold");
        cut.Find("em").TextContent.Should().Be("italic");
        cut.Find("code.bm-inline-code").TextContent.Should().Be("code");
    }

    [Fact]
    public void A_fenced_code_block_is_not_parsed_as_markdown()
    {
        // The whole reason code blocks are extracted first: a table or a list inside a snippet is
        // sample text, not UI. Rendering it as a table is a correctness bug and looks broken.
        var cut = RenderBubble(Assistant("""
            ```
            | not | a table |
            | --- | --- |
            - not a list
            ```
            """));

        cut.FindAll(".bm-table").Should().BeEmpty();
        cut.FindAll(".bm-list").Should().BeEmpty();
        cut.Find("pre.bm-code code").TextContent.Should().Contain("| not | a table |");
    }

    [Fact]
    public void Plain_newlines_become_line_breaks()
    {
        var cut = RenderBubble(Assistant("line one\nline two"));

        cut.FindAll(".bm-bubble__content br").Should().HaveCount(1,
            "the trailing line must not add a break after the last line");
    }

    [Fact]
    public void Empty_text_renders_an_empty_bubble_rather_than_throwing()
    {
        var cut = RenderBubble(Assistant(""));

        cut.Find(".bm-bubble__content").TextContent.Trim().Should().BeEmpty();
    }

    [Fact]
    public void Text_that_looks_like_an_internal_placeholder_is_shown_as_it_is()
    {
        // The renderer swaps code blocks and links for numbered placeholders and puts them back at the end. A
        // model that writes one itself, with no block or link of that number, must not throw out of the render.
        var cut = RenderBubble(Assistant("@@BMCODE7@@ and @@BMLINK3@@"));

        cut.Find(".bm-bubble__content").TextContent.Should().Be("@@BMCODE7@@ and @@BMLINK3@@");
    }

    // ── Links (BUG-215) ─────────────────────────────────────────────────────────

    [Fact]
    public void A_markdown_link_to_an_https_address_becomes_a_link_that_opens_in_a_new_tab()
    {
        // It stayed raw text, brackets and parentheses included, and could not be clicked.
        var cut = RenderBubble(Assistant("See the [Guide](https://example.com/a) for details."));

        var link = cut.Find(".bm-bubble__content a");
        link.GetAttribute("href").Should().Be("https://example.com/a");
        link.GetAttribute("target").Should().Be("_blank");
        link.GetAttribute("rel").Should().Be("noopener noreferrer nofollow");
        link.Attributes.Should().HaveCount(3, "an href, a target and a rel, and nothing a model could add");
        link.TextContent.Should().Be("Guide");
        cut.Find(".bm-bubble__content").TextContent.Should().Be("See the Guide for details.");
    }

    [Theory]
    [InlineData("http://example.com/a?x=1&y=2")]
    [InlineData("https://example.com/my_page_name")]
    [InlineData("https://example.com/a*b*c")]
    [InlineData("https://en.wikipedia.org/wiki/Foo_(bar)")]
    [InlineData("HTTPS://EXAMPLE.COM/A")]
    public void The_address_reaches_the_href_whole(string url)
    {
        // An ampersand is encoded in the attribute and read back by the browser; underscores and asterisks
        // are not italics inside an address; one level of parentheses belongs to it.
        var cut = RenderBubble(Assistant($"[the page]({url})"));

        cut.Find(".bm-bubble__content a").GetAttribute("href").Should().Be(url);
        cut.Find(".bm-bubble__content a").TextContent.Should().Be("the page");
    }

    [Theory]
    [InlineData("[x](javascript:alert(1))")]
    [InlineData("[x](JavaScript:alert(1))")]
    [InlineData("[x](data:text/html,<script>alert(1)</script>)")]
    [InlineData("[x](vbscript:msgbox(1))")]
    [InlineData("[x](/relative)")]
    [InlineData("[x](relative/page)")]
    [InlineData("[x](//evil.example/a)")]
    [InlineData("[x](https://example.com/a\" onmouseover=\"alert(1))")]
    public void A_link_to_any_other_scheme_or_a_relative_address_stays_encoded_text(string markdown)
    {
        var cut = RenderBubble(Assistant(markdown));

        var content = cut.Find(".bm-bubble__content");
        content.QuerySelectorAll("a, script").Should().BeEmpty();
        content.TextContent.Should().Be(markdown);
    }

    [Fact]
    public void A_quote_in_the_address_cannot_add_an_attribute()
    {
        // No space, so the address ends at the closing parenthesis: the quotes are part of it, encoded, and the
        // browser reads one odd https address, not an event handler.
        var cut = RenderBubble(Assistant("[x](https://example.com/a\"onmouseover=\"alert(1))"));

        var content = cut.Find(".bm-bubble__content");
        content.QuerySelectorAll("[onmouseover]").Should().BeEmpty();
        foreach (var link in content.QuerySelectorAll("a"))
        {
            link.Attributes.Select(a => a.Name).Should().BeEquivalentTo("href", "target", "rel");
            link.GetAttribute("href").Should().StartWith("https://");
        }
    }

    [Fact]
    public void An_address_holding_a_placeholder_stays_text()
    {
        // The code blocks are put back last, over the whole reply: one in an href would end the attribute early.
        // As text, the placeholder repeats the reply's own block, which is harmless.
        var cut = RenderBubble(Assistant("```\nx\n```\n[y](https://example.com/@@BMCODE0@@)"));

        cut.FindAll(".bm-bubble__content a, .bm-bubble__content [href]").Should().BeEmpty();
    }

    [Fact]
    public void The_link_text_takes_inline_formatting_and_cannot_hold_another_link()
    {
        var formatted = RenderBubble(Assistant("[**Guide** to `dotnet`](https://example.com/a)"));

        var link = formatted.Find(".bm-bubble__content a");
        link.QuerySelector("strong")!.TextContent.Should().Be("Guide");
        link.QuerySelector("code.bm-inline-code")!.TextContent.Should().Be("dotnet");

        var nested = RenderBubble(Assistant("[a [b](https://example.com/b)](https://example.com/a)"));

        nested.FindAll(".bm-bubble__content a a").Should().BeEmpty();
        nested.FindAll(".bm-bubble__content a").Should().ContainSingle()
              .Which.GetAttribute("href").Should().Be("https://example.com/b");
    }

    [Fact]
    public void A_link_inside_code_stays_code()
    {
        var cut = RenderBubble(Assistant("Write `[Guide](https://example.com/a)` in the file.\n```\n[Guide](https://example.com/b)\n```"));

        cut.FindAll(".bm-bubble__content a").Should().BeEmpty();
        cut.Find("code.bm-inline-code").TextContent.Should().Be("[Guide](https://example.com/a)");
        cut.Find("pre.bm-code code").TextContent.Should().Be("[Guide](https://example.com/b)");
    }

    [Fact]
    public void A_link_works_in_a_list_item_and_in_a_table_cell()
    {
        var cut = RenderBubble(Assistant("""
            1. Install it from [NuGet](https://www.nuget.org/packages/Novior.MentorAgent)

            | Page | Link |
            | --- | --- |
            | Docs | [read](https://example.com/docs) |
            """));

        cut.Find("ol.bm-list li a").GetAttribute("href").Should().Be("https://www.nuget.org/packages/Novior.MentorAgent");
        cut.Find(".bm-table td a").GetAttribute("href").Should().Be("https://example.com/docs");
    }

    // ── Numbered steps with text between them (BUG-216) ─────────────────────────

    [Fact]
    public void A_numbered_list_that_resumes_after_a_paragraph_keeps_its_number()
    {
        // The paragraph ends the list, and the next one started again from 1.
        var cut = RenderBubble(Assistant("1. A\nA line of explanation.\n2. B"));

        var lists = cut.FindAll("ol.bm-list");
        lists.Should().HaveCount(2);
        lists[0].HasAttribute("start").Should().BeFalse("a list that starts at 1 needs no start");
        lists[1].GetAttribute("start").Should().Be("2");
        lists[1].TextContent.Should().Be("B");
    }

    [Fact]
    public void A_numbered_list_starts_at_the_number_its_first_step_has()
    {
        var cut = RenderBubble(Assistant("3. C\n4. D"));

        cut.Find("ol.bm-list").GetAttribute("start").Should().Be("3");
        cut.FindAll("ol.bm-list li").Should().HaveCount(2);
    }

    [Fact]
    public void A_line_indented_under_a_step_stays_inside_that_step()
    {
        var cut = RenderBubble(Assistant("1. Install the package\n   dotnet add package Novior.MentorAgent\n2. Register the services"));

        cut.FindAll("ol.bm-list").Should().ContainSingle();
        var items = cut.FindAll("ol.bm-list li");
        items.Should().HaveCount(2);
        items[0].InnerHtml.Should().Be("Install the package<br>dotnet add package Novior.MentorAgent");
        items[1].TextContent.Should().Be("Register the services");
    }

    [Fact]
    public void A_code_block_indented_under_a_step_stays_inside_that_step()
    {
        var cut = RenderBubble(Assistant("1. Install it:\n   ```bash\n   dotnet add package Novior.MentorAgent\n   ```\n2. Register the services"));

        cut.FindAll("ol.bm-list").Should().ContainSingle();
        cut.Find("ol.bm-list li pre.bm-code code").TextContent.Should().Be("dotnet add package Novior.MentorAgent");
        cut.FindAll("ol.bm-list li").Should().HaveCount(2);
    }

    // ── Role, streaming and timestamps ──────────────────────────────────────────

    [Fact]
    public void A_user_message_gets_the_user_modifier_and_no_avatar()
    {
        var cut = RenderBubble(User("hello"));

        (cut.Find(".bm-bubble").GetAttribute("class") ?? "").Should().Contain("bm-bubble--user");
        cut.FindAll(".bm-bubble__avatar").Should().BeEmpty("the user is not the one being represented");
    }

    [Fact]
    public void An_assistant_message_gets_the_assistant_modifier_and_an_avatar()
    {
        var cut = RenderBubble(Assistant("hi"));

        (cut.Find(".bm-bubble").GetAttribute("class") ?? "").Should().Contain("bm-bubble--assistant");
        cut.FindAll(".bm-bubble__avatar img").Should().HaveCount(1);
    }

    [Fact]
    public void A_streaming_bubble_shows_the_cursor_and_a_settled_one_does_not()
    {
        var streaming = RenderBubble(Assistant("partial"), streaming: true);
        streaming.FindAll(".bm-cursor").Should().HaveCount(1);

        var settled = RenderBubble(Assistant("complete"));
        settled.FindAll(".bm-cursor").Should().BeEmpty();
    }

    [Fact]
    public void The_timestamp_is_shown_as_hours_and_minutes_when_supplied()
    {
        var cut = Render<MessageBubble>(p => p
            .Add(c => c.Message, Assistant("hi"))
            .Add(c => c.SentAt, new DateTime(2026, 8, 13, 14, 35, 12)));

        cut.Find(".bm-bubble__time").TextContent.Should().Be("14:35");
    }

    [Fact]
    public void No_timestamp_leaves_the_line_empty_rather_than_printing_a_default_date()
    {
        var cut = RenderBubble(Assistant("hi"));

        cut.Find(".bm-bubble__time").TextContent.Should().BeEmpty(
            "01/01/0001 in a chat bubble is the classic un-set DateTime leak");
    }

    // ── Attached images (#13) ───────────────────────────────────────────────────

    [Fact]
    public void A_data_content_image_is_rendered_from_the_message_contents()
    {
        var message = new ChatMessage(ChatRole.User, [
            new TextContent("what is this?"),
            new DataContent("data:image/png;base64,iVBORw0KGgo="),
        ]);

        var cut = RenderBubble(message);

        cut.FindAll(".bm-bubble__image").Should().HaveCount(1);
        (cut.Find(".bm-bubble__image").GetAttribute("src") ?? "").Should().StartWith("data:image/png");
    }

    [Fact]
    public void A_non_image_attachment_is_not_rendered_as_an_image()
    {
        var message = new ChatMessage(ChatRole.User, [
            new TextContent("here is a document"),
            new DataContent("data:application/pdf;base64,JVBERi0="),
        ]);

        var cut = RenderBubble(message);

        cut.FindAll(".bm-bubble__image").Should().BeEmpty(
            "an <img> pointing at a PDF renders as a broken image icon");
    }

    [Fact]
    public void An_image_only_message_still_renders_the_image()
    {
        // The documented "image-only turn is valid" case: no text at all.
        var message = new ChatMessage(ChatRole.User, [
            new DataContent("data:image/jpeg;base64,/9j/4AAQ"),
        ]);

        var cut = RenderBubble(message);

        cut.FindAll(".bm-bubble__image").Should().HaveCount(1);
    }

    // ── RAG citations ───────────────────────────────────────────────────────────

    [Fact]
    public void Sources_are_shown_under_an_assistant_message_when_the_option_is_on()
    {
        Widget.ShowRagSources = true;

        var cut = Render<MessageBubble>(p => p
            .Add(c => c.Message, Assistant("the answer"))
            .Add(c => c.Sources, (IReadOnlyList<MentorRagResult>)
            [
                new MentorRagResult("…", "https://x.test/h", "Handbook", 0.9f),
            ]));

        cut.FindAll(".bm-rag-chip").Should().HaveCount(1);
    }

    [Fact]
    public void Sources_are_never_shown_under_a_user_message()
    {
        Widget.ShowRagSources = true;

        var cut = Render<MessageBubble>(p => p
            .Add(c => c.Message, User("my question"))
            .Add(c => c.Sources, (IReadOnlyList<MentorRagResult>)
            [
                new MentorRagResult("…", null, "Handbook", 0.9f),
            ]));

        cut.FindAll(".bm-rag-chip").Should().BeEmpty(
            "the user did not cite anything; attributing sources to them is nonsense");
    }
}
