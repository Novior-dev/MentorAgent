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
    private readonly Dictionary<string, object?> _data    = new();
    private readonly Dictionary<string, UIActionRegistration> _actions = new();
    private readonly Lock _lock = new();

    // â”€â”€ Context â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    public string? PageName => _pageName;

    public IReadOnlyDictionary<string, object?> ContextData
    {
        get { lock (_lock) return new Dictionary<string, object?>(_data); }
    }

    public IReadOnlyDictionary<string, UIActionRegistration> UIActions
    {
        get { lock (_lock) return new Dictionary<string, UIActionRegistration>(_actions); }
    }

    public IMentorPageContext SetPageName(string pageName)
    {
        lock (_lock) _pageName = pageName;
        return this;
    }

    public IMentorPageContext Set(string key, object? value)
    {
        lock (_lock) _data[key] = value;
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

    // â”€â”€ UI Actions â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    public IMentorPageContext RegisterUIAction(string actionName, string description, Action<object?> handler)
    {
        lock (_lock) _actions[actionName] = new UIActionRegistration(actionName, description, handler);
        return this;
    }

    public IMentorPageContext RegisterUIAction<TParam>(string actionName, string description, Action<TParam> handler, string? parameterHint = null)
    {
        lock (_lock) _actions[actionName] = new UIActionRegistration(
            actionName, description, p => handler(Deserialize<TParam>(p)),
            ParameterHint: parameterHint ?? MentorParameterHint.For(typeof(TParam)));
        return this;
    }

    public IMentorPageContext RegisterUIActionAsync(string actionName, string description, Func<object?, Task> handler, string? parameterHint = null)
    {
        lock (_lock) _actions[actionName] = new UIActionRegistration(
            actionName, description, p => { _ = handler(p); }, handler, parameterHint);
        return this;
    }

    public IMentorPageContext RegisterUIActionAsync<TParam>(string actionName, string description, Func<TParam, Task> handler, string? parameterHint = null)
    {
        lock (_lock) _actions[actionName] = new UIActionRegistration(
            actionName, description, p => { _ = handler(Deserialize<TParam>(p)); },
            p => handler(Deserialize<TParam>(p)),
            parameterHint ?? MentorParameterHint.For(typeof(TParam)));
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
                ContextData = _data.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString()),
                UIActions   = _actions.Values.Select(a => new UIActionInfo
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
}
