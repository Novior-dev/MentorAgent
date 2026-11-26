namespace Novior.MentorAgent.Abstractions.Models;

/// <summary>Visual theme of the MentorAgent widget.</summary>
public enum MentorTheme
{
    /// <summary>
    /// Dark and nearly opaque, with a blue accent: the baseline in <c>MentorAgent.css</c>, a navy panel at 96%
    /// opacity, so that its text keeps its contrast on any page (from 1.5.0; up to 1.4 it was glass at 55%, which
    /// <c>--bm-bg-panel</c> brings back). On a light page, <see cref="Minimal"/> matches it.
    /// </summary>
    Default = 0,
    /// <summary>
    /// Dark and nearly opaque: a near-black panel at 96%, darker than <see cref="Default"/>. For apps that are
    /// already dark.
    /// </summary>
    Dark = 1,
    /// <summary>Light: a white panel, thin borders and a soft shadow. The theme for a light page.</summary>
    Minimal = 2,
    /// <summary>
    /// No theme overrides: the widget renders like <see cref="Default"/>, from the baseline values in
    /// <c>MentorAgent.css</c>, and the host's own stylesheet changes what it overrides. <c>PrimaryColor</c>,
    /// if set, is still applied. Nothing has to be set by hand.
    /// </summary>
    Custom = 3,
    /// <summary>
    /// Follows the visitor's system setting (<c>prefers-color-scheme</c>): <see cref="Minimal"/> when it asks
    /// for light, which is also what a browser reports when the system states no preference, and
    /// <see cref="Default"/> when it asks for dark. It follows the system, not your page: on a page that is
    /// always light, a visitor whose system is dark sees the dark widget.
    /// </summary>
    Auto = 4
}
