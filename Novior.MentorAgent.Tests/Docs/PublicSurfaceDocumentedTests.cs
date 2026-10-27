using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Xunit;

namespace Novior.MentorAgent.Tests.Docs;

/// <summary>
/// The <em>other</em> direction of the documentation contract.
///
/// <para>
/// <see cref="ClaimsInventoryTests"/> and <see cref="DocsIdentifierTests"/> walk from the READMEs to
/// the source: every documented identifier must exist. That catches doc rot, and it caught BUG-001
/// through BUG-005. It cannot catch the opposite failure, which is a public member that works
/// perfectly and that nobody is ever told about — and that failure is invisible from the docs side
/// by construction, because there is no sentence to check.
/// </para>
///
/// <para>
/// It is not hypothetical. <c>IMentorOrchestrator.RespondToApprovalAsync</c> and
/// <c>CancelCurrentRequest</c> were both public, both on the interface a host injects to drive the
/// assistant, and both absent from all five READMEs — while the sibling members around them
/// (<c>SendMessageAsync</c>, <c>SerializeSessionAsync</c>) had worked examples. Answering an
/// approval from a host's own UI on WebAssembly is exactly what <c>RespondToApprovalAsync</c> is
/// for, and a reader had no way to discover it.
/// </para>
///
/// <para>
/// Scope is deliberately narrow: the surfaces a host actually types. Wiring types that exist only
/// because DI needs them public are not documentation debt, and asserting on them would produce
/// noise that gets suppressed rather than read.
/// </para>
/// </summary>
public class PublicSurfaceDocumentedTests
{
    /// <summary>All five READMEs concatenated — prose, tables and code fences alike.</summary>
    private static readonly string AllDocs =
        string.Join('\n', RepoFiles.Readmes.Keys.Select(RepoFiles.Readme));

    /// <summary>Only the fenced code blocks, which is where a *worked example* lives.</summary>
    private static readonly string AllSnippets = string.Join('\n',
        Regex.Matches(AllDocs, @"^```[a-zA-Z]*\r?\n(.*?)^```", RegexOptions.Singleline | RegexOptions.Multiline)
             .Select(m => m.Groups[1].Value));

    private static bool Mentioned(string name) =>
        Regex.IsMatch(AllDocs, $@"\b{Regex.Escape(name)}\b");

    private static bool Exampled(string name) =>
        Regex.IsMatch(AllSnippets, $@"\b{Regex.Escape(name)}\b");

    private static string Report(IEnumerable<string> missing, string what) =>
        $"{what} not documented in any of the five READMEs: {string.Join(", ", missing)}. " +
        "A public member nobody is told about is a feature that does not exist for the reader.";

    // ------------------------------------------------------------------ orchestrator

    /// <summary>
    /// The orchestrator is the entire input surface — seven members. A host that injects it can
    /// send, stop, reset, snapshot, restore and answer approvals, and every one of those has to be
    /// findable. This is the assertion that fails for BUG-009's fixed method.
    /// </summary>
    [Fact]
    public void Every_member_of_IMentorOrchestrator_is_documented()
    {
        var missing = typeof(IMentorOrchestrator)
            .GetMethods()
            .Select(m => m.Name)
            .Distinct()
            .Where(n => !Mentioned(n))
            .ToList();

        missing.Should().BeEmpty(Report(missing, "IMentorOrchestrator members"));
    }

    /// <summary>
    /// Naming a method in a table is not the same as showing it being called. The orchestrator is
    /// the one interface where the calling convention carries a trap — answering an approval takes
    /// a different route on WebAssembly than in-process — so a reference row alone is not enough.
    /// </summary>
    [Fact]
    public void Every_member_of_IMentorOrchestrator_appears_in_a_code_example()
    {
        var missing = typeof(IMentorOrchestrator)
            .GetMethods()
            .Select(m => m.Name)
            .Distinct()
            .Where(n => !Exampled(n))
            .ToList();

        missing.Should().BeEmpty(Report(missing, "IMentorOrchestrator members with no worked example"));
    }

    // ------------------------------------------------------------------ state service

    /// <summary>
    /// Every event a custom UI can subscribe to. The <c>Notify*</c> half is deliberately excluded:
    /// the README documents it as one family ("those belong to the orchestrator — subscribe to the
    /// events, do not raise them"), which is the right level for a consumer and a decision worth
    /// keeping rather than mechanically undoing.
    /// </summary>
    [Fact]
    public void Every_event_on_IMentorStateService_is_documented()
    {
        var missing = typeof(IMentorStateService)
            .GetEvents()
            .Select(e => e.Name)
            .Where(n => !Mentioned(n))
            .ToList();

        missing.Should().BeEmpty(Report(missing, "IMentorStateService events"));
    }

    // ------------------------------------------------------------------ options

