using Xunit;

namespace Novior.MentorAgent.Tests.Harness;

/// <summary>
/// A test too slow for every run, for example one that builds a project with the .NET SDK. It runs only in the release
/// run (<see cref="ReleaseGate"/>); elsewhere it is reported as skipped, with the reason.
/// </summary>
internal sealed class ReleaseGateFactAttribute : FactAttribute
{
    public ReleaseGateFactAttribute(string reason)
    {
        if (!ReleaseGate.IsOn)
            Skip = $"Not run: {reason}. It runs with {ReleaseGate.Variable} set.";
    }
}
