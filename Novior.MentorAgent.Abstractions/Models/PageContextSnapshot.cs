namespace Novior.MentorAgent.Abstractions.Models;

/// <summary>
/// Serializable snapshot of the current page context.
/// Sent by <c>WasmMentorPageContext</c> to <c>Novior.MentorAgent.Server</c>
/// before each AI message so the server has the client's page state.
/// </summary>
public sealed class PageContextSnapshot
{
    /// <summary>Current page name (e.g. "Orders", "Products").</summary>
    public string? PageName { get; set; }

    /// <summary>Key-value context data registered by the page.</summary>
    public Dictionary<string, string?> ContextData { get; set; } = [];

    /// <summary>UI actions registered by the page (name + description only — handlers stay on client).</summary>
    public List<UIActionInfo> UIActions { get; set; } = [];

    /// <summary>
    /// Story 1638 — the path of the page shown, without its query or fragment ("/orders/42"): the server offers the
    /// recipes of that page. Sent only by a client that can run them; internal until the recipes' format is public.
    /// </summary>
    [System.Text.Json.Serialization.JsonInclude]
    [System.Text.Json.Serialization.JsonPropertyName("path")]
    internal string? Path { get; set; }
}

/// <summary>
/// Describes a UI action registered by the client page.
/// Sent in <see cref="PageContextSnapshot"/> so the server hub can
/// register pseudo-handlers that invoke <c>UIActionRequested</c> callbacks.
/// </summary>
public sealed class UIActionInfo
{
    /// <summary>Action name in snake_case (e.g. "highlight_row").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Human-readable description for the AI.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Optional type hint shown to the AI (e.g. "integer: order ID").</summary>
    public string? ParameterHint { get; set; }
}
