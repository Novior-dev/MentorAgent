using Microsoft.JSInterop;

namespace Novior.MentorAgent.Core;

/// <summary>
/// Story 1638 — a recipe run in the user's browser by the scope's JavaScript runtime: the circuit's on Blazor Server, the
/// web view's on MAUI Hybrid. The model waits for the browser's answer. No runtime, or one that cannot reach a browser
/// (a hub, an API without a page), runs nothing: the recipe is not done, and stays on.
/// </summary>
internal sealed class MentorHostRecipeRunner(IServiceProvider services) : IMentorRecipeRunner, IAsyncDisposable
{
    /// <summary>The module of the runner, a static asset of Novior.MentorAgent.Abstractions.</summary>
    internal const string Module = "./_content/Novior.MentorAgent.Abstractions/js/MentorRecipeRunner.js";

    private IJSObjectReference? _module;

    public async Task<MentorRecipeOutcome> RunAsync(IReadOnlyList<MentorRecipeStepCall> steps, CancellationToken ct)
    {
        if (services.GetService(typeof(IJSRuntime)) is not IJSRuntime js)
            throw new InvalidOperationException("No browser to run the recipe in.");

        _module ??= await js.InvokeAsync<IJSObjectReference>("import", ct, Module).ConfigureAwait(false);
        return await _module.InvokeAsync<MentorRecipeOutcome>("run", ct, steps).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is null) return;
        try { await _module.DisposeAsync().ConfigureAwait(false); }
        catch (JSDisconnectedException) { }
        catch (ObjectDisposedException) { }
    }
}
