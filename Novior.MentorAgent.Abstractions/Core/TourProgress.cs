namespace Novior.MentorAgent.Abstractions.Core;

/// <summary>
/// Where an onboarding tour should open (roadmap #18).
/// </summary>
/// <remarks>
/// Split out of <c>ChatWidget</c> so it can be tested without a rendering host. The arithmetic is
/// trivial but the edge cases are not, and every one of them is user-visible on someone's first
/// minute in the application: an off-by-one replays a step the user already acted on, and a saved
/// position left over from a longer tour points past the end of a shorter one after a deployment.
/// </remarks>
internal static class TourProgress
{
    /// <summary>
    /// Reads a stored position. Anything unparseable, negative or absent means "from the start" —
    /// a corrupt value in <c>localStorage</c> must not cost the user their tour.
    /// </summary>
    internal static int ParseSavedIndex(string? raw)
        => int.TryParse(raw, out var i) && i > 0 ? i : 0;

    /// <summary>
    /// True when the saved position is at or past the end, so there is nothing left to resume to.
    /// This is the shrinking-tour case: the steps come from the application's own pages and tools,
    /// so removing a page shortens the tour under a position that was already stored.
    /// </summary>
    internal static bool IsExhausted(int savedIndex, int stepCount)
        => stepCount <= 0 || savedIndex >= stepCount;

    /// <summary>
    /// The step to open on: the saved position, clamped into range. Callers that want "finished"
    /// to mean "do not show it at all" should check <see cref="IsExhausted"/> first — this method
    /// deliberately still returns a valid index, because the replay button has to show *something*.
    /// </summary>
    internal static int OpenAt(int savedIndex, int stepCount)
        => stepCount <= 0 ? 0 : Math.Clamp(savedIndex, 0, stepCount - 1);

    /// <summary>
    /// Where to resume after the user acts on a step — the one after it. Acting on a step consumes
    /// it: replaying the suggestion they just took would be a step backwards.
    /// </summary>
    internal static int ResumeAfter(int actedIndex) => actedIndex < 0 ? 0 : actedIndex + 1;
}
