using Xunit;

namespace Novior.MentorAgent.Tests.Harness;

/// <summary>
/// A theory that runs a JavaScript suite with Node. On a machine without <c>node</c> it is reported as skipped, with
/// the reason, instead of passing without having run (BUG-191). A release run (<see cref="ReleaseGate"/>) never
/// skips it: there the theory runs, and fails on the missing runtime.
/// </summary>
/// <remarks>xUnit 2.9 cannot skip from inside a test; an attribute that sets <see cref="FactAttribute.Skip"/> can.</remarks>
internal sealed class NodeTheoryAttribute : TheoryAttribute
{
    public NodeTheoryAttribute()
    {
        if (!NodeRuntime.IsAvailable && !ReleaseGate.IsOn)
            Skip = "Not run: node is not on PATH.";
    }
}
