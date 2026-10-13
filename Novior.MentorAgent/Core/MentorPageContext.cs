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
    private readonly Dictionary<string, object?> _data = new();
    private readonly Dictionary<string, UIActionRegistration> _actions = new();

    // -- IMentorPageContext � Write (called by pages) -------------------------

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
        lock (_lock)
        {
            _pageName = null;
            _data.Clear();
            _actions.Clear();
            // Replace TCS inside the lock so that SignalReady() and
            // WaitForPageReadyAsync() always capture a consistent reference.
            _readyTcs = new TaskCompletionSource<bool>();
        }
    }

    public IMentorPageContext RegisterUIAction(
        string actionName,
        string description,
        Action<object?> handler)
    {
        lock (_lock)
            _actions[actionName] = new UIActionRegistration(actionName, description, handler);
        return this;
    }

    public IMentorPageContext RegisterUIAction<TParam>(
        string actionName,
        string description,
        Action<TParam> handler,
        string? parameterHint = null)
    {
        lock (_lock)
            _actions[actionName] = new UIActionRegistration(
                actionName, description,
                Handler: _ => { },          // legacy slot unused — HandlerAsync takes priority
                HandlerAsync: p =>
                {
                    handler(DeserializeParam<TParam>(p));
                    return Task.CompletedTask;
                },
                ParameterHint: parameterHint ?? MentorParameterHint.For(typeof(TParam)));
        return this;
    }

    public IMentorPageContext RegisterUIActionAsync(
        string actionName,
        string description,
        Func<object?, Task> handler,
        string? parameterHint = null)
    {
        lock (_lock)
            _actions[actionName] = new UIActionRegistration(
                actionName, description,
                Handler: _ => { },
                HandlerAsync: handler,
                ParameterHint: parameterHint);
        return this;
    }

    public IMentorPageContext RegisterUIActionAsync<TParam>(
        string actionName,
        string description,
        Func<TParam, Task> handler,
        string? parameterHint = null)
    {
        lock (_lock)
            _actions[actionName] = new UIActionRegistration(
                actionName, description,
                Handler: _ => { },
                HandlerAsync: p => handler(DeserializeParam<TParam>(p)),
                ParameterHint: parameterHint ?? MentorParameterHint.For(typeof(TParam)));
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
        get { lock (_lock) return new Dictionary<string, object?>(_data); }
    }

    public IReadOnlyDictionary<string, UIActionRegistration> UIActions
    {
        get { lock (_lock) return new Dictionary<string, UIActionRegistration>(_actions); }
    }
}