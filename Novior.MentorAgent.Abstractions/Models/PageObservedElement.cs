namespace Novior.MentorAgent.Abstractions.Models;

/// <summary>
/// One element of a page the widget observed in the browser (story 1640, <c>page.observe</c>): sent by the widget to the
/// application, and by the WebAssembly client of <c>Novior.MentorAgent.Blazor</c> to the hub of
/// <c>Novior.MentorAgent.Server</c> (<c>ReportObservedPage</c>). Structure only: there is no field for a value. Its texts
/// come from the page: the application reads them as untrusted data.
/// </summary>
public sealed class PageObservedElement
{
    /// <summary>The element's tag, for example <c>button</c>.</summary>
    public string Element { get; set; } = string.Empty;

    /// <summary>Its ARIA role, when it has one.</summary>
    public string? Role { get; set; }

    /// <summary>Its label: none for an element of a structure that repeats, the column's header in a table.</summary>
    public string? Label { get; set; }

    /// <summary>The <c>type</c> of an input or a button.</summary>
    public string? Type { get; set; }

    /// <summary>The form, or group of fields, it belongs to.</summary>
    public string? Form { get; set; }

    /// <summary>Its selector: the element's own, or for a structure that repeats one that fits every element in its place.</summary>
    public string Selector { get; set; } = string.Empty;
}
