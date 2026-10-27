using Novior.MentorAgent.Abstractions.Models;

namespace Novior.MentorAgent.Abstractions.Interfaces;

/// <summary>
/// Supplies the steps of the onboarding tour (roadmap #18).
/// <para>
/// The default implementation generates the tour from the application's own surface — the pages
/// registered with <c>[MentorPage]</c> and the tools the assistant actually has, those that need
/// no role — so it stays correct as the app changes, instead of being a hardcoded script that
/// quietly goes stale.
/// Generation happens at most once per process and language, and the result is cached: a tour is the
/// same for every user of a language, so paying for it per visitor would be waste.
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

    /// <summary>
    /// Returns the tour in <paramref name="language"/>, or in <c>MentorOptions.Language</c> when it is
    /// <see langword="null"/>. The generated tour is generated once per language. A tour written by hand
    /// that does not implement this method gets the call above, and so has one language for every page.
    /// </summary>
    Task<IReadOnlyList<MentorTourStep>> GetStepsAsync(MentorLanguage? language, CancellationToken ct = default)
        => GetStepsAsync(ct);
}
