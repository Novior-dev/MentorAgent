using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Novior.MentorAgent.Tests.Docs;

/// <summary>
/// The licence and the copyright the packages declare agree with the files they ship. Up to 1.0.0-rc.14
/// the packages declared MIT; from rc.15 the expression, <c>LICENSE</c> and <c>NOTICE</c> change together,
/// and a package that packed <c>LICENSE</c> without <c>NOTICE</c> would drop the notice Apache 2.0 asks
/// every redistributor to keep.
/// </summary>
public sealed class PackageMetadataTests
{
    private static string Property(string name) =>
        XDocument.Load(Path.Combine(RepoFiles.Root, "Directory.Build.props"))
            .Descendants(name).Single().Value.Trim();

    [Fact]
    public void The_license_expression_names_the_text_in_LICENSE()
    {
        Property("PackageLicenseExpression").Should().Be("Apache-2.0");
        File.ReadAllText(Path.Combine(RepoFiles.Root, "LICENSE"))
            .Should().Contain("Apache License").And.Contain("Version 2.0, January 2004");
    }

    [Fact]
    public void The_copyright_is_the_one_in_NOTICE_and_its_holders_are_the_authors()
    {
        var copyright = Property("Copyright");
        File.ReadAllLines(Path.Combine(RepoFiles.Root, "NOTICE")).Should().Contain(copyright);

        var holders = Regex.Match(copyright, @"^Copyright \d{4}(-\d{4})? (?<holders>.+)$").Groups["holders"].Value;
        holders.Split(" and ").Should().BeEquivalentTo(
            Property("Authors").Split(',', StringSplitOptions.TrimEntries),
            "the package authors are the copyright holders");
    }

    [Theory]
    [InlineData(RepoFiles.Core)]
    [InlineData(RepoFiles.Abstractions)]
    [InlineData(RepoFiles.Blazor)]
    [InlineData(RepoFiles.Declarative)]
    [InlineData(RepoFiles.Server)]
    public void Every_package_ships_LICENSE_and_NOTICE(string package)
    {
        var packed = XDocument.Load(Path.Combine(RepoFiles.Root, package, package + ".csproj"))
            .Descendants("None")
            .Where(e => (string?)e.Attribute("Pack") == "true" && (string?)e.Attribute("PackagePath") == @"\")
            .Select(e => (string?)e.Attribute("Include"));

        packed.Should().Contain([@"..\LICENSE", @"..\NOTICE"], $"{package} ships the licence with its notice");
    }
}
