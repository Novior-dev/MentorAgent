namespace Novior.MentorAgent.Abstractions.Interfaces;

/// <summary>
/// Marker interface for Level 2 specialized agents.
/// A class decorated with [MentorAgent] is NOT required to implement this interface —
/// MentorAgent discovers it via the attribute and never reads the interface. Implementing it is
/// optional and validates nothing; it helps find and inject your agents in your own code and tests.
/// </summary>
public interface IMentorAgent
{
    // Marker interface — no members required.
}
