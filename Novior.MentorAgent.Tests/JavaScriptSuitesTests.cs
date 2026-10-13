using System.Diagnostics;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace Novior.MentorAgent.Tests;

/// <summary>
/// Runs every JavaScript suite under <c>Novior.MentorAgent.Tests/js</c> as part of <c>dotnet test</c>, so
/// a regression in <c>MentorAgent.js</c> fails the same gate as a regression in C#.
/// <para>
/// The logic under test — where a sentence ends, what is safe to strip before speaking, when the
/// microphone may be open — is pure and lives in JavaScript because that is where the Web Speech
/// API is. Porting it to C# to make it testable would mean two implementations that must agree,
/// which is the bug this is meant to catch. Node runs the real file instead.
/// </para>
/// <para>
/// The suites are discovered from the folder rather than listed here: a new <c>*.test.js</c> that
/// nobody remembered to register would pass by not running, which is the failure mode a test
/// runner exists to prevent.
/// </para>
/// </summary>
public sealed class JavaScriptSuitesTests(ITestOutputHelper output)
{
    public static TheoryData<string> Suites
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var file in Directory.EnumerateFiles(JsFolder(), "*.test.js").Order())
                data.Add(Path.GetFileName(file));
            return data;
        }
    }

    [Fact]
    public void Every_javascript_suite_is_discovered()
    {
        // Guards the theory below: an empty folder — a wrong path after a project move — would make
        // the whole JavaScript layer silently untested rather than failing.
        Suites.Count.Should().BeGreaterThanOrEqualTo(2,
            "speech.test.js and dom.test.js are both expected to be found and run");
    }

    [Theory]
    [MemberData(nameof(Suites))]
    public void JavaScript_suite_passes(string suite)
    {
        var script = Path.Combine(JsFolder(), suite);

        // This part runs everywhere: if a suite goes missing, that is a real failure regardless of
        // whether Node is installed.
        Assert.True(File.Exists(script), $"JS test suite not found at {script}");

        if (!TryRunNode(script, out var log, out var exitCode))
        {
            // xUnit 2.9 has no dynamic skip (that arrived in v3), so a machine without Node reports
            // a pass with the reason in the test output rather than failing a build for a missing
            // tool. Node is listed as a prerequisite in ROADMAP.md §9.
            output.WriteLine($"node is not on PATH — {suite} did NOT run.");
            return;
        }

        output.WriteLine(log);
        Assert.True(exitCode == 0, $"JavaScript suite {suite} failed:{Environment.NewLine}{log}");
    }

    /// <summary>
    /// Walks up from the test binaries to the project folder. The suites are deliberately not
    /// copied to the output directory: they must run against the real source file, not a stale copy.
    /// </summary>
    private static string JsFolder()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "js");
            if (Directory.Exists(candidate) && Directory.EnumerateFiles(candidate, "*.test.js").Any())
                return candidate;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        return Path.Combine(AppContext.BaseDirectory, "js");
    }

    private static bool TryRunNode(string script, out string log, out int exitCode)
    {
        log = string.Empty;
        exitCode = -1;
        try
        {
            using var p = Process.Start(new ProcessStartInfo("node", $"\"{script}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return false;

            log = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(60_000);
            exitCode = p.ExitCode;
            return true;
        }
        catch
        {
            return false;   // node missing
        }
    }
}
