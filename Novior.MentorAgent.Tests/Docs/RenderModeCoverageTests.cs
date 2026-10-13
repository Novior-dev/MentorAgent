using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Novior.MentorAgent.Tests.Docs;

/// <summary>
/// Every render mode the core README marks ✅ has to have setup instructions (BUG-053).
///
/// <para>
/// The *Supported render modes* table is where a reader decides whether their application is one
/// this package works in. A ✅ there is a promise, and the sections underneath it are how the
/// promise is kept. **Blazor Hybrid (MAUI)** had the ✅ — with the note *"Fully supported with
/// `MentorAgent` alone"* — and no section at all, so the only instruction a MAUI developer had was
/// that there was nothing to do. Their first <c>dotnet build</c> then failed on a package
/// downgrade that never names MentorAgent.
/// </para>
///
/// <para>
/// This is a coverage test, not a correctness one: it cannot tell whether a section is right, only
/// that a supported mode is not left with a tick and silence.
/// </para>
/// </summary>
public sealed class RenderModeCoverageTests
{
    /// <summary>A `| **Name** | ✅ | … |` row from the supported-render-modes table.</summary>
    private static readonly Regex SupportedRow = new(
        @"^\|\s*\*\*(?<name>[^*]+)\*\*\s*\|\s*✅\s*\|", RegexOptions.Compiled);

    private static List<string> SupportedModes()
    {
        var readme = RepoFiles.Readme("core");
        var lines = readme.Split('\n');

        var start = Array.FindIndex(lines, l => l.Contains("### Supported render modes"));
        start.Should().BeGreaterThan(0, "the table this test reads has to exist to be read");

        var modes = new List<string>();
        for (var i = start; i < lines.Length; i++)
        {
            // The table ends at the horizontal rule that closes the section.
            if (i > start + 2 && lines[i].TrimStart().StartsWith("---") && !lines[i].Contains('|'))
                break;

            var m = SupportedRow.Match(lines[i].Trim());
            if (m.Success) modes.Add(m.Groups["name"].Value.Trim());
        }

        return modes;
    }

    [Fact]
    public void The_table_lists_the_render_modes_this_test_expects_to_check()
    {
        // A guard on the parser itself. If the regex stopped matching, every assertion below would
        // pass against an empty list — the failure mode this campaign has already paid for twice.
        var modes = SupportedModes();

        modes.Should().HaveCountGreaterThan(4);
        modes.Should().Contain(m => m.Contains("MAUI"));
    }

    [Fact]
    public void Every_supported_render_mode_has_a_setup_section()
    {
        var readme = RepoFiles.Readme("core");
        var headings = readme.Split('\n')
            .Where(l => l.StartsWith("### "))
            .Select(l => l[4..].Trim())
            .ToList();

        // A mode is covered when some heading names it. Matching on the distinctive word rather
        // than the whole row label, because a heading legitimately words things differently
        // ("Blazor Web App — Auto render mode (simplest WASM option)" for the Auto row).
        var missing = SupportedModes()
            .Where(mode => !headings.Any(h => Covers(h, mode)))
            .ToList();

        missing.Should().BeEmpty(
            "a ✅ in the supported-render-modes table is a promise, and a mode with a tick and no " +
            "section leaves the reader with no instructions at all (BUG-053). Uncovered: " +
            string.Join(", ", missing));
    }

    [Fact]
    public void The_MAUI_section_warns_about_the_package_downgrade_that_blocks_the_first_build()
    {
        var readme = RepoFiles.Readme("core");

        // The specific thing that stops a fresh MAUI app dead. NU1605 is an error, not a warning,
        // and its message names Microsoft.Extensions.Logging.Debug and nothing else — so a reader
        // searching for the cause has no path back to MentorAgent unless this is written down.
        readme.Should().Contain("NU1605",
            "the error code is what a blocked reader will search for");
        readme.Should().Contain("Microsoft.Extensions.Logging.Debug",
            "and the package the error names is the other half of that search");
    }

    private static bool Covers(string heading, string mode)
    {
        // Hybrid must be matched on "Hybrid", not on "MAUI". The *Non-Blazor frontends (React,
        // Vue, Angular, MAUI, mobile)* section mentions MAUI and is about something else entirely —
        // a headless client driving Novior.MentorAgent.Server over SignalR. Matching on the word "MAUI"
        // let that section satisfy the Blazor Hybrid row, and the test passed while the row it was
        // written for had no instructions at all.
        if (mode.Contains("Hybrid") || mode.Contains("MAUI")) return heading.Contains("Hybrid");
        if (mode.Contains("Standalone")) return heading.Contains("Standalone") || heading.Contains("WASM");
        if (mode.Contains("Auto")) return heading.Contains("Auto");
        if (mode.Contains("WebAssembly")) return heading.Contains("WASM") || heading.Contains("WebAssembly");
        if (mode.Contains("Server")) return heading.Contains("Blazor Server");
        if (mode.Contains("React")) return heading.Contains("Non-Blazor");
        return heading.Contains(mode, StringComparison.OrdinalIgnoreCase);
    }
}
