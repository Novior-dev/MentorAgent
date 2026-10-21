namespace Novior.MentorAgent.Tests.Harness;

/// <summary>
/// The release run: <c>MENTORAGENT_RELEASE_GATE</c> set to any value. A test that depends on something a machine
/// may lack — the sample repository, Node — is skipped without it and fails with it, so a release never passes a
/// check that did not run.
/// </summary>
internal static class ReleaseGate
{
    internal const string Variable = "MENTORAGENT_RELEASE_GATE";

    internal static bool IsOn => Environment.GetEnvironmentVariable(Variable) is { Length: > 0 };
}
