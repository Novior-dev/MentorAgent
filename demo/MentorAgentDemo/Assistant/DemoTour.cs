using MentorAgentDemo.Localization;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;

namespace MentorAgentDemo.Assistant;

/// <summary>
/// The tour the widget opens on a first visit, written by hand in the language of the pages. MentorAgent's own tour
/// is generated once for the whole application; the demo's pages switch language for each visitor, so this one is a
/// scoped service that reads the visitor's <see cref="DemoLanguage"/>. The widget resolves it in the visitor's
/// circuit, and no service of MentorAgent keeps it.
/// </summary>
public sealed class DemoTour(DemoLanguage language) : IMentorTour
{
    public Task<IReadOnlyList<MentorTourStep>> GetStepsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<MentorTourStep>>(
        [
            new(language["tour.welcome.title"], language["tour.welcome.body"]),
            new(language["tour.expenses.title"], language["tour.expenses.body"], "/expenses", language["home.q1"]),
            new(language["tour.approve.title"], language["tour.approve.body"], "/expenses", language["home.q3"]),
            new(language["tour.policy.title"], language["tour.policy.body"], "/policy", language["home.q5"]),
            new(language["tour.receipt.title"], language["tour.receipt.body"], "/expenses/new"),
            new(language["tour.try.title"], language["tour.try.body"], "/try"),
        ]);
}
