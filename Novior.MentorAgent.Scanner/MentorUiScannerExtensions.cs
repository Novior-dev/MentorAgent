using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Novior.MentorAgent.Scanner;

/// <summary>Story 1636 — registers the scan of the pages.</summary>
public static class MentorUiScannerExtensions
{
    /// <summary>
    /// Registers the scan of the pages: once the application has started, in <c>Development</c> only, it opens each
    /// page of the application in the Edge or Chrome already installed and writes what the page offers to the project's
    /// <c>mentor-ui-manifest.json</c>, which the AutoMentor reads at the next build. Outside <c>Development</c> it does
    /// nothing. Reference the package in Debug only, and call this under <c>#if DEBUG</c>; it needs
    /// <c>AddMentorAgent()</c> with <c>AutoMentor.Enabled</c>.
    /// </summary>
    public static IServiceCollection AddMentorUiScanner(this IServiceCollection services,
        Action<MentorUiScannerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<MentorUiScannerOptions>().BindConfiguration(MentorUiScannerOptions.SectionName);
        if (configure is not null) services.Configure(configure);

        if (services.Any(d => d.ServiceType == typeof(MentorUiScanService))) return services;   // called twice: one scan
        services.AddSingleton<MentorUiScanGate>();
        // Slice B: the scan's user, first in the application's pipeline, in Development only (page.scan-user).
        services.AddTransient<IStartupFilter, MentorUiScanStartupFilter>();
        services.AddSingleton<MentorUiScanService>();
        services.AddHostedService(sp => sp.GetRequiredService<MentorUiScanService>());
        return services;
    }

    /// <summary>
    /// Starts the scan of the pages in a .NET MAUI Blazor Hybrid app on Windows, where no host starts it: call it on
    /// <c>MauiApp.Services</c> right after <c>builder.Build()</c>, under <c>#if DEBUG</c>, before the app's window is
    /// created. In a Debug build of the app only, it opens the DevTools port of the app's WebView2 on the loopback (or
    /// uses the one <c>WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS</c> already names, and the log says which), then reads each
    /// page in the app's window, as the app's own user, and returns to the page it found. The port stays open until the
    /// app closes. The scan clicks as the app's user: use it only with a build that points to development data. It needs
    /// <see cref="AddMentorUiScanner"/>; in a web host it does nothing, since the scan starts by itself.
    /// </summary>
    public static IServiceProvider StartMentorUiScan(this IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.GetRequiredService<MentorUiScanService>().StartWebView(System.Reflection.Assembly.GetEntryAssembly());
        return services;
    }
}
