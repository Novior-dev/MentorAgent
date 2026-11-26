using Novior.MentorAgent.Scanner.Cdp;
using Xunit;

namespace Novior.MentorAgent.Tests.Harness;

/// <summary>
/// Story 1636 — a test that drives the Edge or Chrome installed, as the scan of the pages does. On a machine without
/// one it is reported as skipped, with the reason; a release run (<see cref="ReleaseGate"/>) never skips it: there it
/// runs, and fails on the missing browser.
/// </summary>
internal sealed class BrowserFactAttribute : FactAttribute
{
    public BrowserFactAttribute()
    {
        if (CdpBrowser.Find(null) is null && !ReleaseGate.IsOn)
            Skip = "Not run: no Edge or Chrome installed.";
    }
}
