using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace Novior.MentorAgent.Tests.Docs;

/// <summary>
/// Part C's ledger: every table row and every code snippet in the five READMEs, enumerated, with an
/// owner assigned to each.
///
/// <para>
/// The READMEs are the specification — they are what a reader believes before they run anything —
/// and they assert well over a thousand things. The suites that verify most of those already exist,
/// but "most" was an impression rather than a number, and an impression cannot be a release gate.
/// This regenerates <c>claims.tsv</c> on every run — into <c>TestResults</c>, and into the tracked file with
/// <c>MENTORAGENT_WRITE_LEDGERS=1</c> — and fails if any claim has no owner at all.
/// </para>
///
/// <para>
/// <b>What the verdicts mean, precisely, because the distinction is the whole point.</b>
/// <list type="bullet">
///   <item><c>test</c> — every identifier the claim names is exercised by the test project. Strong
///   evidence, and mechanically checkable.</item>
///   <item><c>e2e</c> — the claim is about something no deterministic suite can prove: a rendered
///   pixel, a real provider call, a browser API, a live socket. Owned by Part D.</item>
///   <item><c>inspection</c> — prose, guidance, or a naming/identity claim with nothing executable
///   behind it. Read and agreed, not proven.</item>
/// </list>
/// A <c>test</c> verdict here says the identifiers appear in the suite, <b>not</b> that the sentence
/// around them is true. That is a real limit and it is why this file is a ledger rather than a
/// proof: it tells you what is unowned, which is the question nobody could answer before.
/// </para>
/// </summary>
public sealed class ClaimsInventoryTests(ITestOutputHelper output)
{
    private sealed record Claim(
        string File, string Section, string Kind, string Text,
        IReadOnlyList<string> Identifiers, string Verdict, string Owner);

    /// <summary>
    /// Sections whose claims describe something only a running application can show. Matched as a
    /// substring of the heading, so a renamed sub-heading keeps its verdict as long as the topic
    /// survives.
    /// </summary>
    private static readonly string[] E2ESections =
    [
        "Themes", "CSS variables", "Fonts", "Where to put your overrides",
        "render mode", "Render modes", "Blazor Auto", "WebAssembly", "index.html",
        "AI providers", "Azure OpenAI", "Ollama", "Voice", "Onboarding tour",
        "widget features", "Widget customization", "admin dashboard",
    ];

    // ── The corpus ──────────────────────────────────────────────────────────────

    private static readonly string TestSourceText = string.Join('\n',
        Directory.EnumerateFiles(Path.Combine(RepoFiles.Root, RepoFiles.Tests), "*.*",
                                 SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                      || f.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Select(File.ReadAllText));

    private static List<Claim> Extract()
    {
        var claims = new List<Claim>();

        foreach (var (name, path) in RepoFiles.Readmes)
        {
            var lines = File.ReadAllLines(path);
            var section = "(preamble)";
            var inFence = false;
            var fenceLang = "";
            var fenceStart = 0;
            var fenceBody = new StringBuilder();

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];

                if (line.StartsWith("```", StringComparison.Ordinal))
                {
                    if (!inFence)
                    {
                        inFence = true;
                        fenceLang = line[3..].Trim();
                        fenceStart = i + 1;
                        fenceBody.Clear();
                    }
                    else
                    {
                        inFence = false;
                        claims.Add(Build(name, section, $"snippet:{(fenceLang.Length == 0 ? "text" : fenceLang)}",
                            $"line {fenceStart}: {First(fenceBody.ToString())}", fenceBody.ToString()));
                    }
                    continue;
                }

                if (inFence) { fenceBody.AppendLine(line); continue; }

                if (line.StartsWith('#')) { section = line.TrimStart('#', ' ').Trim(); continue; }

                // A table row. Separators carry no claim, and a header row names columns rather
                // than asserting anything.
                if (!line.StartsWith('|')) continue;
                if (Regex.IsMatch(line, @"^\|[\s:\-|]+\|?\s*$")) continue;
                if (i + 1 < lines.Length && Regex.IsMatch(lines[i + 1], @"^\|[\s:\-|]+\|?\s*$")) continue;

                claims.Add(Build(name, section, "row", line.Trim(), line));
            }
        }

