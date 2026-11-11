using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Novior.MentorAgent.Tests.Docs;

/// <summary>
/// ACTION-RULES.md lists the rules by which a method becomes an action, how it is named and how roles and confirmations
/// add up (story 1273). Every rule in force has a test that names it with a <c>[Trait("Rule", name)]</c>, on the test or
/// on its class, so a rule cannot change without a test going red; a test cannot name a rule the file does not list; and
/// a rule that is new or changed in the version being built is in that version's release notes.
/// </summary>
public sealed class ActionRulesDocumentTests
{
    private const string FileName = "ACTION-RULES.md";

    /// <summary>The address the package README links: nuget.org shows the README, where a relative link leads nowhere.</summary>
    private const string PublishedAddress = "https://github.com/Novior-dev/MentorAgent/blob/main/" + FileName;

    private sealed record Rule(string Name, string Since, string? ChangedIn, bool Retired);

    // A row of the table: | `area.rule` | what it says | since | changed in |. "Changed in" is empty, a dash, a version,
    // or "retired in <version>".
    private static readonly Regex Row = new(
        @"^\|\s*`(?<name>[^`]+)`\s*\|[^|\n]*\|\s*(?<since>[^|\n]*?)\s*\|\s*(?<changed>[^|\n]*?)\s*\|\s*$",
        RegexOptions.Multiline);

    private static readonly Regex NameShape = new(@"^[a-z]+\.[a-z0-9]+(-[a-z0-9]+)*$");

    private static readonly Regex VersionShape = new(@"^\d+\.\d+\.\d+$");

    private static IReadOnlyList<Rule> Rules { get; } = ReadRules();

    private static IReadOnlyList<Rule> ReadRules()
    {
        var text = File.ReadAllText(Path.Combine(RepoFiles.Root, FileName)).Replace("\r\n", "\n");

        return [.. Row.Matches(text).Select(m =>
        {
            var changed = m.Groups["changed"].Value;
            var retired = changed.StartsWith("retired in ", StringComparison.Ordinal);
            var version = retired ? changed["retired in ".Length..] : changed is "" or "—" or "-" ? null : changed;
            return new Rule(m.Groups["name"].Value, m.Groups["since"].Value, version, retired);
        })];
    }

    /// <summary>Every [Fact] and [Theory] of this assembly with the rules it names, its own and its class's.</summary>
    private static IReadOnlyList<(string Test, IReadOnlyList<string> Rules)> RuleTests { get; } =
        [.. typeof(ActionRulesDocumentTests).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttribute<FactAttribute>() is not null)
                .Select(m => ($"{type.Name}.{m.Name}", (IReadOnlyList<string>)[.. RulesNamedBy(m), .. RulesNamedBy(type)])))
            .Where(t => t.Item2.Count > 0)];

    private static IEnumerable<string> RulesNamedBy(MemberInfo member)
        => member.GetCustomAttributesData()
            .Where(a => a.AttributeType == typeof(TraitAttribute)
                        && a.ConstructorArguments.Count == 2
                        && (string?)a.ConstructorArguments[0].Value == "Rule")
            .Select(a => (string)a.ConstructorArguments[1].Value!);

    [Fact]
    public void The_file_lists_rules_by_unique_names_with_the_version_they_came_in()
    {
        Rules.Should().NotBeEmpty($"{FileName} has a table with one row per rule");
        Rules.Select(r => r.Name).Should().OnlyHaveUniqueItems("a name stays with one rule");

        foreach (var rule in Rules)
        {
            rule.Name.Should().MatchRegex(NameShape, "a name is area.rule, lower case with dashes");
            rule.Since.Should().MatchRegex(VersionShape, $"{rule.Name}: Since is the version the rule was listed in");
            if (rule.ChangedIn is not null)
                rule.ChangedIn.Should().MatchRegex(VersionShape, $"{rule.Name}: Changed in is a version");
        }
    }

    [Fact]
    public void Every_rule_in_force_has_a_test_that_names_it()
    {
        var named = RuleTests.SelectMany(t => t.Rules).ToHashSet(StringComparer.Ordinal);

        Rules.Where(r => !r.Retired).Select(r => r.Name).Where(n => !named.Contains(n))
            .Should().BeEmpty("every rule in force has at least one test with [Trait(\"Rule\", name)]");
    }

    [Fact]
    public void Every_rule_a_test_names_is_listed_and_in_force()
    {
        var inForce = Rules.Where(r => !r.Retired).Select(r => r.Name).ToHashSet(StringComparer.Ordinal);

        RuleTests.SelectMany(t => t.Rules.Where(r => !inForce.Contains(r)).Select(r => $"{t.Test}: {r}"))
            .Should().BeEmpty($"a test names only rules {FileName} lists and has not retired");
    }

    [Fact]
    public void A_rule_listed_changed_or_retired_in_the_version_being_built_is_in_its_release_notes()
    {
        var version = XDocument.Load(Path.Combine(RepoFiles.Root, "Directory.Build.props"))
            .Descendants("Version").Single().Value.Trim();
        var notes = ReleaseNotes(version);

        Rules.Where(r => r.Since == version || r.ChangedIn == version).Select(r => r.Name)
            .Where(n => !notes.Contains($"`{n}`", StringComparison.Ordinal))
            .Should().BeEmpty($"a rule that comes in, changes or goes in {version} is named in the release notes of {version}");
    }

    [Fact]
    public void The_package_readme_links_the_rules_at_an_address_nuget_org_can_follow()
        => RepoFiles.Readme("core").Should().Contain($"]({PublishedAddress})");

    /// <summary>The notes the package of <paramref name="version"/> is built with: its section, or [Unreleased].</summary>
    private static string ReleaseNotes(string version)
    {
        var changelog = File.ReadAllText(Path.Combine(RepoFiles.Root, "CHANGELOG.md")).Replace("\r\n", "\n");
        foreach (var name in new[] { version, "Unreleased" })
        {
            var m = Regex.Match(changelog,
                @"^## \[" + Regex.Escape(name) + @"\][^\n]*\n(?<body>.*?)(?=^## |^\[[^\]\n]+\]:\s|\z)",
                RegexOptions.Multiline | RegexOptions.Singleline);
            if (m.Success && m.Groups["body"].Value.Trim().Length > 0) return m.Groups["body"].Value;
        }

        return "";
    }
}
