using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace Novior.MentorAgent.Tests.Docs;

/// <summary>
/// Holds the code snippets in the READMEs to the real API.
///
/// <para>
/// C-NAME covers identifiers written in prose and tables. The snippets are the other half, and the
/// riskier one: a reader copies a fenced block verbatim, so an option that was renamed or removed
/// costs them a compile error at best and a silently ignored setting at worst.
/// </para>
///
/// <para>
/// This deliberately checks <em>API references</em> rather than compiling the blocks. Most snippets
/// are fragments — a few lines of an options lambda, a Razor element, a `builder.Services` call —
/// and wrapping 171 of them in synthetic contexts would test the wrappers as much as the docs. The
/// property and method names are where doc rot actually shows, and they can be checked exactly.
/// </para>
/// </summary>
public sealed class DocsSnippetApiTests(ITestOutputHelper output)
{
    /// <summary>Every option property across the four options types, by name.</summary>
    private static readonly HashSet<string> OptionNames = CollectOptionNames();

    [Fact]
    public void OptionSurfaceWasFound()
    {
        // Guard: an empty set would make the check below pass for everything.
        OptionNames.Should().Contain(["AppName", "ChatClient", "ScanAssemblies", "HubUrl"]);
        OptionNames.Count.Should().BeGreaterThan(100);
    }

    /// <summary>
    /// Members of <b>other</b> libraries' options that a snippet assigns through a lambda parameter
    /// also called <c>options</c> — not MentorAgent options, and not doc rot.
    /// </summary>
    private static readonly HashSet<string> ForeignOptionMembers =
    [
        "Events",   // JwtBearerOptions.Events — the server README's access_token forwarding for SignalR
    ];

    [Fact]
    public void EveryOptionAssignedInASnippetExists()
    {
        // `\r?\n` in every fence pattern of this class: a Windows checkout (core.autocrlf=true) has
        // CRLF READMEs, and `\n` alone matched no block at all — the checks here passed on nothing,
        // on every developer machine, until a README was saved with LF (23 September 2026).
        var failures = new List<string>();
        var blocks = 0;

        foreach (var (readme, path) in RepoFiles.Readmes)
        {
            var text = File.ReadAllText(path);

            foreach (Match block in Regex.Matches(text, @"```(?:csharp|razor)\r?\n(.*?)```", RegexOptions.Singleline))
            {
                blocks++;
                var code = block.Groups[1].Value;

                // `options.Foo = …`, `o.Foo = …`, `opt.Foo = …` — the shapes the docs use.
                // The (?![=>]) matters: without it `o.Status == "Shipped"` reads as an assignment
                // to a non-existent option, and `o.Foo => …` would too.
                foreach (Match m in Regex.Matches(code, @"\b(?:options|opt|o)\.([A-Z]\w+)\s*=(?![=>])"))
                {
                    var name = m.Groups[1].Value;
                    if (OptionNames.Contains(name) || ForeignOptionMembers.Contains(name)) continue;

                    failures.Add($"{readme}: options.{name} is assigned in a snippet but is not an option");
                }
            }
        }

        foreach (var f in failures.Distinct().Order()) output.WriteLine(f);

        blocks.Should().BeGreaterThan(100, "a check that found no snippet has checked nothing");
        string.Join(Environment.NewLine, failures.Distinct().Order()).Should().BeEmpty(
            "a reader copies these blocks verbatim, so every option they assign has to exist");
    }

    [Fact]
    public void EveryRazorComponentUsedInASnippetExists()
    {
        var components = Directory
            .EnumerateFiles(Path.Combine(RepoFiles.Root, RepoFiles.Abstractions, "Components"), "*.razor")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n is not null)
            .Select(n => n!)
            .ToHashSet(StringComparer.Ordinal);

        components.Should().NotBeEmpty();

        // Elements a snippet may legitimately use that are not MentorAgent components.
        string[] external =
        [
            "ChatWidget", "AuthorizeView", "Authorized", "NotAuthorized", "PageTitle", "HeadContent",
            "CardTemplate", "Router", "Found", "NotFound", "RouteView", "FocusOnNavigate", "Virtualize",
            "EditForm", "InputText", "ValidationSummary", "DataAnnotationsValidator", "OrderCard",
        ];

        var failures = new List<string>();

