using FluentAssertions;
using Xunit;

namespace Novior.MentorAgent.Tests.Docs;

/// <summary>
/// <see cref="RepoFiles"/> is how the documentation suites read the repository, so a gap in it is a gap
/// in all of them: a package that drops out of <see cref="RepoFiles.SourceText"/> leaves every "does
/// this symbol exist" check running on the other four, and nothing fails.
/// </summary>
public sealed class RepoFilesTests
{
    [Fact]
    public void Every_package_contributes_its_source()
    {
        foreach (var package in RepoFiles.Packages)
        {
            var folder = Path.Combine(RepoFiles.Root, package) + Path.DirectorySeparatorChar;

            File.Exists(Path.Combine(folder, package + ".csproj")).Should().BeTrue(
                $"{package} is named in RepoFiles as a project folder holding {package}.csproj");
            RepoFiles.SourceFiles.Count(f => f.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
                .Should().BePositive($"{package} has source files");
        }

        // 175 files on 9 October 2026: a floor well below that catches an enumeration that stopped
        // matching (an extension, a folder filter) without failing on ordinary growth or cleanup.
        RepoFiles.SourceFiles.Count.Should().BeGreaterThan(150);
    }

    [Fact]
    public void The_package_list_is_every_packable_project_of_the_solution()
    {
        // Story 1636: the sixth package, Novior.MentorAgent.Scanner. A package missing from RepoFiles.Packages is missing
        // from every check that reads the list: metadata, README references, the local feeds of the package tests.
        var solution = System.Xml.Linq.XDocument.Load(Path.Combine(RepoFiles.Root, RepoFiles.Solution));
        var packable = solution.Descendants("Project")
            .Select(p => Path.Combine(RepoFiles.Root, ((string)p.Attribute("Path")!).Replace('/', Path.DirectorySeparatorChar)))
            .Where(path => System.Xml.Linq.XDocument.Load(path).Descendants("IsPackable").Any(e => e.Value.Trim() == "true"))
            .Select(path => Path.GetFileNameWithoutExtension(path));

        packable.Should().BeEquivalentTo(RepoFiles.Packages);
    }

    [Fact]
    public void A_test_run_writes_the_ledgers_outside_the_tracked_files_unless_asked()
    {
        // BUG-190: every dotnet test rewrote option-coverage.md and claims.tsv, so a run left the working tree changed.
        var tracked = Path.Combine(RepoFiles.Root, RepoFiles.Tests, "E2E", "option-coverage.md");

        RepoFiles.LedgerOutput(tracked, writeTracked: false)
            .Should().Be(Path.Combine(RepoFiles.Root, RepoFiles.Tests, "TestResults", "option-coverage.md"));
        File.ReadAllLines(Path.Combine(RepoFiles.Root, ".gitignore"))
            .Should().Contain("TestResults/", "the default output must be a folder git ignores");

        RepoFiles.LedgerOutput(tracked, writeTracked: true).Should().Be(tracked);
    }
}
