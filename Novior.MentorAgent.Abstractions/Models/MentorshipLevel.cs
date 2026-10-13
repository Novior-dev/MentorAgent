namespace Novior.MentorAgent.Abstractions.Models;

/// <summary>Proactivity and mentorship level of the Mentor AI.</summary>
public enum MentorshipLevel
{
    /// <summary>Executes actions without extra commentary. For expert users.</summary>
    Minimal = 0,
    /// <summary>Balanced — helpful responses with occasional suggestions.</summary>
    Standard = 1,
    /// <summary>Actively guides the user: explains, suggests next steps, warns of risks.</summary>
    Proactive = 2
}
