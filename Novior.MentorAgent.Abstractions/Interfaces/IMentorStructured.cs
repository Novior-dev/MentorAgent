namespace Novior.MentorAgent.Abstractions.Interfaces;

/// <summary>
/// Typed / structured generation (roadmap #12). Asks the model for a strongly-typed result whose
/// JSON schema is derived from the requested type <c>T</c>, so you get back a deserialized object —
/// ideal for auto-filling forms, extracting entities, or building typed generative UI outside the
/// chat flow. Requires a configured <c>ChatClient</c> (Path A) whose provider supports structured
/// outputs (OpenAI / Azure OpenAI).
/// </summary>
public interface IMentorStructured
{
    /// <summary>
    /// Generates a typed <typeparamref name="T"/> from <paramref name="prompt"/> (with optional
    /// system <paramref name="instructions"/>). Returns <see langword="default"/> if the model
    /// output could not be parsed or no <c>ChatClient</c> is configured.
    /// </summary>
    Task<T?> GenerateAsync<T>(string prompt, string? instructions = null, CancellationToken ct = default);
}