        foreach (var (readme, path) in RepoFiles.Readmes)
        {
            var text = File.ReadAllText(path);

            foreach (Match block in Regex.Matches(text, @"```razor\r?\n(.*?)```", RegexOptions.Singleline))
            {
                // (?<![\w>]) keeps generic type arguments out: `GetFromJsonAsync<MentorMetricsSnapshot>`
                // is a call, not a component, and only a markup `<` is preceded by whitespace.
                foreach (Match m in Regex.Matches(block.Groups[1].Value, @"(?<![\w>])<(Mentor\w+|[A-Z]\w*(?:Bar|Badge|Panel|Widget|View|Tour|Banner|Indicator|Bubble|Input|Dashboard))\b"))
                {
                    var name = m.Groups[1].Value;
                    if (components.Contains(name) || external.Contains(name)) continue;

                    failures.Add($"{readme}: <{name}> is used in a Razor snippet but no such component exists");
                }
            }
        }

        foreach (var f in failures.Distinct().Order()) output.WriteLine(f);

        string.Join(Environment.NewLine, failures.Distinct().Order()).Should().BeEmpty(
            "a component named in a snippet must be one the package ships");
    }

    [Fact]
    public void EveryComponentParameterUsedInASnippetExists()
    {
        // A wrong parameter name is a compile error for the reader, and the kind of thing that
        // survives a rename because nothing in this repository references the README.
        var failures = new List<string>();
        var componentDir = Path.Combine(RepoFiles.Root, RepoFiles.Abstractions, "Components");
        Directory.Exists(componentDir).Should().BeTrue("the components are read from {0}", componentDir);
        var checkedElements = 0;

        foreach (var (readme, path) in RepoFiles.Readmes)
        {
            var text = File.ReadAllText(path);

            foreach (Match block in Regex.Matches(text, @"```razor\r?\n(.*?)```", RegexOptions.Singleline))
            {
                foreach (Match el in Regex.Matches(block.Groups[1].Value, @"<(Mentor\w+)\s+([^>]*)>"))
                {
                    var component = el.Groups[1].Value;
                    var file = Path.Combine(componentDir, component + ".razor");
                    if (!File.Exists(file)) continue;   // not a component: the test above reports it
                    checkedElements++;

                    var declared = Regex
                        .Matches(File.ReadAllText(file), @"\[Parameter[^\]]*\][^;]*?\s(\w+)\s*\{\s*get")
                        .Select(m => m.Groups[1].Value)
                        .ToHashSet(StringComparer.Ordinal);

                    foreach (Match attr in Regex.Matches(el.Groups[2].Value, @"\b([A-Z]\w*)\s*="))
                    {
                        var name = attr.Groups[1].Value;
                        if (declared.Contains(name)) continue;

                        failures.Add($"{readme}: <{component} {name}=…> — {component} has no such parameter " +
                                     $"(declared: {string.Join(", ", declared.Order())})");
                    }
                }
            }
        }

        foreach (var f in failures.Distinct().Order()) output.WriteLine(f);

        // 13 elements with parameters on 9 October 2026, all MentorCardView and MentorDashboard.
        checkedElements.Should().BePositive("a check that found no component in a snippet has checked nothing");
        string.Join(Environment.NewLine, failures.Distinct().Order()).Should().BeEmpty(
            "a parameter shown in a snippet has to be one the component declares");
    }

    private static HashSet<string> CollectOptionNames()
    {
        Type[] optionTypes =
        [
            typeof(Novior.MentorAgent.Options.MentorOptions),
            typeof(Novior.MentorAgent.Abstractions.Models.MentorWidgetOptions),
            typeof(Novior.MentorAgent.Declarative.MentorDeclarativeOptions),
        ];

        var names = optionTypes
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        // Novior.MentorAgent.Blazor targets the browser and is not referenced by this test project, so
        // its options are read from the source rather than by reflection.
        var blazorOptions = Path.Combine(
            RepoFiles.Root, RepoFiles.Blazor, "Models", "MentorAgentBlazorOptions.cs");

        if (File.Exists(blazorOptions))
            foreach (Match m in Regex.Matches(File.ReadAllText(blazorOptions), @"public\s+[^\n=;]+?\s(\w+)\s*\{\s*get"))
                names.Add(m.Groups[1].Value);

        return names;
    }
}
