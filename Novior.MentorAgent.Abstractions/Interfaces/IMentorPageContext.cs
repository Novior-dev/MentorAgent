using System.Text.Json;

namespace Novior.MentorAgent.Abstractions.Interfaces;

/// <summary>
/// Scoped service that Blazor pages use to communicate with MentorAgent.
///
/// Two responsibilities:
/// 1. CONTEXT     — the page registers visible data → injected into the system prompt
/// 2. UI ACTIONS  — the page registers executable actions → become AI tools
///
/// Typical usage in a Razor page:
/// <code>
/// @inject IMentorPageContext PageContext
/// @implements IDisposable
///
/// protected override void OnInitialized()
/// {
///     PageContext
///         .SetPageName("Orders")
///         .Set("ActiveFilter", "Pending")
///         .RegisterUIAction("highlight_row", "Highlights the row", id => HighlightRow((int)id!));
/// }
///
/// public void Dispose() => PageContext.Clear("Orders");
/// </code>
/// </summary>
public interface IMentorPageContext
{
    // ── Context ───────────────────────────────────────────────────────────────

    /// <summary>Sets the current page name (e.g. "Orders", "Products").</summary>
    IMentorPageContext SetPageName(string pageName);

    /// <summary>Adds or updates a context entry.</summary>
    IMentorPageContext Set(string key, object? value);

    /// <summary>Removes a context entry.</summary>
    IMentorPageContext Remove(string key);

    /// <summary>
    /// Clears all context data and all UI actions, whichever page registered them. In a page's
    /// <c>Dispose()</c> call <see cref="Clear(string)"/> instead.
    /// </summary>
    void Clear();

    /// <summary>
    /// Clears what the page named <paramref name="pageName"/> registered. Call it in the page's <c>Dispose()</c>,
    /// with the name the page gave <see cref="SetPageName"/>.
    /// <para>
    /// Blazor disposes the previous page after it has initialized the next one. If the next page has set its own
    /// name, only the entries and actions registered under <paramref name="pageName"/> go, and the next page keeps
    /// its own, its name and its <see cref="SignalReady"/>. If <paramref name="pageName"/> is still the current
    /// page, everything is cleared, as with <see cref="Clear()"/> (BUG-192).
    /// </para>
    /// <para>
    /// Entries and actions belong to the page whose name was set when they were registered, so a page calls
    /// <see cref="SetPageName"/> before anything else. The default body, for an implementation that does not
    /// override it, clears everything when <paramref name="pageName"/> is the current page and nothing otherwise.
    /// </para>
    /// </summary>
    void Clear(string pageName)
    {
        if (string.Equals(PageName, pageName, StringComparison.Ordinal))
            Clear();
    }

    // -- UI Actions ------------------------------------------------------------

    /// <summary>Registers a UI action (untyped parameter).</summary>
    IMentorPageContext RegisterUIAction(
        string actionName,
        string description,
        Action<object?> handler);

    /// <summary>Registers a UI action with a strongly-typed parameter.</summary>
    IMentorPageContext RegisterUIAction<TParam>(
        string actionName,
        string description,
        Action<TParam> handler,
        string? parameterHint = null);

    /// <summary>Registers an async UI action (no typed parameter).</summary>
    IMentorPageContext RegisterUIActionAsync(
        string actionName,
        string description,
        Func<object?, Task> handler,
        string? parameterHint = null);

    /// <summary>Registers an async UI action with a strongly-typed parameter.</summary>
    IMentorPageContext RegisterUIActionAsync<TParam>(
        string actionName,
        string description,
        Func<TParam, Task> handler,
        string? parameterHint = null);

    /// <summary>Removes a registered UI action.</summary>
    IMentorPageContext UnregisterUIAction(string actionName);

    /// <summary>Signals that the page has finished rendering and its UI actions are ready.</summary>
    IMentorPageContext SignalReady();

    /// <summary>Waits until the page calls SignalReady or the timeout expires.</summary>
    Task<bool> WaitForPageReadyAsync(int timeoutMs = 2000, CancellationToken ct = default);

    // -- Read-only (used internally) -------------------------------------------

    /// <summary>The current page name.</summary>
    string? PageName { get; }

    /// <summary>Snapshot of the current context entries.</summary>
    IReadOnlyDictionary<string, object?> ContextData { get; }

    /// <summary>UI actions registered by the current page.</summary>
    IReadOnlyDictionary<string, UIActionRegistration> UIActions { get; }
}

/// <summary>
/// Registration of a UI action — name, LLM description and handler.
/// </summary>
public sealed record UIActionRegistration(
    string ActionName,
    string Description,
    Action<object?> Handler,
    Func<object?, Task>? HandlerAsync = null,
    string? ParameterHint = null
);
