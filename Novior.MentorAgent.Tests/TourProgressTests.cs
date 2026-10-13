using FluentAssertions;
using Novior.MentorAgent.Abstractions.Core;
using Xunit;

namespace Novior.MentorAgent.Tests;

/// <summary>
/// Where the onboarding tour resumes (roadmap #18).
///
/// <para>
/// Originally every way out of the tour was treated as "done": tapping a step's ready-made question
/// or opening its screen marked the whole tour seen, so a user who engaged with step 2 lost steps 3
/// to 6 and never got them back automatically. Acting on a step is engagement, not dismissal — it
/// now suspends the tour at the next step instead. These tests pin the arithmetic and, more
/// importantly, the two states that are easy to conflate: <em>suspended part-way</em> and
/// <em>finished</em>.
/// </para>
/// </summary>
public sealed class TourProgressTests
{
    [Theory]
    [InlineData("2", 2)]
    [InlineData("0", 0)]
    [InlineData("-1", 0)]        // nonsense stored value must not push the tour off the start
    [InlineData("abc", 0)]
    [InlineData("", 0)]
    [InlineData(null, 0)]        // never stored — a first-time visitor
    public void ParseSavedIndex_NeverThrowsAndNeverGoesNegative(string? raw, int expected)
        => TourProgress.ParseSavedIndex(raw).Should().Be(expected);

    [Fact]
    public void ActingOnAStep_ResumesAtTheNextOne()
    {
        // The reported case: the user taps the question on step 2 (index 1) and comes back.
        // They want step 3, not the step they just acted on.
        TourProgress.ResumeAfter(1).Should().Be(2);
        TourProgress.OpenAt(2, 6).Should().Be(2);   // index 2 == "Passo 3 di 6"
    }

    [Fact]
    public void ActingOnTheLastStep_IsExhausted()
    {
        // Index 5 of 6 is the last one; resuming past it leaves nothing to show, and the tour must
        // read as finished rather than reopening clamped onto the final step forever.
        var resume = TourProgress.ResumeAfter(5);
        TourProgress.IsExhausted(resume, 6).Should().BeTrue();
    }

    [Fact]
    public void APositionPartWayThrough_IsNotExhausted()
        => TourProgress.IsExhausted(2, 6).Should().BeFalse();

    [Fact]
    public void APositionFromALongerTour_IsExhaustedNotClampedSilently()
    {
        // The steps are generated from the app's own pages, so removing a page shortens the tour
        // under a position that was already stored. Reopening on a step the user never reached —
        // and calling it "resume" — would be worse than starting over.
        TourProgress.IsExhausted(5, 3).Should().BeTrue();
    }

    [Fact]
    public void OpenAt_StaysInRangeForEveryStoredValue()
    {
        TourProgress.OpenAt(99, 6).Should().Be(5);
        TourProgress.OpenAt(-4, 6).Should().Be(0);
        TourProgress.OpenAt(0, 6).Should().Be(0);
    }

    [Fact]
    public void NoSteps_IsExhaustedAndOpensAtZero()
    {
        // An app with no [MentorPage] returns an empty tour. Nothing here may divide by, or index
        // into, that emptiness.
        TourProgress.IsExhausted(0, 0).Should().BeTrue();
        TourProgress.OpenAt(3, 0).Should().Be(0);
    }
}
