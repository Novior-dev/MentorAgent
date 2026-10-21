using System.Text.RegularExpressions;
using FluentAssertions;
using Novior.MentorAgent.Tests.Docs;
using Xunit;

namespace Novior.MentorAgent.Tests.Theming;

/// <summary>
/// BUG-193 — in a table inside an assistant bubble, numbers broke one digit per line.
///
/// <para>
/// <c>.bm-bubble__content</c> has <c>word-break: break-word</c>, which lets a word break anywhere, and the table's
/// cells inherited it. A table sizes its columns from the narrowest its cells can be, and a word that may break
/// anywhere is one character wide: in a bubble narrower than the table, the columns shrank to one character and a
/// claim number such as "1008" stood as four lines. Found live in the demo. bUnit renders no stylesheet, so this
/// reads the rules a browser lays the table out with.
/// </para>
/// </summary>
public sealed class TableCellBreakTests
{
    private static readonly string Css = File.ReadAllText(
        Path.Combine(RepoFiles.Root, RepoFiles.Abstractions, "wwwroot", "css", "MentorAgent.css"));

    /// <summary>The declarations of the rule with exactly this selector list, property → value.</summary>
    private static Dictionary<string, string> Rule(string selectors)
    {
        var pattern = string.Join(@"\s*,\s*", selectors.Split(',').Select(s => Regex.Escape(s.Trim())));
        var rule = Regex.Match(Css, $@"(?m)^{pattern}\s*\{{(?<body>[^}}]*)\}}");
        rule.Success.Should().BeTrue($"the stylesheet should have a '{selectors}' rule");

        return Regex.Matches(Regex.Replace(rule.Groups["body"].Value, @"/\*.*?\*/", "", RegexOptions.Singleline),
                             @"(?<name>[a-z-]+)\s*:\s*(?<value>[^;]+);")
                    .ToDictionary(m => m.Groups["name"].Value, m => m.Groups["value"].Value.Trim());
    }

    [Fact]
    public void Table_cells_break_only_between_words()
    {
        var cells = Rule(".bm-table th, .bm-table td");

        cells.Should().Contain("word-break", "normal",
            "the bubble's word-break: break-word must not reach the cells, or every column can shrink to one character");
        cells.Should().Contain("overflow-wrap", "normal");
    }

    [Fact]
    public void A_number_cell_stays_on_one_line()
    {
        // Breaking between words still splits "2026-10-16" after a hyphen: the renderer marks a cell that holds only
        // a number, an amount, a date or a time, and this keeps it whole.
        Rule(".bm-table .bm-num").Should().Contain("white-space", "nowrap");
    }

    [Fact]
    public void A_table_wider_than_the_bubble_scrolls_inside_it()
    {
        // With the cells no longer breaking inside words, a wide table is wider than the bubble: it scrolls in its
        // wrapper, and the page does not.
        Rule(".bm-table-wrap").Should().Contain("overflow-x", "auto");

        // The bubble's body is a flex item, at least as wide as its content unless told otherwise: the table widened
        // the bubble past the panel, and the whole conversation scrolled sideways instead of the table.
        Rule(".bm-bubble__body").Should().Contain("min-width", "0");
    }
}