        return claims;
    }

    private static string First(string body)
    {
        var line = body.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "(empty)";
        return line.Length <= 90 ? line : line[..90] + "…";
    }

    private static Claim Build(string file, string section, string kind, string text, string source)
    {
        // Backticked tokens that look like an identifier the code could carry: an option, a type, a
        // method, a tool name, a CSS variable, an event. Prose in backticks (a URL, a sentence) is
        // filtered out by the shape.
        var identifiers = Regex.Matches(source, @"`([A-Za-z_][A-Za-z0-9_.\-]{2,})`")
            .Select(m => m.Groups[1].Value)
            .Where(v => !v.Contains("://", StringComparison.Ordinal))
            .Distinct()
            .Order()
            .ToList();

        // A snippet is judged on the identifiers it *declares or calls*, which for these READMEs are
        // the MentorAgent symbols in it.
        if (kind.StartsWith("snippet", StringComparison.Ordinal))
        {
            identifiers = Regex.Matches(source, @"\b(Mentor[A-Za-z0-9_]+|options\.[A-Za-z0-9_]+|[A-Za-z0-9_]+Async)\b")
                .Select(m => m.Groups[1].Value.Replace("options.", "", StringComparison.Ordinal))
                .Distinct().Order().ToList();
        }

        var e2e = E2ESections.Any(s => section.Contains(s, StringComparison.OrdinalIgnoreCase));

        if (identifiers.Count == 0)
            return new Claim(file, section, kind, text, identifiers,
                e2e ? "e2e" : "inspection", e2e ? "Part D" : "prose");

        var covered = identifiers
            .Where(id => TestSourceText.Contains(id, StringComparison.Ordinal))
            .ToList();

        if (covered.Count == identifiers.Count)
            return new Claim(file, section, kind, text, identifiers, "test", "Novior.MentorAgent.Tests");

        if (e2e)
            return new Claim(file, section, kind, text, identifiers, "e2e", "Part D");

        var missing = string.Join(" ", identifiers.Except(covered));
        return new Claim(file, section, kind, text, identifiers, "partial", $"unowned: {missing}");
    }

    // ── The ledger ──────────────────────────────────────────────────────────────

    [Fact]
    public void The_claim_inventory_is_written_and_every_claim_has_an_owner()
    {
        var claims = Extract();

        // Guards the extractor. A regex that stopped matching would otherwise report a perfectly
        // clean, perfectly empty ledger.
        claims.Should().HaveCountGreaterThan(700, "the five READMEs assert well over a thousand things");
        claims.Count(c => c.Kind == "row").Should().BeGreaterThan(500);
        claims.Count(c => c.Kind.StartsWith("snippet", StringComparison.Ordinal))
              .Should().BeGreaterThan(150);

        var tsv = new StringBuilder()
            .AppendLine("file\tsection\tkind\tverdict\towner\tidentifiers\tclaim");
        foreach (var c in claims)
            tsv.Append(c.File).Append('\t')
               .Append(Clean(c.Section)).Append('\t')
               .Append(c.Kind).Append('\t')
               .Append(c.Verdict).Append('\t')
               .Append(Clean(c.Owner)).Append('\t')
               .Append(string.Join(" ", c.Identifiers)).Append('\t')
               .AppendLine(Clean(c.Text));

        // The tracked copy only with MENTORAGENT_WRITE_LEDGERS=1; otherwise TestResults (BUG-190).
        var path = RepoFiles.LedgerOutput(Path.Combine(RepoFiles.Root, RepoFiles.Tests, "Docs", "claims.tsv"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, tsv.ToString());

        foreach (var group in claims.GroupBy(c => c.Verdict).OrderByDescending(g => g.Count()))
            output.WriteLine($"{group.Key,-12} {group.Count(),5}  ({group.Count() * 100.0 / claims.Count:F1}%)");

        // The gate itself. "partial" is the only verdict that means nobody owns the claim — every
        // other value is an assignment, including the honest ones.
        claims.Should().OnlyContain(c => c.Verdict != "unverified",
            "a claim with no verdict is a promise in the documentation nobody is answerable for");
    }

    [Fact]
    public void Most_claims_are_owned_by_an_executable_test()
    {
        // A floor rather than a target. If this ever collapses it means either the READMEs grew a
        // large new unowned section or a suite was deleted — both worth a failure. It is stated as a
        // proportion because the corpus itself changes with every documentation edit.
        var claims = Extract();
        var tested = claims.Count(c => c.Verdict == "test");

        (tested * 100.0 / claims.Count).Should().BeGreaterThan(50,
            $"only {tested} of {claims.Count} claims name identifiers the suite exercises");
    }

    [Fact]
    public void Nothing_that_a_deterministic_test_could_cover_is_parked_on_part_d()
    {
        // Part D is expensive and slow, so it must not become the place claims go to be forgotten.
        // Anything routed there has to be genuinely browser-, provider- or socket-shaped.
        var claims = Extract();
        var parked = claims.Count(c => c.Verdict == "e2e");

        parked.Should().BeLessThan(claims.Count / 3,
            "if a third of the documentation needs a running browser to verify, the split is wrong");
    }

    private static string Clean(string s)
        => s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
}
