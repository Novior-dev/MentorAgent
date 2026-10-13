namespace Novior.MentorAgent.Abstractions.Models;

/// <summary>Visual theme of the MentorAgent widget.</summary>
public enum MentorTheme
{
    /// <summary>Light theme with blue accents.</summary>
    Default = 0,
    /// <summary>Dark theme.</summary>
    Dark = 1,
    /// <summary>Minimalist theme without shadows.</summary>
    Minimal = 2,
    /// <summary>No theme — all CSS variables must be set manually.</summary>
    Custom = 3
}
