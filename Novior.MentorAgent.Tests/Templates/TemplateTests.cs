using System.Diagnostics;
using System.Security;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using Novior.MentorAgent.Tests.Docs;
using Novior.MentorAgent.Tests.Harness;
using Xunit;

namespace Novior.MentorAgent.Tests.Templates;

/// <summary>
/// The <c>dotnet new</c> template in <c>templates/mentoragent-blazor</c>: a Blazor Server helpdesk with a MentorAgent
/// assistant and a test project that runs it with a scripted model. These checks read its files; the build of the
/// project it creates is in <see cref="TemplateBuildTests"/>, in the release run only.
/// </summary>
public sealed class TemplateTests
{
    internal static readonly string Folder = Path.Combine(RepoFiles.Root, "templates", "mentoragent-blazor");

    internal static string Version => XDocument.Load(Path.Combine(RepoFiles.Root, "Directory.Build.props"))
        .Descendants("Version").Single().Value.Trim();

    private static JsonElement Config()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(Folder, ".template.config", "template.json")));
        return json.RootElement.Clone();
    }

    private static string SourceName => Config().GetProperty("sourceName").GetString()!;

    private static string AppProject => Path.Combine(Folder, SourceName, $"{SourceName}.csproj");

    private static string TestProject => Path.Combine(Folder, $"{SourceName}.Tests", $"{SourceName}.Tests.csproj");

    /// <summary>The template's files as <c>dotnet new</c> copies them: without its configuration and any build output.</summary>
    private static IEnumerable<string> ContentFiles() =>
        Directory.EnumerateFiles(Folder, "*", SearchOption.AllDirectories)
            .Where(f => !Path.GetRelativePath(Folder, f).Split(Path.DirectorySeparatorChar)
                .Any(part => part is ".template.config" or "bin" or "obj" or ".vs"));

    private static string? PackageVersion(string project, string id) => XDocument.Load(project)
        .Descendants("PackageReference")
        .Where(r => (string?)r.Attribute("Include") == id)
        .Select(r => (string?)r.Attribute("Version"))
        .SingleOrDefault();

    [Fact]
    public void The_template_is_named_and_its_source_name_works_for_any_project_name()
    {
        var config = Config();
        config.GetProperty("identity").GetString().Should().Be("Novior.MentorAgent.Templates.BlazorServer");
        config.GetProperty("shortName").GetString().Should().Be("mentoragent-blazor");

        // With a dash, the source name has two forms: as it is in the file names and paths, and with an underscore in
        // the namespaces. `dotnet new` replaces each with the same form of the new name, so my-helpdesk.csproj gets the
        // namespace my_helpdesk. Without the dash both would be one form, and a dashed name broke the project's paths.
        SourceName.Should().Contain("-");
        File.Exists(AppProject).Should().BeTrue("the application project is named after the source name");
        File.Exists(TestProject).Should().BeTrue("the test project is named after the source name");
        File.Exists(Path.Combine(Folder, $"{SourceName}.slnx")).Should().BeTrue("the solution is named after the source name");

        var namespaceForm = SourceName.Replace('-', '_');
        var cs = ContentFiles().Where(f => f.EndsWith(".cs", StringComparison.Ordinal)).Select(File.ReadAllText).ToList();
        cs.Should().OnlyContain(text => !text.Contains(SourceName), "a namespace or using with a dash does not compile");
        cs.Should().Contain(text => text.Contains($"namespace {namespaceForm}"));
    }

    [Fact]
    public void Each_project_it_creates_gets_its_own_user_secrets_and_ports()
    {
        var config = Config();

        // A guid listed in template.json is replaced by a new one in every project: the key in the user secrets of
        // one project is not read by another.
        var guids = config.GetProperty("guids").EnumerateArray().Select(g => g.GetString()!).ToList();
        guids.Should().ContainSingle();
        XDocument.Load(AppProject).Descendants("UserSecretsId").Single().Value.Should().Be(guids[0]);

        // The ports of launchSettings.json are replaced by free ones; the numbers must appear nowhere else, or they
        // would be replaced there too.
        var ports = config.GetProperty("symbols").EnumerateObject()
            .Where(s => s.Value.GetProperty("generator").GetString() == "port")
            .Select(s => s.Value.GetProperty("replaces").GetString()!)
            .ToList();
        ports.Should().HaveCount(2);
        foreach (var port in ports)
            ContentFiles().Where(f => File.ReadAllText(f).Contains(port)).Select(Path.GetFileName)
                .Should().Equal(["launchSettings.json"], $"{port} is the port that template.json replaces");
    }

    [Fact]
    public void The_project_it_creates_references_this_version_of_the_library()
    {
        PackageVersion(AppProject, "Novior.MentorAgent").Should().Be(Version,
            "the template is released with the library, and its project uses the packages of that release");
    }

    [Fact]
    public void Its_OpenAI_adapter_has_the_version_of_the_librarys_Microsoft_Extensions_AI()
    {
        // As for the demo: Microsoft.Extensions.AI.OpenAI is built against one Microsoft.Extensions.AI, which the
        // library brings, and a pair that does not match fails at run time.
        var library = PackageVersion(Path.Combine(RepoFiles.Root, RepoFiles.Core, $"{RepoFiles.Core}.csproj"), "Microsoft.Extensions.AI");
        library.Should().NotBeNullOrEmpty();
        PackageVersion(AppProject, "Microsoft.Extensions.AI.OpenAI").Should().Be(library);
    }

    [Fact]
    public void Its_test_packages_are_the_versions_of_the_librarys_own_tests()
    {
        // One set of test packages for the repository: the build of the template in the release run then needs
        // nothing that the library's own tests have not already restored.
        var ours = Path.Combine(RepoFiles.Root, RepoFiles.Tests, $"{RepoFiles.Tests}.csproj");
        foreach (var id in new[] { "Microsoft.NET.Test.Sdk", "xunit", "xunit.runner.visualstudio" })
            PackageVersion(TestProject, id).Should().Be(PackageVersion(ours, id), id);
    }

    [Fact]
    public void No_settings_file_of_the_template_holds_a_key()
    {
        // The model key goes in the user secrets of the project it creates, never in a file that travels with it.
        var secret = new Regex(@"""[^""]*(key|secret|password|token|connectionstring|credential)[^""]*""\s*:", RegexOptions.IgnoreCase);
        var files = ContentFiles().Where(f => Path.GetFileName(f).StartsWith("appsettings", StringComparison.OrdinalIgnoreCase)).ToList();

        files.Should().NotBeEmpty();
        foreach (var file in files)
            secret.Matches(File.ReadAllText(file)).Select(m => m.Value).Should().BeEmpty(file);
    }

    [Fact]
    public void The_template_is_neither_built_nor_packed_with_the_library()
    {
        // In 1.3.0 the template is installed from this folder; it is not a package yet, and the release pipeline
        // expects the five packages of the solution.
        File.ReadAllText(Path.Combine(RepoFiles.Root, RepoFiles.Solution)).Should().NotContain("templates/");
        File.ReadAllText(Path.Combine(RepoFiles.Root, "templates", "Directory.Build.props"))
            .Should().Contain("<IsPackable>false</IsPackable>");
        File.Exists(Path.Combine(RepoFiles.Root, "templates", "Directory.Build.targets")).Should().BeTrue(
            "without it the template's projects, opened here, would take the packages' build rules from the repository root");
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TemplateBuildCollection
{
    public const string Name = "Template build";
}

/// <summary>
/// The criterion of the template: <c>dotnet new</c> creates a project that builds, and whose tests see the assistant
/// run an action after a confirmation. The template is installed from its folder in a private hive, the project is
/// restored from the five packages of this build and the packages already on the machine, with no source on the
/// network, and its tests run. Minutes, not seconds: in the release run only, and alone, so its builds do not slow
/// the other tests.
/// </summary>
[Collection(TemplateBuildCollection.Name)]
public sealed class TemplateBuildTests
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromMinutes(5);

    [ReleaseGateFact("it installs the template and builds the project it creates, which takes minutes")]
    public async Task The_project_it_creates_builds_and_its_tests_pass_with_the_packages_of_this_build()
    {
        var version = TemplateTests.Version;
        AppContext.BaseDirectory.Should().Contain($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            "the project is restored from the packages of the Release build: run the tests with -c Release");

        var work = Directory.CreateTempSubdirectory("mentoragent-template-").FullName;
        try
        {
            // The five packages of this build, and nothing else, as the package source.
            var feed = Directory.CreateDirectory(Path.Combine(work, "feed")).FullName;
            foreach (var id in RepoFiles.Packages)
            {
                var nupkg = Path.Combine(RepoFiles.Root, id, "bin", "Release", $"{id}.{version}.nupkg");
                File.Exists(nupkg).Should().BeTrue($"`dotnet build {RepoFiles.Solution} -c Release` writes {Path.GetFileName(nupkg)}");
                File.Copy(nupkg, Path.Combine(feed, Path.GetFileName(nupkg)));
            }

            var core = Path.Combine(RepoFiles.Root, RepoFiles.Core, "bin", "Release", $"{RepoFiles.Core}.{version}.nupkg");
            File.GetLastWriteTimeUtc(core).Should().BeOnOrAfter(
                File.GetLastWriteTimeUtc(Path.Combine(AppContext.BaseDirectory, $"{RepoFiles.Core}.dll")),
                "a package older than the library these tests run is from an earlier build");

            var hive = Path.Combine(work, "hive");
            var packages = Path.Combine(work, "packages");
            var project = Path.Combine(work, "Contoso.Helpdesk");
            var results = Path.Combine(work, "results");

            await DotnetAsync(work, "new", "install", TemplateTests.Folder, "--debug:custom-hive", hive);
            await DotnetAsync(work, "new", "mentoragent-blazor", "-n", "Contoso.Helpdesk", "-o", project,
                "--no-update-check", "--debug:custom-hive", hive);

            await File.WriteAllTextAsync(Path.Combine(project, "nuget.config"), NuGetConfig(feed, packages));

            await DotnetAsync(project, "test", "Contoso.Helpdesk.slnx", "--logger", "trx", "--results-directory", results);

            var counters = XDocument.Load(Directory.EnumerateFiles(results, "*.trx").Single())
                .Descendants().Single(e => e.Name.LocalName == "Counters");
            var (total, passed, failed) = ((int)counters.Attribute("total")!, (int)counters.Attribute("passed")!,
                (int)counters.Attribute("failed")!);
            failed.Should().Be(0);
            passed.Should().Be(total).And.BeGreaterThanOrEqualTo(3, "the template comes with three tests");

            // From this build's packages, not from a copy already in the machine's cache.
            Directory.Exists(Path.Combine(packages, RepoFiles.Core.ToLowerInvariant(), version)).Should().BeTrue(
                $"{RepoFiles.Core} {version} was taken from the machine's cache instead of this build's packages");
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// The feed of this build as the only source; the packages it restores in a folder of the test, so a 1.3.0 built
    /// here never lands in the machine's cache; that cache as a read-only fallback for everything else. A package
    /// missing from it fails the restore (NU1101) instead of being downloaded.
    /// </summary>
    private static string NuGetConfig(string feed, string packages)
    {
        var cache = Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } named
            ? named
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <config>
                <add key="globalPackagesFolder" value="{SecurityElement.Escape(packages)}" />
              </config>
              <packageSources>
                <clear />
                <add key="this-build" value="{SecurityElement.Escape(feed)}" />
              </packageSources>
              <fallbackPackageFolders>
                <clear />
                <add key="machine-cache" value="{SecurityElement.Escape(cache)}" />
              </fallbackPackageFolders>
            </configuration>
            """;
    }

    private static async Task DotnetAsync(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        // A build of its own: none of this run's MSBuild, test host, package folder or gate settings.
        foreach (var name in start.Environment.Keys.ToList())
        {
            if (name.StartsWith("MSBUILD", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("VSTEST", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("TESTINGPLATFORM", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("MENTORAGENT_", StringComparison.OrdinalIgnoreCase)
                || name.Equals("DOTNET_HOST_PATH", StringComparison.OrdinalIgnoreCase)
                || name.Equals("NUGET_PACKAGES", StringComparison.OrdinalIgnoreCase))
                start.Environment.Remove(name);
        }
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";
        // No build server left running after the test, holding files of the folder it deletes.
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        start.Environment["UseSharedCompilation"] = "false";

        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(StepTimeout);
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"dotnet {string.Join(' ', arguments)} did not end within {StepTimeout.TotalMinutes} minutes.");
        }

        var output = (await stdout) + (await stderr);
        var tail = string.Join(Environment.NewLine, output.Split('\n').TakeLast(60));
        process.ExitCode.Should().Be(0, $"dotnet {string.Join(' ', arguments)} failed:{Environment.NewLine}{tail}");
    }
}
