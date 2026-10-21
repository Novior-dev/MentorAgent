using System.Text.Json;

namespace Novior.MentorAgent.Blazor.Services;

/// <summary>
/// WASM implementation of <see cref="IMentorPageContext"/>.
/// Maintains page context locally and provides <see cref="BuildSnapshot"/>
/// to serialize it for transmission to the server hub.
/// </summary>
public sealed class WasmMentorPageContext : IMentorPageContext
{
    private string? _pageName;
    // Each entry remembers the page that registered it, so that Clear(pageName) removes only that page's (BUG-192).
    private readonly Dictionary<string, PageEntry<object?>> _data    = new();
    private readonly Dictionary<string, PageEntry<UIActionRegistration>> _actions = new();
    private readonly Lock _lock = new();

    // â”€â”€ Context â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    public string? PageName => _pageName;

    public IReadOnlyDictionary<string, object?> ContextData
    {
        get { lock (_lock) return _data.ToDictionary(e => e.Key, e => e.Value.Value); }
    }

    public IReadOnlyDictionary<string, UIActionRegistration> UIActions
    {
        get { lock (_lock) return _actions.ToDictionary(e => e.Key, e => e.Value.Value); }
    }

    public IMentorPageContext SetPageName(string pageName)
    {
        lock (_lock) _pageName = pageName;
        return this;
    }

    public IMentorPageContext Set(string key, object? value)
    {
        lock (_lock) _data[key] = new(_pageName, value);
        return this;
    }

    public IMentorPageContext Remove(string key)
    {
        lock (_lock) _data.Remove(key);
        return this;
    }

    public void Clear()
    {
        lock (_lock) { _data.Clear(); _actions.Clear(); _pageName = null; }
    }

    public void Clear(string pageName)
    {
        lock (_lock)
        {
            // Still the current page: it all goes. Otherwise the next page has set its name (Blazor disposes the
            // previous page after initializing the next one), and only the previous page's entries go.
            if (string.Equals(_pageName, pageName, StringComparison.Ordinal))
            {
                _data.Clear(); _actions.Clear(); _pageName = null;
                return;
            }
            RemovePage(_data, pageName);
            RemovePage(_actions, pageName);
        }
    }

    private static void RemovePage<T>(Dictionary<string, PageEntry<T>> entries, string pageName)
    {
        foreach (var (key, entry) in entries)
            if (string.Equals(entry.PageName, pageName, StringComparison.Ordinal))
                entries.Remove(key);
    }

    // â”€â”€ UI Actions â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    public IMentorPageContext RegisterUIAction(string actionName, string description, Action<object?> handler)
        => Add(new UIActionRegistration(actionName, description, handler));

    public IMentorPageContext RegisterUIAction<TParam>(string actionName, string description, Action<TParam> handler, string? parameterHint = null)
        => Add(new UIActionRegistration(
            actionName, description, p => handler(Deserialize<TParam>(p)),
            ParameterHint: parameterHint ?? MentorParameterHint.For(typeof(TParam))));

    public IMentorPageContext RegisterUIActionAsync(string actionName, string description, Func<object?, Task> handler, string? parameterHint = null)
        => Add(new UIActionRegistration(
            actionName, description, p => { _ = handler(p); }, handler, parameterHint));

    public IMentorPageContext RegisterUIActionAsync<TParam>(string actionName, string description, Func<TParam, Task> handler, string? parameterHint = null)
        => Add(new UIActionRegistration(
            actionName, description, p => { _ = handler(Deserialize<TParam>(p)); },
            p => handler(Deserialize<TParam>(p)),
            parameterHint ?? MentorParameterHint.For(typeof(TParam))));

    private WasmMentorPageContext Add(UIActionRegistration registration)
    {
        lock (_lock) _actions[registration.ActionName] = new(_pageName, registration);
        return this;
    }

    public IMentorPageContext UnregisterUIAction(string actionName)
    {
        lock (_lock) _actions.Remove(actionName);
        return this;
    }

    public IMentorPageContext SignalReady() => this; // no-op in WASM

    public Task<bool> WaitForPageReadyAsync(int timeoutMs = 2000, CancellationToken ct = default)
        => Task.FromResult(true); // WASM: always ready

    // â”€â”€ Snapshot â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <summary>Builds a serializable snapshot of the current page context for the server.</summary>
    public PageContextSnapshot BuildSnapshot()
    {
        lock (_lock)
        {
            return new PageContextSnapshot
            {
                PageName    = _pageName,
                ContextData = _data.ToDictionary(kv => kv.Key, kv => kv.Value.Value?.ToString()),
                UIActions   = _actions.Values.Select(e => e.Value).Select(a => new UIActionInfo
                {
                    Name          = a.ActionName,
                    Description   = a.Description,
                    ParameterHint = a.ParameterHint
                }).ToList()
            };
        }
    }

    private static readonly JsonSerializerOptions _deserializeOptions = new() { PropertyNameCaseInsensitive = true };

    private static TParam Deserialize<TParam>(object? value)
    {
        if (value is TParam typed) return typed;
        if (value is null) return default!;
        var json = value is JsonElement el ? el.GetRawText() : JsonSerializer.Serialize(value);
        return JsonSerializer.Deserialize<TParam>(json, _deserializeOptions)!;
    }

    /// <summary>An entry and the name of the page that registered it.</summary>
    private readonly record struct PageEntry<T>(string? PageName, T Value);
}
