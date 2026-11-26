using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Novior.MentorAgent.Blazor.Hub;

namespace Novior.MentorAgent.Blazor.Services;

/// <summary>
/// Story 1638, slice C — runs in this browser the recipes the server asks for (<c>RecipeRequested</c>), with the runner
/// module of Novior.MentorAgent.Abstractions, and sends the answer back with <c>POST /mentor/recipe-result</c>: done,
/// or the step that could not be done and why, never a value of the page. It also tells the server which page is shown
/// (<see cref="PagePath"/>), sent with the page context before each message: the server offers the recipes of that page.
/// </summary>
internal sealed class WasmRecipeClient : IAsyncDisposable
{
    /// <summary>The module of the runner, a static asset of Novior.MentorAgent.Abstractions.</summary>
    internal const string Module = "./_content/Novior.MentorAgent.Abstractions/js/MentorRecipeRunner.js";

    private readonly MentorHubClient _hub;
    private readonly IJSRuntime _js;
    private readonly HttpClient _http;
    private readonly MentorAgentBlazorOptions? _options;
    private readonly NavigationManager? _navigation;
    private readonly ILogger<WasmRecipeClient> _logger;
    private readonly IDisposable _subscription;
    private IJSObjectReference? _module;

    public WasmRecipeClient(MentorHubClient hub, IJSRuntime js, HttpClient http, ILogger<WasmRecipeClient> logger,
        MentorAgentBlazorOptions? options = null, NavigationManager? navigation = null)
    {
        _hub = hub;
        _js = js;
        _http = http;
        _logger = logger;
        _options = options;
        _navigation = navigation;
        _subscription = hub.On<string, JsonElement>("RecipeRequested", (runId, steps) => _ = RunAsync(runId, steps));
    }

    /// <summary>The run of the last request, for the tests.</summary>
    internal Task? LastRun { get; private set; }

    /// <summary>The path of the page shown, without query or fragment: those can carry what the user typed.</summary>
    internal string? PagePath()
    {
        if (_navigation is null) return null;
        try
        {
            return "/" + _navigation.ToBaseRelativePath(_navigation.Uri).Split('?', '#')[0];
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private Task RunAsync(string runId, JsonElement steps) => LastRun = RunCoreAsync(runId, steps);

    private async Task RunCoreAsync(string runId, JsonElement steps)
    {
        bool done;
        int step;
        string? reason;
        try
        {
            _module ??= await _js.InvokeAsync<IJSObjectReference>("import", Module);
            var outcome = await _module.InvokeAsync<JsonElement>("run", steps);
            done = outcome.TryGetProperty("done", out var d) && d.ValueKind == JsonValueKind.True;
            step = outcome.TryGetProperty("step", out var s) && s.TryGetInt32(out var n) ? n : 0;
            reason = outcome.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
            if (!done && !IsReason(reason)) reason = "error";   // one of the runner's words, never a text of the page
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[MentorAgent.Blazor] recipe.hub: the recipe could not run in this browser: {Message}", ex.Message);
            (done, step, reason) = (false, 0, "error");
        }

        var connectionId = _hub.ConnectionId;
        if (connectionId is null) return;   // the connection is gone, and its turn with it
        var query = $"/mentor/recipe-result?connectionId={Uri.EscapeDataString(connectionId)}&runId={Uri.EscapeDataString(runId)}"
            + $"&done={(done ? "true" : "false")}&step={step}"
            + (done ? "" : $"&reason={Uri.EscapeDataString(reason ?? "missing")}");
        try
        {
            using var response = await MentorServerRequests.PostAsync(_http, _options, query);
            if (!response.IsSuccessStatusCode)
                _logger.LogWarning("[MentorAgent.Blazor] recipe.hub: the server answered {Status} to the recipe's result.", (int)response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[MentorAgent.Blazor] recipe.hub: the recipe's result did not reach the server: {Message}", ex.Message);
        }
    }

    /// <summary>A reason of the runner: a short word of lowercase letters and hyphens ("no-row").</summary>
    private static bool IsReason(string? reason)
        => reason is { Length: > 0 and <= 32 } && reason.All(c => c is (>= 'a' and <= 'z') or '-');

    public async ValueTask DisposeAsync()
    {
        _subscription.Dispose();
        if (_module is null) return;
        try { await _module.DisposeAsync(); }
        catch (JSDisconnectedException) { }
        catch (ObjectDisposedException) { }
    }
}
