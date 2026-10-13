using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Novior.MentorAgent.Tests.Docs;

/// <summary>
/// Every member documented in a README table has to appear in a code example somewhere.
///
/// <para>
/// <c>PublicSurfaceDocumentedTests</c> already guarantees the other direction — no public option
/// goes undocumented. This is the complement, and it was worth measuring: 38 of 220 documented
/// members existed <b>only</b> as a table row, so a reader had a description and nothing to copy.
/// Twenty-four of them were the widget's own component parameters, in a README whose stated premise
/// is that you can compose those components yourself — with no code showing how.
/// </para>
///
/// <para>
/// A table row says what a thing is; an example says what to type. For a library whose surface is
/// mostly configuration, the second is the one people actually use — and the members that were
/// missing included <c>McpCallerPrincipal</c>, which decides what a remote caller may invoke.
/// </para>
/// </summary>
public sealed class OptionExampleCoverageTests
{
    /// <summary>A `| `Name` | `Type` | …` row — the shape every option and parameter table uses.</summary>
    private static readonly Regex DocumentedRow = new(
        @"^\|\s*`([A-Z][A-Za-z0-9]+)`\s*\|\s*`", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// A fenced code block. The <c>\r?</c> is load-bearing: these files are CRLF, so a pattern
    /// expecting a bare <c>\n</c> after the language tag matches almost nothing — and this test
    /// would then compare a full option list against an empty haystack and report every member as
    /// undocumented. The parser guard below exists because that is exactly what happened.
    /// </summary>
    private static readonly Regex Fence = new(
        @"```[a-zA-Z]*\r?\n(.*?)```", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>
    /// Names the row regex picks up that are not members of ours at all — a BCL type used as a
    /// column value, and a class named in prose. Deliberately tiny and explicit: an allow-list is
    /// how this kind of test quietly stops meaning anything, so anything added here needs a reason.
    /// </summary>
    private static readonly HashSet<string> NotOurMembers = ["Guid", "MentorLanguageExtensions"];

    private static (HashSet<string> Documented, string Code) Read()
    {
        var documented = new HashSet<string>(StringComparer.Ordinal);
        var code = new System.Text.StringBuilder();

        foreach (var (_, path) in RepoFiles.Readmes)
        {
            var text = File.ReadAllText(path);
            foreach (Match m in DocumentedRow.Matches(text))
                documented.Add(m.Groups[1].Value);
            foreach (Match m in Fence.Matches(text))
                code.AppendLine(m.Groups[1].Value);
        }

        return (documented, code.ToString());
    }

    [Fact]
    public void The_parser_finds_the_tables_and_the_code_it_is_meant_to_compare()
    {
        // The guard this campaign has needed twice: a regex that stops matching turns every
        // assertion below into a comparison of two empty sets, and the file passes vacuously.
        var (documented, code) = Read();

        documented.Should().HaveCountGreaterThan(150, "the five READMEs document a large surface");
        documented.Should().Contain("ChatClient").And.Contain("OnSend").And.Contain("Steps");
        code.Length.Should().BeGreaterThan(50_000, "there are hundreds of code fences to search");
    }

    [Fact]
    public void Every_documented_member_appears_in_at_least_one_code_example()
    {
        var (documented, code) = Read();

        var missing = documented
            .Where(name => !NotOurMembers.Contains(name))
            .Where(name => !Regex.IsMatch(code, @"\b" + Regex.Escape(name) + @"\b"))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        missing.Should().BeEmpty(
            "a member documented only as a table row leaves the reader with a description and " +
            "nothing to copy. Add it to a code example, or — if it genuinely is not ours — to " +
            "NotOurMembers with a reason. Missing:\n  " + string.Join("\n  ", missing));
    }

    [Fact]
    public void The_allow_list_only_holds_names_that_are_still_documented()
    {
        // Stops the allow-list outliving its reason: an entry for a row that no longer exists is a
        // suppression nobody will ever revisit.
        var (documented, _) = Read();

        NotOurMembers.Where(n => !documented.Contains(n)).Should().BeEmpty(
            "an allow-list entry for a row that is gone hides nothing and should be deleted");
    }
}
