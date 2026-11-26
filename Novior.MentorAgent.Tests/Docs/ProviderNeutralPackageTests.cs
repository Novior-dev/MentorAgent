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

    /// <summary>
    /// BUG-120 — a build or buildTransitive target runs in every consumer's build, and MENTOR001 was one: up to rc.14 a
    /// check of this kind stopped every build that resolved OpenAI 2.11 or later. The core package carries one file there
    /// and no other, <c>buildTransitive/Novior.MentorAgent.targets</c>, which embeds the AutoMentor's manifest of the
    /// pages when the project has the file and does nothing without it (story 1635). It is allowed because it cannot stop
    /// a build: it holds property and item groups only — a default path the project can change, the manifest embedded
    /// under a fixed name, taken out of Content and None — and every item group asks whether the file exists. Anything
    /// else, a target, an error, a warning, a message, a task, an import, a choice or an element not listed here, fails
    /// this test.
    /// </summary>
    [Fact]
    public void The_core_package_ships_only_the_build_file_that_embeds_the_manifest_and_it_cannot_stop_a_build()
    {
        var packed = CoreProject().Descendants("None")
            .Where(n => (string?)n.Attribute("Pack") == "true")
            .Where(n => ((string?)n.Attribute("PackagePath") ?? "").StartsWith("build", StringComparison.OrdinalIgnoreCase))
            .Select(n => ((string?)n.Attribute("Include"), (string?)n.Attribute("PackagePath")));
        packed.Should().Equal([(@"buildTransitive\Novior.MentorAgent.targets", @"buildTransitive\")],
            "the one build file of the core package is the targets that embed the manifest");

        // The folder first: under another name the assertions below would hold for any package.
        var core = Path.Combine(RepoFiles.Root, RepoFiles.Core);
        Directory.Exists(core).Should().BeTrue("the core package lives in {0}", core);
        Directory.Exists(Path.Combine(core, "build")).Should().BeFalse();
        Directory.EnumerateFileSystemEntries(Path.Combine(core, "buildTransitive")).Select(Path.GetFileName)
            .Should().Equal("Novior.MentorAgent.targets");

        var project = XDocument.Load(Path.Combine(core, "buildTransitive", "Novior.MentorAgent.targets")).Root!;
        project.Name.LocalName.Should().Be("Project");
        project.Attributes().Should().BeEmpty("no Sdk, no InitialTargets, no DefaultTargets");

        foreach (var group in project.Elements())
        {
            group.Name.LocalName.Should().BeOneOf(["PropertyGroup", "ItemGroup"], "only property and item groups, nothing that runs");
            if (group.Name.LocalName == "PropertyGroup")
            {
                group.Attributes().Should().BeEmpty();
                var property = group.Elements().Should().ContainSingle().Subject;
                property.Name.LocalName.Should().Be("MentorUiManifest");
                property.Attributes().Select(a => (a.Name.LocalName, a.Value))
                    .Should().Equal([("Condition", "'$(MentorUiManifest)' == ''")], "the project can set another path");
                property.Value.Should().StartWith("$(MSBuildProjectDirectory)", "the default path is the project's");
                property.Elements().Should().BeEmpty();
            }
            else
            {
                group.Attributes().Select(a => a.Name.LocalName).Should().Equal("Condition");
                group.Attribute("Condition")!.Value.Should().Contain("Exists(", "without the file it does nothing");
                foreach (var item in group.Elements())
                {
                    item.Elements().Should().BeEmpty();
                    var attributes = item.Attributes().Select(a => a.Name.LocalName).ToList();
                    switch (item.Name.LocalName)
                    {
                        case "EmbeddedResource":
                            attributes.Should().Equal("Include", "LogicalName");
                            item.Attribute("LogicalName")!.Value.Should().Be("Novior.MentorAgent.UiManifest.json",
                                "a fixed name: reading it does not depend on the application's RootNamespace");
                            break;
                        case "Content" or "None":
                            attributes.Should().Equal("Remove");
                            break;
                        default:
                            item.Name.LocalName.Should().BeOneOf(["EmbeddedResource", "Content", "None"],
                                "the only items are the manifest embedded, and taken out of Content and None");
                            break;
                    }
                }
            }
        }
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
