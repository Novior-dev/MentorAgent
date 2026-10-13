namespace Novior.MentorAgent.Attributes;

/// <summary>
/// Decorates a Blazor page or component to make it navigable by the Mentor AI.
///
/// MentorAgent scans assemblies for this attribute during discovery —
/// the page is registered without needing to be visited first.
///
/// <code>
/// // Simple page — navigation only
/// @attribute [MentorPage("/orders", "Orders")]
///
/// // Page with UI actions — waits for SignalReady after navigation
/// @attribute [MentorPage("/orders-interactive", "Interactive Orders",
///     HasUIActions  = true,
///     ReadyTimeout  = 3000,
///     Description   = "Order management with forms, filters and row highlighting")]
/// </code>
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class MentorPageAttribute : Attribute
{
    /// <summary>The page URL. E.g.: "/orders-interactive"</summary>
    public required string Url { get; init; }

    /// <summary>Human-readable page name. Used in the system prompt.</summary>
    public required string Name { get; init; }

    /// <summary>Optional description of the page's features.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// If true, the Mentor waits for the page to call
    /// <c>PageContext.SignalReady()</c> before executing UI actions.
    /// Default: false
    /// </summary>
    public bool HasUIActions { get; init; } = false;

    /// <summary>
    /// Timeout in milliseconds for WaitForPageReadyAsync.
    /// Used only when HasUIActions = true.
    /// Default: 2000ms
    /// </summary>
    public int ReadyTimeout { get; init; } = 2000;
}