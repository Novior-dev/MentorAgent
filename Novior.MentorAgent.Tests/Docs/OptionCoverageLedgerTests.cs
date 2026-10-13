using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Novior.MentorAgent.Options;
using Xunit;
using Xunit.Abstractions;

namespace Novior.MentorAgent.Tests.Docs;

/// <summary>
/// The second axis of the release gate: every public option of the two option classes, with a
/// verdict on <b>where</b> it was exercised.
///
/// <para>
/// <c>claims.tsv</c> answers "does every documented claim have an owner in the suite?".
/// <c>release-matrix.md</c> answers "does every feature work in every app?". Neither answers the
/// question the user actually asked before 1.0.0 — <i>have all the options been exercised, and
/// where?</i> — because a feature can be verified live through the values the samples happen to
/// configure while a dozen options sit on their defaults the whole time.
/// </para>
///
/// <para>
/// This regenerates <c>Novior.MentorAgent.Tests/E2E/option-coverage.md</c> on every run, the way
/// <see cref="ClaimsInventoryTests"/> regenerates the claims ledger: the mechanical columns — the
/// option, its class, its default, which sample application sets it to a non-default value, which
/// test files name it — are recomputed, and the three human columns — <b>Verdict</b>, <b>Live
/// row</b>, <b>Note</b> — are carried over from the previous file by option name. Verdicts:
/// <list type="bullet">
///   <item><c>live</c> — a sample sets it and a numbered row of the release matrix exercises it.</item>
///   <item><c>probe</c> — no sample sets it; it was exercised once through a command-line override
///   (<c>--MentorAgent:Option=value</c>) and the probe is a matrix row.</item>
///   <item><c>in-process</c> — not observable from a running application; the note says why and
///   the owner column says which suite has it.</item>
/// </list>
/// </para>
///
/// <para>
/// The ledger is always written; the <b>assertion</b> that every option has a complete verdict runs
/// only when <c>MENTORAGENT_RELEASE_GATE</c> is set, so a day-to-day run stays green while the
/// verdicts are being earned and the release run cannot forget one. It also runs when the ledger is
/// already complete, so a completed ledger cannot silently regress.
/// </para>
/// </summary>
public sealed class OptionCoverageLedgerTests(ITestOutputHelper output)
{
    private static readonly string LedgerPath =
        Path.Combine(RepoFiles.Root, RepoFiles.Tests, "E2E", "option-coverage.md");

    private static readonly string[] Verdicts = ["live", "probe", "in-process"];

    /// <summary>The verdict of an option the ledger has not seen before, until someone gives it one.</summary>
    private const string Todo = "TODO";

    private sealed record Row(
        string Option, string Class, string Default, string Sample, string Owner,
        string Verdict, string LiveRow, string Note);

    [Fact]
    public void Every_public_option_has_a_verdict()
    {
        var previous = ReadPrevious();
        var rows = new List<Row>();

        foreach (var (type, instance) in Subjects())
        {
            var settable = type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.SetMethod is { IsPublic: true })
                .OrderBy(p => p.Name, StringComparer.Ordinal);

            foreach (var p in settable)
            {
                previous.TryGetValue($"{type.Name}.{p.Name}", out var prev);
                // A new option is written as TODO, not blank: the file is rewritten before the assertion, so a
                // blank row would make the next run read the ledger as never complete and stop gating (rc.14).
                rows.Add(new Row(
                    p.Name, type.Name, Describe(SafeGet(p, instance)),
                    SampleThatSets(p.Name), Owner(p.Name),
                    prev?.Verdict ?? Todo, prev?.LiveRow ?? "", prev?.Note ?? ""));
            }
        }

        Write(rows);

        var incomplete = rows
            .Where(r => !Verdicts.Contains(r.Verdict)
                     || (r.Verdict == "in-process" && r.Note.Length == 0)
                     || (r.Verdict != "in-process" && r.LiveRow.Length == 0))
            .Select(r => $"{r.Class}.{r.Option} [{(r.Verdict.Length == 0 ? "no verdict" : r.Verdict)}]")
            .ToList();

