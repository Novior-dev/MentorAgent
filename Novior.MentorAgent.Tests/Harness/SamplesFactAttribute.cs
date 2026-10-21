using Novior.MentorAgent.Tests.Docs;
using Xunit;

namespace Novior.MentorAgent.Tests.Harness;

/// <summary>
/// A test that reads the sample repository (<see cref="RepoFiles.SamplesRoot"/>). On a machine without it the test
/// is reported as skipped, with the reason, instead of passing without having run (BUG-189). A release run
/// (<see cref="ReleaseGate"/>) never skips it: there the test runs, and fails on the missing folder.
/// </summary>
/// <remarks>xUnit 2.9 cannot skip from inside a test; an attribute that sets <see cref="FactAttribute.Skip"/> can.</remarks>
internal sealed class SamplesFactAttribute : FactAttribute
{
    public SamplesFactAttribute()
    {
        if (RepoFiles.SamplesRoot is null && !ReleaseGate.IsOn)
            Skip = $"Not run: {RepoFiles.SamplesMissing}.";
    }
}