    /// <summary>
    /// Every option a host can set. These are the published knobs: an undocumented one is a setting
    /// a reader cannot know exists, and the reference tables are what they read instead of the
    /// source.
    /// </summary>
    [Theory]
    [InlineData(typeof(MentorWidgetOptions))]
    [InlineData(typeof(Novior.MentorAgent.Options.MentorOptions))]
    [InlineData(typeof(Novior.MentorAgent.Blazor.Models.MentorAgentBlazorOptions))]
    [InlineData(typeof(Novior.MentorAgent.Declarative.MentorDeclarativeOptions))]
    public void Every_public_option_is_documented(Type optionsType)
    {
        var missing = optionsType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite)
            .Select(p => p.Name)
            .Where(n => !Mentioned(n))
            .ToList();

        missing.Should().BeEmpty(Report(missing, $"{optionsType.Name} properties"));
    }

    /// <summary>
    /// Named somewhere in five READMEs is not in the table a reader scans. The README and the Server README each have
    /// reference tables of <c>MentorOptions</c>, and the Server README a list of the options it leaves out on purpose:
    /// the 1.1.0 notes said every option was in one or the other (BUG-163), and <c>ConfirmationMessage</c>, new in
    /// the same release, was in neither in the Server README (story 128).
    /// </summary>
    [Theory]
    [InlineData("core")]
    [InlineData("server")]
    public void Every_MentorOptions_property_is_in_a_reference_table_or_left_out_on_purpose(string readme)
    {
        var text = RepoFiles.Readme(readme).Replace("\r\n", "\n");
        var inTables = Regex.Matches(text, @"^\|\s*`([A-Za-z0-9]+)`\s*\|", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
        var leftOut = LeftOutOnPurpose(text);

        var missing = typeof(Novior.MentorAgent.Options.MentorOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.SetMethod is { IsPublic: true })
            .Select(p => p.Name)
            .Where(n => !inTables.Contains(n) && !leftOut.Contains(n))
            .Order(StringComparer.Ordinal)
            .ToList();

        missing.Should().BeEmpty(
            $"every public option of MentorOptions is a row of a reference table of the {readme} README, or among the " +
            $"options it does not list on purpose; missing: {string.Join(", ", missing)}");
    }

    /// <summary>
    /// The <c>[MentorAction]</c> table of the README and of the Server README: every property of the attribute is a
    /// row, and <c>Description</c> says it is required (BUG-163, story 128).
    /// </summary>
    [Theory]
    [InlineData("core")]
    [InlineData("server")]
    public void The_MentorAction_table_lists_every_property_and_Description_as_required(string readme)
    {
        var lines = RepoFiles.Readme(readme).Replace("\r\n", "\n").Split('\n');
        var required = Array.FindIndex(lines, l => l.StartsWith("| `Description` | **Required.**", StringComparison.Ordinal));
        required.Should().BeGreaterThanOrEqualTo(0, $"the {readme} README's [MentorAction] table marks Description as required");

        var first = required;
        while (first > 0 && lines[first - 1].StartsWith('|')) first--;
        var rows = lines.Skip(first).TakeWhile(l => l.StartsWith('|'))
            .Select(l => Regex.Match(l, @"^\|\s*`([A-Za-z0-9]+)`\s*\|"))
            .Where(m => m.Success)
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        var missing = typeof(Novior.MentorAgent.Attributes.MentorActionAttribute)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(p => p.Name)
            .Where(n => !rows.Contains(n))
            .Order(StringComparer.Ordinal)
            .ToList();

        missing.Should().BeEmpty($"the {readme} README's [MentorAction] table lists every property of the attribute");
    }

    /// <summary>The names in the blockquote that starts with <c>**Not listed here on purpose.**</c>, if there is one.</summary>
    private static HashSet<string> LeftOutOnPurpose(string text)
    {
        var lines = text.Split('\n');
        var start = Array.FindIndex(lines, l => l.StartsWith('>') && l.Contains("**Not listed here on purpose.**"));
        if (start < 0) return [];

        var block = string.Join('\n', lines.Skip(start).TakeWhile(l => l.StartsWith('>')));
        return Regex.Matches(block, @"`([A-Za-z0-9]+)`").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
    }

    // ------------------------------------------------------------------ metrics shapes

    /// <summary>
    /// The metrics snapshot is served verbatim by <c>GET /mentor/admin/metrics</c> and read by any
    /// host building its own dashboard, so its shape is a wire contract, not an implementation
    /// detail. <c>DeflectionRate</c> — the one number in the whole library a business stakeholder
    /// asks for — was named nowhere, and <c>RequestsWithoutUsage</c> shipped new in 1.0.0-rc.1
    /// undocumented.
    /// </summary>
    [Theory]
    [InlineData(typeof(MentorMetricsSnapshot))]
    [InlineData(typeof(MentorModelUsage))]
    [InlineData(typeof(MentorUsagePoint))]
    [InlineData(typeof(MentorActionCount))]
    public void Every_member_of_the_metrics_snapshot_is_documented(Type shape)
    {
        var missing = shape
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .Where(n => !Mentioned(n))
            .ToList();

        missing.Should().BeEmpty(Report(missing, $"{shape.Name} members"));
    }
}
