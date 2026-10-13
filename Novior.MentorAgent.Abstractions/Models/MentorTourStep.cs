namespace Novior.MentorAgent.Abstractions.Models;

/// <summary>
/// One step of the AI-guided onboarding tour (roadmap #18).
/// </summary>
/// <param name="Title">Short heading, a few words.</param>
/// <param name="Body">One or two sentences explaining what this part of the app is for.</param>
/// <param name="Url">
/// Page the step is about, when there is one. The tour offers to navigate there; a step with no
/// URL is a general one (welcome, closing).
/// </param>
/// <param name="TryAsking">
/// A ready-made question the user can send to the assistant from this step. This is what makes the
/// tour worth generating rather than writing by hand: the hardest part of adopting an in-app
/// assistant is not knowing what it can be asked, and an example drawn from the app's own tools
/// answers that better than any generic hint.
/// </param>
public sealed record MentorTourStep(
    string Title,
    string Body,
    string? Url = null,
    string? TryAsking = null);
