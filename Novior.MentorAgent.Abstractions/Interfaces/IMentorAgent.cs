namespace Novior.MentorAgent.Abstractions.Interfaces;

/// <summary>
/// Marker interface for Level 2 specialized agents.
/// A class decorated with [MentorAgent] is NOT required to implement this interface —
/// MentorAgent discovers it via the attribute. Implementing it is optional but adds
/// compile-time validation and testability.
/// </summary>
public interface IMentorAgent
{
    // Marker interface — no members required.
}
