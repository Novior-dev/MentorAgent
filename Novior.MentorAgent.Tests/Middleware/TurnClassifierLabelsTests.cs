using FluentAssertions;
using Novior.MentorAgent.Core;
using Xunit;

namespace Novior.MentorAgent.Tests.Middleware;

/// <summary>
/// BUG-105 — a classifier's label is read by position, the n-th word, not searched for in the whole reply:
/// <c>Contains("OUT")</c> matched "about", "without" and "outside". This parses the classifier's own
/// constrained output, never user text.
/// </summary>
public sealed class TurnClassifierLabelsTests
{
    [Theory]
    [InlineData("SAFE IN - the question is about a product", 1, "IN", "OUT", true)]
    [InlineData("safe, out.", 1, "IN", "OUT", false)]
    [InlineData("**SAFE** **IN**", 1, "IN", "OUT", true)]
    [InlineData("SAFE REFUSE", 1, "REFUSE", "ANSWER", true)]
    [InlineData("SAFE ANSWER, without doubt it is about a product", 1, "REFUSE", "ANSWER", false)]
    [InlineData("REFUSE", 0, "REFUSE", "ANSWER", true)]
    [InlineData("answer", 0, "REFUSE", "ANSWER", false)]
    public void A_label_is_read_at_its_position(string reply, int position, string yes, string no, bool expected)
        => TurnClassifierLabels.Read(reply, position, yes, no).Should().Be(expected);

    [Theory]
    [InlineData("SAFE")]              // the second word is missing
    [InlineData("OUT SAFE")]          // a word, but not one of the two labels
    [InlineData("Sure, it is fine")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_decides_nothing(string? reply)
        => TurnClassifierLabels.Read(reply, 1, "IN", "OUT").Should().BeNull(
            "an unreadable label must not be taken for either answer: the caller fails open");

    [Fact]
    public void The_reply_shown_in_a_log_line_is_short_and_on_one_line()
    {
        TurnClassifierLabels.Shown("  SAFE\r\nIN  ").Should().Be("SAFE IN");
        TurnClassifierLabels.Shown(new string('x', 100)).Should().HaveLength(41);
    }
}
