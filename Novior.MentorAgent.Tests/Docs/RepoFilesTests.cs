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
}
