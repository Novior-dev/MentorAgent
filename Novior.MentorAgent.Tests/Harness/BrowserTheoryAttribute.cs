using Novior.MentorAgent.Scanner.Cdp;
using Xunit;

namespace Novior.MentorAgent.Tests.Harness;

/// <summary>A theory that needs Edge or Chrome, skipped without one as <see cref="BrowserFactAttribute"/> (story 750).</summary>
internal sealed class BrowserTheoryAttribute : TheoryAttribute
{
    public BrowserTheoryAttribute()
    {
        if (CdpBrowser.Find(null) is null && !ReleaseGate.IsOn)
            Skip = "Not run: no Edge or Chrome installed.";
    }
}
