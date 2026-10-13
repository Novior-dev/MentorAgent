using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Novior.MentorAgent.Tests.Docs;

/// <summary>
/// CHANGELOG.md is the single source of every package's release notes (1.0.0-rc.13): at pack time
/// <c>Directory.Build.targets</c> takes the section for the package's version, or <c>[Unreleased]</c>
/// while a version is being prepared, and every package ships the whole file.
///
/// <para>
/// Before, the notes were written by hand in five .csproj files, about 30 KB each, and trimmed by hand
/// whenever a package neared nuget.org's 35,000-character cap. These tests keep the one file well formed
/// enough for the extraction to find its section, and keep the five copies from coming back.
/// </para>
/// </summary>
public sealed class ChangelogTests
{
    private static readonly string ChangelogPath = Path.Combine(RepoFiles.Root, "CHANGELOG.md");

    private static string Changelog => File.ReadAllText(ChangelogPath).Replace("\r\n", "\n");

    private static readonly Regex Heading = new(
        @"^## \[(?<v>[^\]]+)\](?<rest>[^\n]*)$", RegexOptions.Multiline);

    private static readonly Regex Version = new(
        @"^(?<core>\d+\.\d+\.\d+)(?:-(?<pre>[0-9A-Za-z.]+))?$");

    private static IReadOnlyList<(string Name, string Tail)> Headings =>
        Heading.Matches(Changelog).Select(m => (m.Groups["v"].Value, m.Groups["rest"].Value)).ToList();

    [Fact]
    public void The_file_exists_at_the_repository_root()
        => File.Exists(ChangelogPath).Should().BeTrue("Directory.Build.targets reads it from there");

    [Fact]
    public void Unreleased_comes_first_and_only_once()
    {
        var names = Headings.Select(h => h.Name).ToList();
        names.Should().NotBeEmpty();
        names[0].Should().Be("Unreleased");
        names.Count(n => n == "Unreleased").Should().Be(1);
        Headings[0].Tail.Should().BeEmpty("an unreleased section has no date");
    }

    [Fact]
    public void Every_released_heading_is_a_version_with_a_date()
    {
        foreach (var (name, tail) in Headings.Skip(1))
        {
            Version.IsMatch(name).Should().BeTrue($"'{name}' is not a SemVer version");
            tail.Should().MatchRegex(@"^ - \d{4}-\d{2}-\d{2}$", $"'{name}' needs ' - yyyy-MM-dd'");
        }
    }

    [Fact]
    public void Versions_are_newest_first_by_semver_precedence()
    {
        var versions = Headings.Skip(1).Select(h => h.Name).ToList();
        for (var i = 1; i < versions.Count; i++)
            Compare(versions[i - 1], versions[i]).Should().BePositive(
                $"{versions[i - 1]} is listed above {versions[i]}");
    }

    [Fact]
    public void Every_heading_has_a_link_reference()
    {
        var text = Changelog;
        foreach (var (name, _) in Headings)
            text.Should().MatchRegex(@"(?m)^\[" + Regex.Escape(name) + @"\]: https://\S+$",
                $"'[{name}]' needs a link reference at the bottom");
    }

    [Fact]
    public void The_version_being_built_has_notes()
    {
        // Directory.Build.targets fails the pack (MENTORCL001) otherwise; this says so without packing.
        var version = XDocument.Load(Path.Combine(RepoFiles.Root, "Directory.Build.props"))
            .Descendants("Version").Single().Value.Trim();

        var body = Section(version) ?? Section("Unreleased");
        body.Should().NotBeNullOrWhiteSpace($"neither [{version}] nor [Unreleased] has content");
    }

    [Fact]
    public void Every_section_fits_nugets_release_notes_cap()
    {
        foreach (var (name, _) in Headings)
            (Section(name) ?? "").Length.Should().BeLessThan(34_500,
                $"[{name}] becomes a package's <releaseNotes>, which nuget.org caps at 35,000 characters");
    }

    [Fact]
    public void No_project_writes_its_own_release_notes()
    {
        var projects = Directory.EnumerateFiles(RepoFiles.Root, "*.csproj", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

        foreach (var project in projects)
            File.ReadAllText(project).Should().NotContain("<PackageReleaseNotes>",
                $"{Path.GetFileName(project)}: the notes come from CHANGELOG.md, one copy for all five packages");
    }

    /// <summary>The body of <c>## [name]</c> without HTML comments, or null when there is no such heading.</summary>
    private static string? Section(string name)
    {
        var m = Regex.Match(Changelog,
            @"^## \[" + Regex.Escape(name) + @"\][^\n]*\n(?<body>.*?)(?=^## |^\[[^\]\n]+\]:\s|\z)",
            RegexOptions.Multiline | RegexOptions.Singleline);
        return m.Success ? Regex.Replace(m.Groups["body"].Value, "<!--.*?-->", "", RegexOptions.Singleline).Trim() : null;
    }

    /// <summary>SemVer 2.0.0 precedence: positive when <paramref name="a"/> is newer.</summary>
    private static int Compare(string a, string b)
    {
        var ma = Version.Match(a);
        var mb = Version.Match(b);
        var core = System.Version.Parse(ma.Groups["core"].Value).CompareTo(System.Version.Parse(mb.Groups["core"].Value));
        if (core != 0) return core;

        var pa = ma.Groups["pre"].Success ? ma.Groups["pre"].Value : null;
        var pb = mb.Groups["pre"].Success ? mb.Groups["pre"].Value : null;
        if (pa is null || pb is null) return pa is null ? (pb is null ? 0 : 1) : -1;

        var xa = pa.Split('.');
        var xb = pb.Split('.');
        for (var i = 0; i < Math.Min(xa.Length, xb.Length); i++)
        {
            var na = int.TryParse(xa[i], out var ia);
            var nb = int.TryParse(xb[i], out var ib);
            var c = na && nb ? ia.CompareTo(ib)
                  : na ? -1 : nb ? 1
                  : string.CompareOrdinal(xa[i], xb[i]);
            if (c != 0) return c;
        }
        return xa.Length.CompareTo(xb.Length);
    }
}
