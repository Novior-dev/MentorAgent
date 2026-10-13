using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Novior.MentorAgent.Tests.Docs;

/// <summary>
/// What the five package READMEs say about the version they ship with. They are the package pages on
/// nuget.org, so a stale version or a stale "preview" banner is the first thing a reader sees.
///
/// <para>
/// The MAUI snippet in the core README pinned <c>Version="1.0.0-rc.4"</c> through six published
/// packages, rc.5 to rc.10, because nothing compared it with <c>Directory.Build.props</c>.
/// </para>
/// </summary>
public sealed class ReleaseReadinessTests
{
    private static string Version => XDocument.Load(Path.Combine(RepoFiles.Root, "Directory.Build.props"))
        .Descendants("Version").Single().Value.Trim();

    /// <summary>The ids the five projects pack under, read from their <c>.csproj</c>.</summary>
    private static HashSet<string> PackageIds() => RepoFiles.Packages
        .Select(p => XDocument.Load(Path.Combine(RepoFiles.Root, p, p + ".csproj")).Descendants("PackageId").Single().Value.Trim())
        .ToHashSet(StringComparer.Ordinal);

    // A package id as the READMEs write it, before and after the 1.0.0 rename to Novior.MentorAgent. Each
    // match is then held to the ids the projects pack, so a README left on the other name fails here
    // rather than matching nothing and passing.
    private const string Id = @"(?<id>(Novior\.)?MentorAgent(\.\w+)?)";

    [Fact]
    public void Every_package_reference_in_the_readmes_names_the_version_being_built()
    {
        var reference = new Regex(@"<PackageReference\s+Include=""" + Id + @"""\s+Version=""(?<v>[^""]+)""");
        var ids = PackageIds();
        var references = 0;

        foreach (var (name, path) in RepoFiles.Readmes)
            foreach (Match m in reference.Matches(File.ReadAllText(path)))
            {
                references++;
                ids.Should().Contain(m.Groups["id"].Value, $"{name} README references a package this repository builds");
                m.Groups["v"].Value.Should().Be(Version,
                    $"{name} README pins {m.Groups["id"].Value} to {m.Groups["v"].Value}");
            }

        references.Should().BePositive("the core README shows the PackageReference of a MAUI host");
    }

    [Fact]
    public void A_stable_version_ships_without_the_preview_banner()
    {
        if (Version.Contains('-'))
            return;   // a prerelease: the banner is true

        foreach (var (name, path) in RepoFiles.Readmes)
            File.ReadAllText(path).Should().NotContainAny(
                ["Preview Release", "public preview", "APIs may change before the stable release",
                 "Release candidate", "release candidate for"],
                $"{name} README still announces a preview, and {Version} is stable");
    }

    /// <summary>
    /// <c>dotnet add package</c> without a version looks only at stable releases, so for a prerelease it
    /// needs <c>--prerelease</c> or it stops (BUG-141: three commands in the rc.14 READMEs lacked it). For
    /// a stable version the flag must go, or the command installs the next prerelease once there is one.
    /// </summary>
    [Fact]
    public void Every_install_command_in_the_readmes_fits_the_version_being_built()
    {
        var install = new Regex(@"dotnet add package " + Id + @"\b(?<args>[^`\r\n]*)");
        var prerelease = Version.Contains('-');
        var ids = PackageIds();
        var commands = 0;

        foreach (var (name, path) in RepoFiles.Readmes)
            foreach (Match m in install.Matches(File.ReadAllText(path)))
            {
                commands++;
                ids.Should().Contain(m.Groups["id"].Value, $"{name} README installs a package this repository builds");
                m.Groups["args"].Value.Contains("--prerelease").Should().Be(prerelease,
                    $"{name} README: \"{m.Value.Trim()}\" for {Version}, " +
                    (prerelease ? "a prerelease, needs --prerelease" : "a stable version, must not pass --prerelease"));
            }

        commands.Should().BePositive("the READMEs tell the reader how to install the packages");
    }
}
