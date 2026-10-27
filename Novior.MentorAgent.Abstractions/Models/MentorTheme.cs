namespace Novior.MentorAgent.Abstractions.Models;

/// <summary>Visual theme of the MentorAgent widget.</summary>
public enum MentorTheme
{
    /// <summary>
    /// Dark translucent glass with a blue accent: the baseline in <c>MentorAgent.css</c>, a navy panel at 55%
    /// opacity over a blur. On a light page it shows as dark grey with the page behind it; there, use
    /// <see cref="Minimal"/>.
    /// </summary>
    Default = 0,
    /// <summary>
    /// Dark and nearly opaque: a near-black panel at 85%, darker than <see cref="Default"/>. For apps that are
    /// already dark, where the default glass looks washed out.
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
