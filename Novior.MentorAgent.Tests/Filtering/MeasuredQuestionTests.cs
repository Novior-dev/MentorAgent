using FluentAssertions;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Tests.Docs;
using Xunit;

namespace Novior.MentorAgent.Tests.Filtering;

/// <summary>
/// The questions of the request classifier that were measured on the model, kept as they were measured, in
/// <c>MeasuredQuestions/</c>: each is rebuilt from the code, with the actions, pages, names and page actions it was
/// measured with, and must be the same, character for character. A change to <see cref="RequestClassifier.Question"/>
/// that reaches a measured shape fails here, and is measured again before it is changed (BUG-106, BUG-118, BUG-247,
/// BUG-251). A file is compared without regard to its line endings: the checkout writes CRLF, the code writes LF.
/// </summary>
public sealed class MeasuredQuestionTests
{
    private static string Measured(string file)
        => File.ReadAllText(Path.Combine(RepoFiles.Root, "Novior.MentorAgent.Tests", "Filtering", "MeasuredQuestions", file))
            .Replace("\r\n", "\n");

    private static string Between(string text, string from, string to)
    {
        var start = text.IndexOf(from, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"the measured question has «{from}»");
        start += from.Length;
        return text[start..text.IndexOf(to, start, StringComparison.Ordinal)];
    }

    private static List<string> Names(string text) => [.. Between(text, "anywhere in the message: ", ". Answer with").Split(", ")];

    private static List<string> Actions(string text) => [.. text.Split('\n').Where(l => l.StartsWith("- ", StringComparison.Ordinal) && l.Contains(". Inputs: ", StringComparison.Ordinal))];

    private static List<string> Pages(string text) => [.. Between(text, "not something within the page already open: ", ". Answer OTHER").Split("; ")];

    private static List<string> PageActions(string text) => [.. Between(text, "on that page:\n", "\nA message that asks").Split('\n')];

    private static readonly RequestShape Reads = new(Lookup: true, [], Navigation: false);

    [Fact]
    public void The_question_measured_for_reads_is_unchanged_BUG_106()
    {
        var measured = Measured("bug106-reads.txt");

        RequestClassifier.Question(Names(measured), Reads).Should().Be(measured);
    }

    [Fact]
    public void The_question_measured_for_reads_on_a_page_with_actions_is_unchanged_BUG_247()
    {
        var measured = Measured("bug247-reads-page.txt");

        RequestClassifier.Question(Names(measured), Reads with { PageActions = PageActions(measured) }).Should().Be(measured);
    }

    [Fact]
    public void The_question_measured_with_every_label_is_unchanged_BUG_118()
    {
        var measured = Measured("bug118-lan.txt");
        var shape = new RequestShape(Lookup: true, Actions(measured), Navigation: true);

        RequestClassifier.Question(Names(measured), shape, Pages(measured)).Should().Be(measured);
    }

    [Fact]
    public void The_question_measured_with_every_label_on_a_page_with_actions_is_unchanged_BUG_251()
    {
        var measured = Measured("bug251-lan-page.txt");
        var shape = new RequestShape(Lookup: true, Actions(measured), Navigation: true) { PageActions = PageActions(measured) };

        RequestClassifier.Question(Names(measured), shape, Pages(measured)).Should().Be(measured);
    }

    [Fact]
    public void With_actions_offered_the_page_rule_says_what_is_offered_and_gives_no_example_of_a_request()
    {
        var page = new[] { "- sort_tickets: The tickets: sorts the rows by a column" };
        var action = new[] { "- issue_refund: Issues a refund. Inputs: orderId (integer)." };

        var actionsOnly = RequestClassifier.Question([], new RequestShape(false, action, false) { PageActions = page });
        actionsOnly.Should().Contain("is OTHER, not ACTION. ").And.NotContain("not LOOKUP")
            .And.Contain("A complete request for one of the actions listed above is still ACTION, even when that page is open.")
            .And.NotContain("A question about a value");
        actionsOnly.IndexOf("- sort_tickets", StringComparison.Ordinal).Should()
            .BeGreaterThan(actionsOnly.IndexOf("- issue_refund", StringComparison.Ordinal), "the page's actions come after the ones offered");

        var both = RequestClassifier.Question([], new RequestShape(true, action, false) { PageActions = page });
        both.Should().Contain("is OTHER, not LOOKUP or ACTION. ").And.Contain("still LOOKUP").And.Contain("still ACTION");
        both.Split(PageRuleStart).Should().HaveCount(2, "the page's actions are listed once");

        RequestClassifier.Question([], new RequestShape(false, [], true) { PageActions = page })
            .Should().NotContain(PageRuleStart, "a page request is navigation's own question");
        RequestClassifier.Question([], new RequestShape(false, action, false))
            .Should().NotContain(PageRuleStart, "without page actions nothing is added");
    }

    private const string PageRuleStart = "The page the user has open has actions of its own";
}
