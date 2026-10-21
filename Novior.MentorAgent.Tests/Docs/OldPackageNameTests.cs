using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Novior.MentorAgent.Tests.Docs;

/// <summary>
/// The five package READMEs name no package, namespace or path of the <c>MentorAgent.*</c> packages outside the guide
/// that moves an application off them, "Migrating from the MentorAgent.* packages".
///
/// <para>
/// From 1.0.0 the packages are <c>Novior.MentorAgent.*</c>. The old ones stay on nuget.org, deprecated, so a README
/// line left on an old name still builds for whoever copies it, and keeps that application on 1.0.0-rc.15 without a
/// warning. The READMEs of the published 1.0.0 were checked by hand; this repeats the check for every version.
/// </para>
///
/// <para>
/// What kept its name on purpose is not an old name: methods and classes (<c>AddMentorAgent()</c>,
/// <c>[MentorAgent]</c>, <c>MentorOptions</c>), the <c>MentorAgent</c> section of appsettings, <c>MentorAgent.css</c>
/// and <c>MentorAgent.js</c>, and the CORS policy <c>MentorAgentMcp</c>.
/// </para>
/// </summary>
public sealed class OldPackageNameTests
{
    private const string MigrationHeading = "## Migrating from the MentorAgent.* packages";

    // A dotted name on the old prefix: a package id, a namespace, an assembly, a _content path or a log category. Not
    // after a word character or a dot, so neither Novior.MentorAgent.X nor AddMentorAgent matches.
    private static readonly Regex OldDottedName = new(@"(?<![\w.])MentorAgent\.(?<rest>[A-Za-z]\w*)");

    // The old core package, whose id had no suffix: in a reference, an install command or a nuget.org link.
    private static readonly Regex OldCorePackage = new(@"(?:Include=""|package\s+|nuget\.org/packages/)MentorAgent(?![\w.])");

    // A using of the old root namespace itself.
    private static readonly Regex OldRootUsing = new(@"\busing\s+MentorAgent\s*;");

    // The two files that kept the old name: the widget's stylesheet and script.
    private static readonly HashSet<string> KeptFileExtensions = new(StringComparer.Ordinal) { "css", "js" };

    /// <summary>
    /// The old names in <paramref name="readme"/> outside the migration guide, as "R line: name". The guide runs from
    /// its heading to the next level-2 heading; a line inside a fenced code block is never a heading.
    /// </summary>
    internal static IEnumerable<string> OldNames(string readme)
    {
        var inGuide = false;
        var inFence = false;
        var number = 0;

        foreach (var raw in readme.Split('\n'))
        {
            number++;
            var line = raw.TrimEnd('\r');

            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                inFence = !inFence;
            else if (!inFence && line.StartsWith("## ", StringComparison.Ordinal))
                inGuide = line.TrimEnd() == MigrationHeading;

            if (inGuide) continue;

            foreach (Match m in OldDottedName.Matches(line))
                if (!KeptFileExtensions.Contains(m.Groups["rest"].Value))
                    yield return $"R {number}: {m.Value}";

            foreach (Match m in OldCorePackage.Matches(line))
                yield return $"R {number}: {m.Value}";

            foreach (Match m in OldRootUsing.Matches(line))
                yield return $"R {number}: {m.Value}";
        }
    }

    [Fact]
    public void No_readme_names_an_old_package_namespace_or_path_outside_the_migration_guide()
    {
        RepoFiles.Readme("core").Should().Contain(MigrationHeading,
            "the guide this test leaves out is the core README's; renamed, its old names would be read as mistakes");

        var found = RepoFiles.Readmes
            .SelectMany(r => OldNames(File.ReadAllText(r.Value)).Select(o => $"{r.Key} README {o}"))
            .ToList();

        string.Join(Environment.NewLine, found).Should().BeEmpty(
            "a README line on a MentorAgent.* name keeps whoever copies it on 1.0.0-rc.15");
    }

    [Theory]
    [InlineData("using MentorAgent.Extensions;")]
    [InlineData("@using MentorAgent.Blazor.Components")]
    [InlineData("using MentorAgent;")]
    [InlineData("<PackageReference Include=\"MentorAgent\" Version=\"1.0.0\" />")]
    [InlineData("<PackageReference Include=\"MentorAgent.Server\" Version=\"1.0.0\" />")]
    [InlineData("dotnet add package MentorAgent")]
    [InlineData("dotnet add package MentorAgent.Blazor")]
    [InlineData("[MentorAgent](https://www.nuget.org/packages/MentorAgent)")]
    [InlineData("<link href=\"_content/MentorAgent.Abstractions/css/MentorAgent.css\" rel=\"stylesheet\" />")]
    [InlineData("\"MentorAgent.Core.MentorOrchestrator\": \"Debug\"")]
    public void An_old_name_is_found(string line)
        => OldNames(line).Should().NotBeEmpty();

    [Theory]
    [InlineData("builder.Services.AddMentorAgent(options => { });")]
    [InlineData("[MentorAgent(Name = \"ShippingAgent\")]")]
    [InlineData("\"MentorAgent\": { \"Language\": \"Italian\" }")]
    [InlineData("<script src=\"_content/Novior.MentorAgent.Abstractions/js/MentorAgent.js?v=10\"></script>")]
    [InlineData("`MentorAgent.css` and `MentorAgent.js` keep their names.")]
    [InlineData("app.UseCors(\"MentorAgentMcp\");")]
    [InlineData("dotnet add package Novior.MentorAgent.Server")]
    [InlineData("<PackageReference Include=\"Novior.MentorAgent\" Version=\"1.1.0\" />")]
    [InlineData("using Novior.MentorAgent.Extensions;")]
    [InlineData("up to 1.0.0-rc.15 they were `MentorAgent.*`.")]
    public void A_name_kept_on_purpose_is_not_an_old_name(string line)
        => OldNames(line).Should().BeEmpty();

    [Fact]
    public void Only_the_migration_guide_is_left_out()
    {
        var readme = string.Join("\r\n",
            "# Title",
            MigrationHeading,
            "using MentorAgent.Extensions;",
            "```bash",
            "## a comment in a script, not a heading",
            "dotnet add package MentorAgent.Server",
            "```",
            "## What comes next",
            "using MentorAgent.Core;");

        OldNames(readme).Should().Equal("R 9: MentorAgent.Core");
    }
}
