using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Novior.MentorAgent.Tests.Docs;

/// <summary>
/// The demo's texts and its What to try page, read from the files of the repository (the test project does not
/// reference the demo).
///
/// <para>
/// The page quotes lines of the demo's code for each feature. Code changes and a quotation does not follow it by
/// itself, so every quoted line must still be in the file the page names, in the same order. The texts of the pages
/// come from DemoText, which shows the key itself when one is missing: every key the demo uses must have both
/// languages.
/// </para>
/// </summary>
public sealed class DemoPagesTests
{
    private static readonly string App = Path.Combine(RepoFiles.Root, "demo", "MentorAgentDemo");

    private static readonly JsonElement Catalog =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(App, "Localization", "TryCatalog.json"))).RootElement;

    private static IEnumerable<string> SourceFiles()
        => Directory.EnumerateFiles(App, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".razor", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    // @page "/expenses" in a page of the demo.
    private static HashSet<string> PageRoutes()
        => [.. SourceFiles().SelectMany(f => Regex.Matches(File.ReadAllText(f), @"(?m)^@page ""(?<url>[^""]+)""")
                                                  .Select(m => m.Groups["url"].Value))];

    [Fact]
    public void Every_line_of_code_the_page_quotes_is_in_its_file_in_that_order()
    {
        var missing = new List<string>();
        foreach (var feature in Catalog.EnumerateArray())
        foreach (var block in feature.GetProperty("code").EnumerateArray())
        {
            var file = block.GetProperty("file").GetString()!;
            var path = Path.Combine(App, file.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) { missing.Add($"{feature.GetProperty("id")}: {file} does not exist"); continue; }

            var lines = File.ReadAllLines(path).Select(l => l.Trim()).ToList();
            var at = 0;
            foreach (var quoted in block.GetProperty("lines").EnumerateArray().Select(l => l.GetString()!.Trim()))
            {
                var found = lines.IndexOf(quoted, at);
                if (found < 0) missing.Add($"{feature.GetProperty("id")}: {file}: {quoted}");
                else at = found + 1;
            }
        }

        missing.Should().BeEmpty("the What to try page shows these lines as the demo's code");
    }

    [Fact]
    public void Every_feature_is_written_in_both_languages_and_links_only_pages_of_the_demo()
    {
        var pages = PageRoutes();
        foreach (var feature in Catalog.EnumerateArray())
        {
            var id = feature.GetProperty("id").GetString();
            foreach (var name in new[] { "title", "what", "ask" })
            {
                if (!feature.TryGetProperty(name, out var text)) { name.Should().Be("ask", $"{id} needs a {name}"); continue; }
                text.GetProperty("en").GetString().Should().NotBeNullOrWhiteSpace($"{id}.{name} in English");
                text.GetProperty("it").GetString().Should().NotBeNullOrWhiteSpace($"{id}.{name} in Italian");
            }
            feature.GetProperty("code").GetArrayLength().Should().BePositive($"{id} shows the code that turns it on");
            if (feature.TryGetProperty("page", out var page))
                pages.Should().Contain(page.GetString()!, $"{id} links a page of the demo");
        }
    }

    [Fact]
    public void The_page_reads_the_catalog_from_the_assembly()
    {
        var project = File.ReadAllText(Path.Combine(App, "MentorAgentDemo.csproj"));
        project.Should().Contain(@"<EmbeddedResource Include=""Localization\TryCatalog.json"" LogicalName=""MentorAgentDemo.TryCatalog.json"" />");
        File.ReadAllText(Path.Combine(App, "Localization", "TryCatalog.cs")).Should().Contain(@"""MentorAgentDemo.TryCatalog.json""");
    }

    [Fact]
    public void Every_text_key_the_demo_uses_has_both_languages()
    {
        var texts = File.ReadAllText(Path.Combine(App, "Localization", "DemoText.cs"));
        var defined = Regex.Matches(texts, @"\[""(?<key>[^""]+)""\] = \(""(?<en>(?:[^""\\]|\\.)*)"",\s*""(?<it>(?:[^""\\]|\\.)*)""\)")
            .ToDictionary(m => m.Groups["key"].Value, m => (En: m.Groups["en"].Value, It: m.Groups["it"].Value));
        defined.Should().NotBeEmpty();
        defined.Where(t => t.Value.En.Length == 0 || t.Value.It.Length == 0).Should().BeEmpty("every text has both languages");

        // L["home.title"], language["tour.welcome.title"]: an indexer with a literal dotted key.
        var used = SourceFiles()
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"\w\[""(?<key>[a-z]\w*(?:\.\w+)+)""\]")
                                  .Select(m => (File: Path.GetFileName(f), Key: m.Groups["key"].Value)))
            .ToList();
        used.Should().NotBeEmpty();
        used.Where(u => !defined.ContainsKey(u.Key)).Select(u => $"{u.File}: {u.Key}").Should().BeEmpty();
    }

    [Fact]
    public void The_tour_opens_only_pages_of_the_demo()
    {
        var tour = File.ReadAllText(Path.Combine(App, "Assistant", "DemoTour.cs"));
        var urls = Regex.Matches(tour, @"""(?<url>/[a-z0-9/-]*)""").Select(m => m.Groups["url"].Value).ToList();

        urls.Should().NotBeEmpty();
        PageRoutes().Should().Contain(urls);
    }
}
