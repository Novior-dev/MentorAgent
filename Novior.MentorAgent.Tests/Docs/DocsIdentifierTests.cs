using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace Novior.MentorAgent.Tests.Docs;

/// <summary>
/// Several options address a tool by its <em>name</em> rather than its method —
/// <c>RequiresApproval</c>, <c>OnToolResult</c>, <c>EvalChecks.ToolCalledCheck</c>, the metrics
/// <c>TopActions</c> list. There is no compile-time link between the two, so a name that is wrong
/// in the documentation produces a silent no-op in the reader's application: the gate never fires,
/// the transform never runs, the check never matches.
///
/// <para>
/// That makes a misspelled tool name in a README a functional defect, not a typo — which is why it
/// is pinned here rather than left to review.
/// </para>
/// </summary>
public sealed class DocsIdentifierTests(ITestOutputHelper output)
{
    /// <summary>Tool names the library actually registers, read from the source.</summary>
    private static readonly HashSet<string> RegisteredTools = ReadRegisteredTools();

    [Fact]
    public void RegisteredToolsWereFound()
    {
        // Guard: if extraction breaks, the test below would pass against an empty set.
        RegisteredTools.Should().Contain(["navigate_to", "remember", "forget_all", "load_skill"]);
    }

    [Fact]
    public void EveryDocumentedToolNameExists()
    {
        // BUG-003: three passages said `forget`; the tool is `forget_all`, so a gate written from
        // the docs left a memory-erasing tool ungated.
        // BUG-004: the skills walkthrough said `submit_expense_report_async`, but ToSnakeCase
        // strips a trailing "Async" — the real name has no suffix.
        var failures = new List<string>();

        foreach (var (name, path) in RepoFiles.Readmes)
        {
            var markdown = File.ReadAllText(path);

            foreach (var candidate in SnakeCaseSpans(markdown))
            {
                // Only judge names the docs present as tools; a snake_case span can also be a JSON
                // field, a CSS class or a file name.
                if (!LooksLikeAToolReference(markdown, candidate.Value)) continue;
                if (RegisteredTools.Contains(candidate.Value)) continue;
                if (IsIllustrative(candidate.Value)) continue;

                failures.Add($"{name}: `{candidate.Value}` is presented as a tool but is not registered");
            }
        }

        foreach (var f in failures.Distinct()) output.WriteLine(f);

        failures.Should().BeEmpty(
            "a tool name in the docs is an API: RequiresApproval and OnToolResult key on it. " +
            "Registered tools are [{0}]", string.Join(", ", RegisteredTools.Order()));
    }

    [Fact]
    public void DocumentedToolNamesObeyTheDocumentedNamingRule()
    {
        // The naming rule the core README states: PascalCase -> snake_case, trailing "Async"
        // dropped first. A documented tool name ending in _async contradicts it on its face.
        var offenders = RepoFiles.Readmes
            .SelectMany(kv => SnakeCaseSpans(File.ReadAllText(kv.Value))
                .Where(m => m.Value.EndsWith("_async", StringComparison.Ordinal))
                .Select(m => $"{kv.Key}: `{m.Value}`"))
            .Distinct()
            .ToList();

        offenders.Should().BeEmpty(
            "MentorUtils.ToSnakeCase strips a trailing \"Async\" before converting, so no real " +
            "tool is ever named *_async");
    }

    [Fact]
    public void EveryDocumentedCssVariableIsDefined()
    {
        var css = File.ReadAllText(Path.Combine(
            RepoFiles.Root, RepoFiles.Abstractions, "wwwroot", "css", "MentorAgent.css"));

        var defined = Regex.Matches(css, @"(--bm-[\w-]+)\s*:")
                           .Select(m => m.Groups[1].Value)
                           .ToHashSet(StringComparer.Ordinal);

        defined.Should().NotBeEmpty();

        var documented = RepoFiles.Readmes
            .SelectMany(kv => Regex.Matches(File.ReadAllText(kv.Value), @"`(--bm-[\w-]+)`")
                                   .Select(m => (Readme: kv.Key, Var: m.Groups[1].Value)))
            .Distinct()
            .ToList();

        documented.Should().NotBeEmpty("theming is documented by variable name");

        var phantom = documented.Where(d => !defined.Contains(d.Var)).ToList();

        phantom.Should().BeEmpty(
            "a CSS variable a reader is told to override must exist, or their stylesheet silently " +
            "does nothing");
    }

    /// <summary>Snake_case spans inside backticks, outside fenced code blocks.</summary>
    private static IEnumerable<Match> SnakeCaseSpans(string markdown)
    {
        var prose = Regex.Replace(markdown, "```.*?```", string.Empty, RegexOptions.Singleline);
        return Regex.Matches(prose, @"`([a-z][a-z0-9]*(?:_[a-z0-9]+)+)`")
                    .Select(m => Regex.Match(m.Groups[1].Value, @"^.*$"));
    }

    private static bool LooksLikeAToolReference(string markdown, string candidate)
    {
        // A tool reference sits near the vocabulary the docs use for tools. Anything else is more
        // likely a JSON field or a CSS class, and judging those here would produce noise.
        var idx = markdown.IndexOf($"`{candidate}`", StringComparison.Ordinal);
        if (idx < 0) return false;

        var window = markdown[Math.Max(0, idx - 260)..Math.Min(markdown.Length, idx + 260)];
        return window.Contains("tool", StringComparison.OrdinalIgnoreCase)
            || window.Contains("RequiresApproval", StringComparison.Ordinal)
            || window.Contains("MentorAction", StringComparison.Ordinal);
    }

    private static bool IsIllustrative(string candidate)
    {
        // Names invented for an example of the reader's own application, not tools MentorAgent
        // registers. They are derived in the docs from a sample C# method, so they cannot resolve
        // against this repository and are not defects.
        string[] examples =
        [
            "get_active_orders", "cancel_order", "issue_refund", "create_order", "get_orders",
            "get_order_status", "get_all_orders", "get_orders_by_customer", "show_order_cards",
            "get_weather", "submit_expense_report", "approve_expense_report",
            "get_policy_for_category", "expense_report", "business_analysis_team",
            "route_to_specialist", "invoke_ui_action",
        ];
        return examples.Contains(candidate);
    }

    private static HashSet<string> ReadRegisteredTools()
    {
        var tools = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in RepoFiles.SourceFiles.Where(f => f.EndsWith(".cs")))
        {
            var text = File.ReadAllText(file);

            // Built-in tools are created through AIFunctionFactory with an explicit name — this is
            // the authoritative registration, and it catches single-word names like `remember`
            // that a snake_case pattern would miss.
            foreach (Match m in Regex.Matches(text, @"name:\s*""([a-z][a-z0-9_]*)"""))
                tools.Add(m.Groups[1].Value);

            // Anything else spelled as a snake_case literal, so a tool registered another way
            // still counts as existing rather than producing a false accusation.
            foreach (Match m in Regex.Matches(text, @"""([a-z][a-z0-9]*(?:_[a-z0-9]+)+)"""))
                tools.Add(m.Groups[1].Value);
        }

        return tools;
    }
}
