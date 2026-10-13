using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using Novior.MentorAgent.Options;
using Xunit;

namespace Novior.MentorAgent.Tests.Docs;

/// <summary>
/// BUG-120 — MentorAgent brings no AI provider SDK, and its README says what it does bring.
///
/// <para>
/// Up to rc.14 the package referenced <c>Azure.AI.OpenAI</c> 2.9.0-beta.1, <c>Azure.Identity</c> and
/// <c>Microsoft.Extensions.AI.OpenAI [10.6.0]</c> — none of them used by the library, there so the quickstart
/// compiled — and shipped a <c>buildTransitive</c> check that stopped every build resolving <c>OpenAI</c> 2.11
/// or later with <c>MENTOR001</c>. A project on the current Agent Framework could not build with it at all. The
/// provider is now the host's choice, and these tests keep it that way.
/// </para>
/// </summary>
public sealed class ProviderNeutralPackageTests
{
    private static readonly string[] ProviderPackages =
        ["Azure.AI.OpenAI", "Azure.Identity", "Microsoft.Extensions.AI.OpenAI", "OpenAI", "Azure.Core"];

    private static XDocument CoreProject() =>
        XDocument.Load(Path.Combine(RepoFiles.Root, RepoFiles.Core, RepoFiles.Core + ".csproj"));

    private static Dictionary<string, string> PackageReferences() =>
        CoreProject().Descendants("PackageReference")
            .ToDictionary(p => (string)p.Attribute("Include")!, p => (string)p.Attribute("Version")!);

    [Fact]
    public void The_core_project_references_no_provider_SDK()
    {
        PackageReferences().Keys.Should().NotIntersectWith(ProviderPackages,
            "the host picks its provider and its version; a dependency here decides both for every host");
    }

    [Fact]
    public void The_core_assembly_references_no_provider_SDK()
    {
        var references = typeof(MentorOptions).Assembly.GetReferencedAssemblies().Select(a => a.Name!);

        references.Should().NotIntersectWith(ProviderPackages,
            "System.ClientModel is all the library reads from a provider: the status of ClientResultException");
    }

    [Fact]
    public void The_core_package_ships_no_build_targets()
    {
        var packed = CoreProject().Descendants("None")
            .Where(n => (string?)n.Attribute("Pack") == "true")
            .Select(n => (string?)n.Attribute("PackagePath") ?? "");

        packed.Should().NotContain(p => p.StartsWith("build", StringComparison.OrdinalIgnoreCase),
            "a build or buildTransitive target runs in every consumer's build — MENTOR001 was one");

        // The folder first: under another name the second assertion would hold for any package.
        var core = Path.Combine(RepoFiles.Root, RepoFiles.Core);
        Directory.Exists(core).Should().BeTrue("the core package lives in {0}", core);
        Directory.Exists(Path.Combine(core, "build")).Should().BeFalse();
    }

    [Fact]
    public void The_Requirements_table_lists_every_package_reference_at_its_version()
    {
        var readme = RepoFiles.Readme("core").Replace("\r\n", "\n");
        var start = readme.IndexOf("\n## Requirements\n", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0, "the core README has a Requirements section");
        var rows = readme[start..]
            .Split('\n')
            .SkipWhile(l => !l.StartsWith('|'))
            .TakeWhile(l => l.StartsWith('|'))
            .Skip(2)   // header and separator
            .Select(l => l.Split('|', StringSplitOptions.TrimEntries))
            .ToDictionary(c => c[1], c => c[2]);

        foreach (var (package, version) in PackageReferences())
        {
            rows.Should().ContainKey(package, "every package MentorAgent brings is listed");
            Regex.IsMatch(rows[package], @"^`?" + Regex.Escape(version) + @"\b").Should().BeTrue(
                "{0} is listed as '{1}' but referenced at {2}", package, rows[package], version);
        }

        var notPackages = new[] { ".NET", "Blazor", RepoFiles.Abstractions };
        rows.Keys.Except(notPackages).Should().BeSubsetOf(PackageReferences().Keys,
            "a row that names a package MentorAgent does not reference sends the reader after a dependency that is not there");
    }
}
