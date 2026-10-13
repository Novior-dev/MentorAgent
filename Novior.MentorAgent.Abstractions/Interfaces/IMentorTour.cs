using Novior.MentorAgent.Abstractions.Models;

namespace Novior.MentorAgent.Abstractions.Interfaces;

/// <summary>
/// Supplies the steps of the onboarding tour (roadmap #18).
/// <para>
/// The default implementation generates the tour from the application's own surface — the pages
/// registered with <c>[MentorPage]</c> and the tools the assistant actually has — so it stays
/// correct as the app changes, instead of being a hardcoded script that quietly goes stale.
/// Generation happens at most once per process and the result is cached: a tour is the same for
/// every user, so paying for it per visitor would be waste.
/// </para>
/// <para>
/// Register your own implementation before <c>AddMentorAgent()</c> to write the tour by hand.
/// </para>
/// </summary>
public interface IMentorTour
{
    /// <summary>
    /// Returns the tour steps, generating them on first call. Never returns <see langword="null"/>;
    /// an empty list means there was nothing to show a tour of.
    /// </summary>
    Task<IReadOnlyList<MentorTourStep>> GetStepsAsync(CancellationToken ct = default);
}
