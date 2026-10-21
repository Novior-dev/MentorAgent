using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Novior.MentorAgent.Tests.Docs;

/// <summary>
/// The demo in <c>demo/</c> travels with the repository to GitHub, where anyone can read it. Its settings must hold
/// no key: the model key belongs in the user secrets of the project. It must also stay an application the pipeline
/// builds, not a package it would try to publish.
/// </summary>
public sealed class DemoRepositoryTests
{
    private static readonly string DemoRoot = Path.Combine(RepoFiles.Root, "demo");

    // A setting whose name says it holds a secret.
    private static readonly Regex SecretName = new("key|secret|password|token|connectionstring|credential", RegexOptions.IgnoreCase);

    // A value that looks like a key whatever its setting is called: an OpenAI key, or a long run of key characters.
    private static readonly Regex SecretValue = new(@"^(sk-|sk_)|^[A-Za-z0-9+/=_\-]{32,}$");

    private static IReadOnlyList<string> SettingsFiles()
        => [.. Directory.EnumerateFiles(DemoRoot, "appsettings*.json", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                         && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))];

    [Fact]
    public void No_settings_file_of_the_demo_holds_a_key()
    {
        var files = SettingsFiles();
        files.Should().NotBeEmpty("the demo has an appsettings.json; none found means this test reads the wrong folder");

        var found = new List<string>();
        foreach (var file in files)
        {
            using var json = JsonDocument.Parse(File.ReadAllText(file),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            Walk(json.RootElement, "", Path.GetRelativePath(RepoFiles.Root, file), found);
        }

        string.Join(Environment.NewLine, found).Should().BeEmpty(
            "the demo is public: the key goes in the user secrets (dotnet user-secrets set \"Demo:ApiKey\" …)");
    }

    [Fact]
    public void The_demo_keeps_its_key_in_user_secrets_and_is_not_a_package()
    {
        var project = File.ReadAllText(Path.Combine(DemoRoot, "MentorAgentDemo", "MentorAgentDemo.csproj"));
        project.Should().MatchRegex("<UserSecretsId>[^<]+</UserSecretsId>", "the README tells the visitor to set the key there");

        File.ReadAllText(Path.Combine(DemoRoot, "Directory.Build.props")).Should().Contain("<IsPackable>false</IsPackable>",
            "the pipeline packs every packable project of the solution and expects the five packages");
        File.Exists(Path.Combine(DemoRoot, "Directory.Build.targets")).Should().BeTrue(
            "without it the demo would take the packages' build rules from the repository root");

        File.ReadAllText(Path.Combine(RepoFiles.Root, RepoFiles.Solution))
            .Should().Contain("demo/MentorAgentDemo/MentorAgentDemo.csproj", "the pipeline builds the demo with the solution");
    }

    [Theory]
    [InlineData("""{ "Demo": { "ApiKey": "abc" } }""")]
    [InlineData("""{ "ConnectionStrings": { "Default": "Server=x" }, "Demo": { "Token": "t" } }""")]
    [InlineData("""{ "Demo": { "Model": "sk-proj-0123456789" } }""")]
    [InlineData("""{ "Other": "QWxhZGRpbjpvcGVuIHNlc2FtZQ0123456789abcdef" }""")]
    public void A_key_in_a_settings_file_is_found(string settings)
    {
        using var json = JsonDocument.Parse(settings);
        var found = new List<string>();
        Walk(json.RootElement, "", "test.json", found);
        found.Should().NotBeEmpty();
    }

    [Theory]
    [InlineData("""{ "Demo": { "ApiKey": "", "Provider": "OpenAI", "Model": "gpt-4.1-mini", "Endpoint": "" } }""")]
    [InlineData("""{ "Logging": { "LogLevel": { "Default": "Information" } }, "MentorAgent": { "Language": "English" } }""")]
    public void Settings_without_a_key_pass(string settings)
    {
        using var json = JsonDocument.Parse(settings);
        var found = new List<string>();
        Walk(json.RootElement, "", "test.json", found);
        found.Should().BeEmpty();
    }

    private static void Walk(JsonElement element, string path, string file, List<string> found)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var name = path.Length == 0 ? property.Name : $"{path}:{property.Name}";
                    if (property.Value.ValueKind == JsonValueKind.String)
                        Check(name, property.Name, property.Value.GetString() ?? "", file, found);
                    else
                        Walk(property.Value, name, file, found);
                }
                break;

            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in element.EnumerateArray())
                    Walk(item, $"{path}:{i++}", file, found);
                break;
        }
    }

    private static void Check(string path, string name, string value, string file, List<string> found)
    {
        if (value.Length == 0) return;
        if (SecretName.IsMatch(name) || path.StartsWith("ConnectionStrings:", StringComparison.OrdinalIgnoreCase))
            found.Add($"{file}: {path} has a value");
        else if (SecretValue.IsMatch(value))
            found.Add($"{file}: {path} looks like a key");
    }
}
