using Microsoft.AspNetCore.Components.Authorization;

namespace Novior.MentorAgent.Core;

/// <summary>
/// Story 1640 — what the widget observes of the pages, taken by the application itself: one per circuit on Blazor
/// Server, one for the web view on MAUI Hybrid. The user is the circuit's signed-in user, as a hash
/// (<see cref="MentorPageDirectory.ViewerOf"/>), or the circuit itself when nobody is signed in. A MAUI Hybrid app, known
/// by its web view's JavaScript runtime, has one user, on their own device: what they see needs no second user.
/// </summary>
internal sealed class MentorHostPageObserver(MentorPageDirectory directory, IOptions<MentorOptions> options, IServiceProvider services)
    : IMentorPageObserver
{
    private readonly long _circuit = MentorPageDirectory.NewViewer();
    private int _reports;

    /// <summary>A MAUI Hybrid app (<c>BlazorWebView</c>): its JavaScript runtime is the web view's.</summary>
    internal bool OneUser { get; } = services.GetService(typeof(Microsoft.JSInterop.IJSRuntime))?.GetType().Namespace
        ?.StartsWith("Microsoft.AspNetCore.Components.WebView", StringComparison.Ordinal) == true;

    public bool IsOn => options.Value.AutoMentor is { Enabled: true, ObservePages: true };

    public async Task<bool> ReportAsync(string? path, IReadOnlyList<PageObservedElement?>? elements, string? limit)
    {
        if (!IsOn) return false;
        var viewer = await ViewerAsync().ConfigureAwait(false);
        return directory.Observe(path, elements, viewer, Interlocked.Increment(ref _reports), limit, OneUser);
    }

    private async Task<long> ViewerAsync()
    {
        if (services.GetService(typeof(AuthenticationStateProvider)) is AuthenticationStateProvider authentication)
        {
            try
            {
                var state = await authentication.GetAuthenticationStateAsync().ConfigureAwait(false);
                if (MentorPageDirectory.ViewerOf(state.User) is { } user) return user;
            }
            catch (InvalidOperationException)
            {
                // No authentication state in this scope: the circuit is the user.
            }
        }
        return _circuit;
    }
}
