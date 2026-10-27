using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Novior.MentorAgent.Tests.Docs;

/// <summary>
/// The short README at the repository's root, GitHub's front page. The long one moved to
/// <c>Novior.MentorAgent/README.md</c>: it is the package's, the one nuget.org shows and the one the documentation
/// suites read. The short one only points at things, so a later move would break it without any other test seeing.
/// </summary>
public sealed class RootReadmeTests
{
    private static readonly string Text = File.ReadAllText(Path.Combine(RepoFiles.Root, "README.md"));

    [Fact]
    public void Every_relative_link_of_the_root_readme_reaches_a_file_or_folder_of_the_repository()
    {
        var links = Regex.Matches(Text, @"\]\((?!https?://|#|mailto:)(?<path>[^)\s#]+)(?:#[^)]*)?\)")
                         .Select(m => m.Groups["path"].Value)
                         .Distinct()
                         .ToList();

        links.Should().Contain("Novior.MentorAgent/README.md").And.Contain("LICENSE");
        string.Join(", ", links.Where(l => !File.Exists(Path.Combine(RepoFiles.Root, l))
                                        && !Directory.Exists(Path.Combine(RepoFiles.Root, l))))
              .Should().BeEmpty();
    }

    [Fact]
    public void The_package_packs_the_long_readme_not_the_root_one()
    {
        // Packed from ..\README.md, nuget.org would show the short page.
        var csproj = File.ReadAllText(Path.Combine(RepoFiles.Root, RepoFiles.Core, RepoFiles.Core + ".csproj"));

        csproj.Should().Contain(@"<None Include=""README.md"" Pack=""true"" PackagePath=""\"" />");
        csproj.Should().NotContain(@"..\README.md");
        RepoFiles.Readmes["core"].Should().Be(Path.Combine(RepoFiles.Root, RepoFiles.Core, "README.md"));
    }
}
