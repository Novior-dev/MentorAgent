using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Novior.MentorAgent.Tests.Docs;

/// <summary>
/// Every package is built with its software bill of materials (Microsoft.Sbom.Targets writes it at pack time, inside the
/// .nupkg). The release pipeline checks the packages it builds; this pins the setting that produces it, so a cleanup of
/// Directory.Build.props cannot drop it, or turn the tool into a dependency of the applications.
/// </summary>
public sealed class SbomConfigurationTests
{
    private static readonly XDocument Props = XDocument.Load(Path.Combine(RepoFiles.Root, "Directory.Build.props"));

    private const string PackagesOnly = "!$(MSBuildProjectName.Contains('.Tests'))";

    [Fact]
    public void The_packages_generate_a_bill_of_materials()
    {
        var generate = Props.Descendants("GenerateSBOM").SingleOrDefault();
        generate.Should().NotBeNull("Microsoft.Sbom.Targets writes the SBOM only with GenerateSBOM");
        generate!.Value.Trim().Should().Be("true");
        ((string?)generate.Parent!.Attribute("Condition")).Should().Be(PackagesOnly, "the five packages, not the test projects");
        // The packages are packed on every build (GeneratePackageOnBuild): without this a Debug build, a dotnet run of
        // the demo included, ran the tool for a few seconds and could warn that its own manifest was in use. The
        // pipeline and the local feeds build in Release.
        ((string?)generate.Attribute("Condition")).Should().Be("'$(Configuration)' == 'Release'",
            "the bill of materials is written for Release builds only");

        // Who publishes the packages. Without it the tool takes the first name of Authors, split at the comma.
        var supplier = Props.Descendants("SbomGenerationPackageSupplier").SingleOrDefault();
        supplier.Should().NotBeNull("the SBOM names the supplier of the packages");
        supplier!.Value.Trim().Should().Be("Novior");
        supplier.Parent.Should().BeSameAs(generate.Parent, "it applies to the same five packages");
    }

    [Fact]
    public void The_tool_is_a_build_time_reference_of_the_packages_only()
    {
        var reference = Props.Descendants("PackageReference")
            .SingleOrDefault(r => (string?)r.Attribute("Include") == "Microsoft.Sbom.Targets");
        reference.Should().NotBeNull();

        ((string?)reference!.Attribute("PrivateAssets")).Should().Be("all",
            "it must not become a dependency of the applications that install the packages");
        ((string?)reference.Attribute("Version")).Should().MatchRegex(@"^4\.1\.\d+$", "the version chosen for the SBOM, 4.1.x");
        ((string?)reference.Parent!.Attribute("Condition")).Should().Be(PackagesOnly);
    }
}
