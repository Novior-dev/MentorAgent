using System.Reflection;
using System.Text.Json;


using Novior.MentorAgent.Abstractions.Core;

namespace Novior.MentorAgent.Core;

/// <summary>
/// Implementation of <see cref="IMentorPageContext"/>.
///
/// Scoped � one instance per circuit/user in Blazor Server,
/// one instance per tab in Blazor WASM.
///
/// Thread-safety: pages write on the Blazor UI thread,
/// AppContextProvider reads on the AI thread. We use a lock for safety.
/// </summary>
internal sealed class MentorPageContext : IMentorPageContext
{
    private readonly Lock _lock = new();

    private string? _pageName;
    private TaskCompletionSource<bool> _readyTcs = new();

    // Each entry remembers the page that registered it, so that Clear(pageName) removes only that page's (BUG-192).
    private readonly Dictionary<string, PageEntry<object?>> _data = new();
    private readonly Dictionary<string, PageEntry<UIActionRegistration>> _actions = new();

    // -- IMentorPageContext � Write (called by pages) -------------------------

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
        lock (_lock) ClearAll();
    }

    public void Clear(string pageName)
    {
        lock (_lock)
        {
            // Still the current page: nobody has taken the context over, so it all goes, the ready signal too.
            if (string.Equals(_pageName, pageName, StringComparison.Ordinal))
            {
                ClearAll();
                return;
            }

            // The next page has set its name: Blazor disposes the previous page after initializing the next one,
            // so its name, its entries and its SignalReady stay, and only the previous page's entries go.
            RemovePage(_data, pageName);
            RemovePage(_actions, pageName);
        }
    }

    private void ClearAll()
    {
        _pageName = null;
        _data.Clear();
        _actions.Clear();
        // Replace TCS inside the lock so that SignalReady() and
        // WaitForPageReadyAsync() always capture a consistent reference.
        _readyTcs = new TaskCompletionSource<bool>();
    }

    private static void RemovePage<T>(Dictionary<string, PageEntry<T>> entries, string pageName)
    {
        foreach (var (key, entry) in entries)
            if (string.Equals(entry.PageName, pageName, StringComparison.Ordinal))
                entries.Remove(key);
    }

    public IMentorPageContext RegisterUIAction(
        string actionName,
        string description,
        Action<object?> handler)
        => Add(new UIActionRegistration(actionName, description, handler));

    public IMentorPageContext RegisterUIAction<TParam>(
        string actionName,
        string description,
        Action<TParam> handler,
        string? parameterHint = null)
        => Add(new UIActionRegistration(
                actionName, description,
                Handler: _ => { },          // legacy slot unused — HandlerAsync takes priority
                HandlerAsync: p =>
                {
                    handler(DeserializeParam<TParam>(p));
                    return Task.CompletedTask;
                },
                ParameterHint: parameterHint ?? MentorParameterHint.For(typeof(TParam))));

    public IMentorPageContext RegisterUIActionAsync(
        string actionName,
        string description,
        Func<object?, Task> handler,
        string? parameterHint = null)
        => Add(new UIActionRegistration(
                actionName, description,
                Handler: _ => { },
                HandlerAsync: handler,
                ParameterHint: parameterHint));

    public IMentorPageContext RegisterUIActionAsync<TParam>(
        string actionName,
        string description,
        Func<TParam, Task> handler,
        string? parameterHint = null)
        => Add(new UIActionRegistration(
                actionName, description,
                Handler: _ => { },
                HandlerAsync: p => handler(DeserializeParam<TParam>(p)),
                ParameterHint: parameterHint ?? MentorParameterHint.For(typeof(TParam))));

    private MentorPageContext Add(UIActionRegistration registration)
    {
        lock (_lock) _actions[registration.ActionName] = new(_pageName, registration);
        return this;
    }

    public IMentorPageContext UnregisterUIAction(string actionName)
    {
        lock (_lock) _actions.Remove(actionName);
        return this;
    }

    // -- Helpers ---------------------------------------------------------------

    /// <summary>
    /// Deserializes a raw <c>object?</c> (typically a <see cref="JsonElement"/> coming
    /// from the AI tool call) to a strongly-typed value.
    ///
    /// Uses <c>PropertyNameCaseInsensitive = true</c> so that AI-generated camelCase JSON
    /// (e.g. <c>{"customerId":3}</c>) maps correctly to PascalCase .NET properties
    /// (e.g. <c>CustomerId</c>).
    /// </summary>
    private static readonly JsonSerializerOptions _deserializeOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static T DeserializeParam<T>(object? param)
    {
        if (param is JsonElement je)
        {
            return JsonSerializer.Deserialize<T>(je.GetRawText(), _deserializeOptions)
                   ?? throw new InvalidOperationException(
                       $"The AI passed a null JSON value; expected {typeof(T).Name}.");
        }
        if (param is T direct) return direct;
        if (param is null) return default!;
        // Fallback for boxed primitives (e.g. object boxing of long/double)
        return (T)Convert.ChangeType(param, typeof(T));
    }

    /// <summary>
    /// Takes the ready signal back. <c>navigate_to</c> calls it before it asks for another page, so that only that
    /// page's <see cref="SignalReady"/> ends its wait: the page being left signalled when it opened (BUG-194).
    /// </summary>
    internal void TakeReadySignalBack()
    {
        lock (_lock) _readyTcs = new TaskCompletionSource<bool>();
    }

    public IMentorPageContext SignalReady()
    {
        // Capture reference inside the lock so we signal the right TCS even
        // if Clear() replaces _readyTcs concurrently on another thread.
        TaskCompletionSource<bool> tcs;
        lock (_lock) { tcs = _readyTcs; }
        tcs.TrySetResult(true);
        return this;
    }

    public async Task<bool> WaitForPageReadyAsync(int timeoutMs = 2000, CancellationToken ct = default)
    {
        // Capture reference inside the lock so we await the right TCS even
        // if Clear() replaces _readyTcs between our read and the await.
        TaskCompletionSource<bool> tcs;
        lock (_lock) { tcs = _readyTcs; }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            return await tcs.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            return false; // timeout � the page did not call SignalReady
        }
    }

    // -- IMentorPageContext � Read (called by AppContextProvider / Orchestrator) -

    public string? PageName
    {
        get { lock (_lock) return _pageName; }
    }

    public IReadOnlyDictionary<string, object?> ContextData
    {
        get { lock (_lock) return _data.ToDictionary(e => e.Key, e => e.Value.Value); }
    }

    public IReadOnlyDictionary<string, UIActionRegistration> UIActions
    {
        get { lock (_lock) return _actions.ToDictionary(e => e.Key, e => e.Value.Value); }
    }

    /// <summary>An entry and the name of the page that registered it.</summary>
    private readonly record struct PageEntry<T>(string? PageName, T Value);
}