        output.WriteLine($"{rows.Count} public options; {rows.Count - incomplete.Count} with a complete verdict, {incomplete.Count} without.");
        foreach (var line in incomplete) output.WriteLine("  " + line);

        var gate = Environment.GetEnvironmentVariable("MENTORAGENT_RELEASE_GATE") is { Length: > 0 };
        var alreadyComplete = previous.Count > 0
            && previous.Values.All(r => Verdicts.Contains(r.Verdict) || r.Verdict == Todo);

        if (gate || alreadyComplete)
            string.Join(Environment.NewLine, incomplete).Should().BeEmpty(
                "every public option needs a verdict before a release, and a completed ledger must not regress");
    }

    // ── Subjects ──────────────────────────────────────────────────────────────────

    private static IEnumerable<(Type Type, object Instance)> Subjects()
    {
        yield return (typeof(MentorOptions), new MentorOptions { AppName = "ledger", ScanAssemblies = [] });

        // The Blazor client's options live in a package this project references but whose namespace
        // is not worth pinning here: resolve by name, and fail loudly if the type is ever renamed.
        var blazor = AppDomain.CurrentDomain.GetAssemblies()
            .Concat([Assembly.Load(RepoFiles.Blazor)])
            .SelectMany(a => { try { return a.GetTypes(); } catch { return []; } })
            .FirstOrDefault(t => t.Name == "MentorAgentBlazorOptions" && t.IsClass && t.IsPublic);
        blazor.Should().NotBeNull("MentorAgentBlazorOptions is the WASM client's public option surface");
        yield return (blazor!, Activator.CreateInstance(blazor!)!);
    }

    private static object? SafeGet(PropertyInfo p, object instance)
    {
        try { return p.GetValue(instance); }
        catch (Exception ex) { return $"(throws {ex.GetBaseException().GetType().Name})"; }
    }

    private static string Describe(object? value) => value switch
    {
        null => "null",
        string s => s.Length == 0 ? "\"\"" : $"\"{Shorten(s)}\"",
        bool b => b ? "true" : "false",
        Enum e => e.ToString(),
        Delegate => "(delegate)",
        System.Collections.ICollection c => $"{c.Count} item(s)",
        System.Collections.IEnumerable and not string => "(sequence)",
        _ when value.GetType().IsPrimitive || value is decimal || value is TimeSpan => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "",
        _ => value.GetType().Name,
    };

    // A default that spans lines (RagSystemPromptTemplate) would split its own table row and drop
    // the option from the ledger on the next read — the one thing the ledger must never do.
    private static string Shorten(string s)
    {
        var flat = s.Replace("\r", "", StringComparison.Ordinal).Replace("\n", "⏎", StringComparison.Ordinal);
        return flat.Length <= 40 ? flat : flat[..40] + "…";
    }

    // ── Mechanical columns ─────────────────────────────────────────────────────────

    /// <summary>
    /// The sibling sample repository, when this machine has it. A CI box without it gets a marker
    /// rather than an empty cell, so "no sample sets this" is never confused with "could not look".
    /// </summary>
    private static readonly string? SampleRoot = new[]
        {
            Path.GetFullPath(Path.Combine(RepoFiles.Root, "..", "MentorAgentSample")),
        }
        .FirstOrDefault(Directory.Exists);

    private static readonly IReadOnlyList<(string App, string Text)> SamplePrograms =
        SampleRoot is null
            ? []
            : Directory.EnumerateFiles(SampleRoot, "*.cs", SearchOption.AllDirectories)
                .Where(f => (f.EndsWith("Program.cs", StringComparison.OrdinalIgnoreCase)
                          || f.EndsWith("MauiProgram.cs", StringComparison.OrdinalIgnoreCase))
                         && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                         && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                .Select(f => (App: Path.GetRelativePath(SampleRoot, f).Split(Path.DirectorySeparatorChar)[0],
                              Text: File.ReadAllText(f)))
                .ToList();

    [Fact]
    public void A_release_run_reads_the_sample_repository()
    {
        // Without it every "Set by sample" cell reads "(sample repo not found)" and nothing fails.
        if (Environment.GetEnvironmentVariable("MENTORAGENT_RELEASE_GATE") is not { Length: > 0 }) return;

        SampleRoot.Should().NotBeNull("a release run fills the ledger from the samples beside the library");
        SamplePrograms.Should().NotBeEmpty("the sample repository has Program.cs files to read");
    }

    private static string SampleThatSets(string option)
    {
        if (SampleRoot is null) return "(sample repo not found)";
        var pattern = new Regex($@"options\.{Regex.Escape(option)}\s*(\|=|\+=|=)(?!=)");
        var apps = SamplePrograms.Where(p => pattern.IsMatch(p.Text)).Select(p => p.App).Distinct().Order().ToList();
        return apps.Count == 0 ? "—" : string.Join(", ", apps);
    }

    private static readonly IReadOnlyList<(string File, string Text)> TestSources =
        Directory.EnumerateFiles(Path.Combine(RepoFiles.Root, RepoFiles.Tests), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.EndsWith("OptionCoverageLedgerTests.cs", StringComparison.Ordinal))
            .Select(f => (File: Path.GetFileNameWithoutExtension(f), Text: File.ReadAllText(f)))
            .ToList();

    private static string Owner(string option)
    {
        var pattern = new Regex($@"\b{Regex.Escape(option)}\b");
        var files = TestSources.Where(t => pattern.IsMatch(t.Text)).Select(t => t.File).Distinct().Order().ToList();
        return files.Count switch
        {
            0 => "—",
            <= 3 => string.Join(", ", files),
            _ => string.Join(", ", files.Take(3)) + $" +{files.Count - 3}",
        };
    }

    // ── The file ──────────────────────────────────────────────────────────────────

    private static Dictionary<string, Row> ReadPrevious()
    {
        var result = new Dictionary<string, Row>(StringComparer.Ordinal);
        if (!File.Exists(LedgerPath)) return result;

        foreach (var line in File.ReadLines(LedgerPath))
        {
            if (!line.StartsWith("| ", StringComparison.Ordinal)) continue;
            var cells = line.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();
            if (cells.Length < 8 || cells[0] == "Option" || cells[0].StartsWith("---", StringComparison.Ordinal)) continue;
            var row = new Row(cells[0].Trim('`'), cells[1].Trim('`'), cells[2], cells[3], cells[4], cells[5], cells[6], cells[7]);
            result[$"{row.Class}.{row.Option}"] = row;
        }
        return result;
    }

    private static void Write(IReadOnlyList<Row> rows)
    {
        var sb = new StringBuilder();
        sb.Append("# Option coverage ledger\n\n");
        sb.Append("Regenerated by `OptionCoverageLedgerTests` on every run. The first five columns are computed; ");
        sb.Append("**Verdict**, **Live row** and **Note** are kept from this file by option name — edit them here.\n\n");
        sb.Append("Verdicts: `live` (a sample sets it, a `release-matrix.md` row exercises it) · ");
        sb.Append("`probe` (no sample sets it; exercised once through `--MentorAgent:Option=value`, and the probe is a matrix row) · ");
        sb.Append("`in-process` (not observable from a running application — the note says why, the owner says where).\n\n");
        sb.Append($"Set by sample: {(SampleRoot is null ? "sample repository not found on this machine" : $"scanned `{SamplePrograms.Count}` Program/MauiProgram files under `{Path.GetFileName(SampleRoot)}`")}.\n\n");
        sb.Append("| Option | Class | Default | Set by sample | In-process owner | Verdict | Live row | Note |\n");
        sb.Append("|---|---|---|---|---|---|---|---|\n");
        foreach (var r in rows.OrderBy(r => r.Class, StringComparer.Ordinal).ThenBy(r => r.Option, StringComparer.Ordinal))
            sb.Append($"| `{r.Option}` | `{r.Class}` | {Cell(r.Default)} | {Cell(r.Sample)} | {Cell(r.Owner)} | {Cell(r.Verdict)} | {Cell(r.LiveRow)} | {Cell(r.Note)} |\n");

        Directory.CreateDirectory(Path.GetDirectoryName(LedgerPath)!);
        File.WriteAllText(LedgerPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>A pipe inside a cell would split the row on the way back in.</summary>
    private static string Cell(string s) => s.Replace("|", "¦", StringComparison.Ordinal);
}
