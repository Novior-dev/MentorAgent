using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Novior.MentorAgent.Tests.Docs;

/// <summary>
/// A dependency with a published vulnerability of high or critical severity does not reach a package.
///
/// <para>
/// 1.0.0 shipped <c>Novior.MentorAgent.Blazor</c> on <c>Microsoft.AspNetCore.SignalR.Client</c> 10.0.3, whose
/// <c>Microsoft.AspNetCore.SignalR.Common</c> got a high-severity advisory (GHSA-334f-vvp4-7g6p,
/// CVE-2026-70159) the day 1.0.0 was published; every library build reported it as warning NU1903 and went on
/// (BUG-173). NuGet audit now fails a Release build on such a package. These tests keep that rule in the build
/// files, and the patched SignalR client in the projects, also on a machine that cannot reach the advisory
/// database (there NuGet reports NU1900 and audits nothing).
/// </para>
/// </summary>
public sealed class DependencyAuditTests
{
    private static string Props => Path.Combine(RepoFiles.Root, "Directory.Build.props");
    private static string Targets => Path.Combine(RepoFiles.Root, "Directory.Build.targets");

    /// <summary>The repository's project files: the five packages, the tests and anything added beside them.</summary>
    private static IEnumerable<string> ProjectFiles() =>
        Directory.EnumerateFiles(RepoFiles.Root, "*.csproj", SearchOption.AllDirectories)
            .Where(p => !p.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"));

    private static IEnumerable<string> Codes(IEnumerable<string> values) =>
        values.SelectMany(v => v.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));

    [Fact]
    public void A_high_or_critical_advisory_fails_a_Release_build()
    {
        var props = XDocument.Load(Props);
        var release = props.Descendants("PropertyGroup")
            .Where(g => ((string?)g.Attribute("Condition") ?? "").Replace(" ", "") == "'$(Configuration)'=='Release'")
            .Descendants("WarningsAsErrors")
            .Select(e => e.Value);

        Codes(release).Should().Contain(["NU1903", "NU1904"], "NU1903 is a high-severity advisory, NU1904 a critical one");
        props.Descendants("NuGetAuditMode").Single().Value.Trim().Should().Be("all",
            "SignalR.Common reached the Blazor package through another package, not as a direct reference");
    }

    [Fact]
    public void No_build_file_turns_the_audit_off_or_silences_a_serious_advisory()
    {
        foreach (var file in ProjectFiles().Append(Props).Append(Targets))
        {
            var doc = XDocument.Load(file);

            doc.Descendants("NuGetAudit").Select(e => e.Value.Trim().ToLowerInvariant())
                .Should().NotContain("false", file);
            doc.Descendants("NuGetAuditLevel").Select(e => e.Value.Trim().ToLowerInvariant())
                .Should().NotContain("critical", $"{file} would let a high-severity advisory through");

            var silenced = doc.Descendants("NoWarn").Concat(doc.Descendants("WarningsNotAsErrors")).Select(e => e.Value)
                .Concat(doc.Descendants("PackageReference").Select(r => (string?)r.Attribute("NoWarn") ?? ""));
            Codes(silenced).Should().NotContain(["NU1903", "NU1904"], file);
        }
    }

    [Fact]
    public void The_SignalR_client_is_the_release_that_fixes_CVE_2026_70159()
    {
        var references = ProjectFiles()
            .SelectMany(p => XDocument.Load(p).Descendants("PackageReference")
                .Where(r => (string?)r.Attribute("Include") == "Microsoft.AspNetCore.SignalR.Client")
                .Select(r => (Project: Path.GetFileNameWithoutExtension(p), Version: Version.Parse((string)r.Attribute("Version")!))))
            .ToList();

        references.Select(r => r.Project).Should().Contain(RepoFiles.Blazor);
        references.Should().OnlyContain(r => r.Version >= new Version(10, 0, 13),
            "Microsoft.AspNetCore.SignalR.Common 10.0.0 to 10.0.12 carry GHSA-334f-vvp4-7g6p");
    }
}
