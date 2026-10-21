using System.Diagnostics;

namespace Novior.MentorAgent.Tests.Harness;

/// <summary>Whether <c>node</c> can be started from this test run, asked once.</summary>
internal static class NodeRuntime
{
    private static readonly Lazy<bool> Available = new(() =>
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("node", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return false;

            p.StandardOutput.ReadToEnd();
            return p.WaitForExit(10_000) && p.ExitCode == 0;
        }
        catch
        {
            return false;   // node missing
        }
    });

    internal static bool IsAvailable => Available.Value;
}
