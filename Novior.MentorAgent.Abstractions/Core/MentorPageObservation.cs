using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Novior.MentorAgent.Abstractions.Models;

namespace Novior.MentorAgent.Abstractions.Core;

/// <summary>
/// Story 1640 — where the widget sends what it observed of the pages (<c>page.observe</c>): the application itself on
/// Blazor Server and MAUI Hybrid (<c>Novior.MentorAgent</c>), the hub on a WebAssembly client
/// (<c>Novior.MentorAgent.Blazor</c>). The widget starts the observation only when one is registered and on.
/// </summary>
internal interface IMentorPageObserver
{
    /// <summary>Whether the application observes its pages: <c>AutoMentor.Enabled</c> and <c>AutoMentor.ObservePages</c>.</summary>
    bool IsOn { get; }

    /// <summary>
    /// Takes the elements observed on the page at <paramref name="path"/>; <paramref name="limit"/> names the browser's
    /// limit when this is its last message. False: the browser stops observing.
    /// </summary>
    Task<bool> ReportAsync(string? path, IReadOnlyList<PageObservedElement?>? elements, string? limit);
}

/// <summary>
/// Story 1640 — the widget's end of the observation: <c>MentorPageObserver.js</c> calls <see cref="OnPageObserved"/>, and
/// a failure stops the observation, never the widget.
/// </summary>
internal sealed class MentorPageObservation(IMentorPageObserver observer, ILogger logger)
{
    /// <summary>The module of the observation, a static asset of this package.</summary>
    internal const string Module = "./_content/Novior.MentorAgent.Abstractions/js/MentorPageObserver.js";

    /// <summary>What the browser observed of the page at <paramref name="path"/>. False: the browser stops observing.</summary>
    [JSInvokable]
    public async Task<bool> OnPageObserved(string? path, List<PageObservedElement?>? elements, string? limit)
    {
        try
        {
            return await observer.ReportAsync(path, elements, limit).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogDebug("[MentorAgent] page.observe: the observed page could not be taken ({Type}); the observation stops.", ex.GetType().Name);
            return false;
        }
    }
}
