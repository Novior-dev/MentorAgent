namespace Novior.MentorAgent.Core;

/// <summary>
/// Lets the coordinator announce its configuration <b>once per process</b> instead of once per
/// session.
///
/// <para>
/// The build-time summary — routing, filtering, hosted tools, the domain scope, the approval list —
/// describes the application, not the visitor, and it is identical for every one of them. On Blazor
/// Server a coordinator is built per circuit, so a busy afternoon repeats those fifteen lines (one
/// of them carrying the ~900-character scope sentence) thousands of times, and the log stops being
/// readable exactly when someone needs to read it.
/// </para>
///
/// <para>
/// Singleton by registration, not a static field: each test host has its own, so a suite that
/// asserts on build warnings still sees them in its own container.
/// </para>
/// </summary>
internal sealed class CoordinatorAnnouncement
{
    private int _announced;
    private int _completed;

    /// <summary>True exactly once per process — for the first coordinator that asks.</summary>
    internal bool FirstBuild() => Interlocked.Exchange(ref _announced, 1) == 0;

    /// <summary>Called by the winner when its build is done, so the collaborators can go quiet too.</summary>
    internal void Completed() => Interlocked.Exchange(ref _completed, 1);

    /// <summary>
    /// True once a full build has been announced. Read by the builders the orchestrator calls into
    /// — skills, handoffs, the declarative sources — which have their own loggers and would
    /// otherwise keep announcing the application once per visitor.
    ///
    /// <para>
    /// It is deliberately <b>not</b> <c>_announced</c>: that flag is claimed at the top of the very
    /// first build, and those builders run <i>inside</i> it. Reading the claim would silence the
    /// one build that is supposed to speak.
    /// </para>
    /// </summary>
    internal bool Quiet => Volatile.Read(ref _completed) == 1;
}
