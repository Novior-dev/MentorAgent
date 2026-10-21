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
    /// <summary>
    /// No theme overrides: the widget renders like <see cref="Default"/>, from the baseline values in
    /// <c>MentorAgent.css</c>, and the host's own stylesheet changes what it overrides. <c>PrimaryColor</c>,
    /// if set, is still applied. Nothing has to be set by hand.
    /// </summary>
    Custom = 3
}
