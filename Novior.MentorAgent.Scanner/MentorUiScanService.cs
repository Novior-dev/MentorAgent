using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Novior.MentorAgent.Abstractions.Core;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Scanner.Cdp;

namespace Novior.MentorAgent.Scanner;

/// <summary>
/// What a scan did: the manifest it wrote, how many pages and elements it has, the pages it could not read, how many
/// safe clicks it made, how many pages it opened and kept unchanged, the routes still waiting for parameter values,
/// whether it wrote the manifest, and the pages whose entry in the manifest changed, came or went (1637).
/// </summary>
internal sealed record MentorUiScanResult(string ManifestPath, int Pages, int Elements, int Excluded, IReadOnlyList<string> Failed, int Clicks,
    int Read, int Kept, IReadOnlyList<string> Waiting, bool Written, IReadOnlyList<string> Changed);

/// <summary>
/// Story 1636 — the scan of the pages (<c>page.scan</c>). Once the application has started, in <c>Development</c> only
/// (<c>page.scan-development</c>), it opens each page the host serves without route parameters in the browser already
/// installed (<c>page.scan-browser</c>), reads what the page offers with <c>MentorPageReader.js</c>, drops what MentorAgent
/// never lets out, and writes the manifest. It runs in the background and never stops the application: a page it cannot
/// read is left out, and the log says which. Slice B: it reads each page with no user, then as its own user with each
/// role of <c>Scanner:Roles</c> (<c>page.scan-user</c>), and clicks only where the page's structure says something
/// opens (<c>page.scan-clicks</c>). Slice D: in a MAUI app, where no host runs it, <see cref="StartWebView"/> reads the
/// pages in the app's own WebView2 (<c>page.scan-webview</c>).
/// </summary>
internal sealed class MentorUiScanService(
    IOptions<MentorOptions> mentorOptions,
    IOptions<MentorUiScannerOptions> options,
    MentorUiScanGate gate,
    ILogger<MentorUiScanService> logger,
    IHostEnvironment? environment = null,
    IHostApplicationLifetime? lifetime = null,
    IServer? server = null,
    MentorPageDirectory? directory = null) : BackgroundService, IAsyncDisposable
{
    internal const string WebViewArgumentsVariable = "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS";

    /// <summary>How long the scan waits for the app's WebView to show a page.</summary>
    internal static readonly TimeSpan WebViewWait = TimeSpan.FromMinutes(2);

    private readonly CancellationTokenSource _webView = new();

    internal const string ManifestPathMetadata = "Novior.MentorAgent.UiManifestPath";
    internal const string StatePathMetadata = "Novior.MentorAgent.UiScanStatePath";
    internal const string ManifestFileName = "mentor-ui-manifest.json";

    private readonly TaskCompletionSource<MentorUiScanResult?> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The scan's end: what it did, or <see langword="null"/> when it did not run.</summary>
    internal Task<MentorUiScanResult?> Completion => _done.Task;

    /// <summary>The process id of the browser this scan started, once it started.</summary>
    internal int? BrowserProcessId { get; private set; }

    /// <summary>
    /// Disposed without being stopped, the application still waits, 15 seconds at most, for the scan to close its browser
    /// and delete its profile.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await _webView.CancelAsync().ConfigureAwait(false);
        Dispose();   // cancels the scan
        if (ExecuteTask is not { } running) return;
        try
        {
            await running.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The job object, or the next scan, closes what is left.
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _done.TrySetResult(await RunAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _done.TrySetResult(null);
        }
        catch (Exception ex)
        {
            // The type only: a message may carry what a page said.
            logger.LogWarning("[MentorAgent] The scan of the pages stopped ({Type}); the manifest was not written.", ex.GetType().Name);
            logger.LogDebug(ex, "[MentorAgent] The scan of the pages stopped.");
            _done.TrySetResult(null);
        }
    }

    /// <summary>
    /// Slice D — starts the scan in the app's WebView2, for a MAUI app on Windows (<c>page.scan-webview</c>), in a Debug
    /// build of <paramref name="app"/> only. It sets the WebView's DevTools port, or uses the one
    /// <c>WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS</c> already names, so it must run before the app's window is created;
    /// then it waits for the WebView in the background. Whether it started.
    /// </summary>
    internal bool StartWebView(Assembly? app)
    {
        if (server is not null)
        {
            logger.LogWarning("[MentorAgent] StartMentorUiScan() is for a MAUI app: in a web host the scan of the pages starts by itself (page.scan-webview).");
            return false;
        }
        if (!IsDebugBuild(app))
        {
            logger.LogWarning("[MentorAgent] The scan of the pages runs in a Debug build of the app only; it does not start (page.scan-webview).");
            _done.TrySetResult(null);
            return false;
        }
        var (arguments, port) = WebViewArguments(Environment.GetEnvironmentVariable(WebViewArgumentsVariable), FreePort);
        if (port is not { } chosen)
        {
            logger.LogWarning("[MentorAgent] " + WebViewArgumentsVariable + " names a remote debugging port that is not a port; the scan of the pages does not start (page.scan-webview).");
            _done.TrySetResult(null);
            return false;
        }
        Environment.SetEnvironmentVariable(WebViewArgumentsVariable, arguments);
        logger.LogInformation("[MentorAgent] The scan of the pages will read the app's WebView through its DevTools port, 127.0.0.1:{Port} (page.scan-webview).", chosen);

        _ = Task.Run(async () =>
        {
            try
            {
                _done.TrySetResult(await ScanWebViewAsync(chosen, _webView.Token).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (_webView.IsCancellationRequested)
            {
                _done.TrySetResult(null);
            }
            catch (Exception ex)
            {
                logger.LogWarning("[MentorAgent] The scan of the pages stopped ({Type}); the manifest was not written.", ex.GetType().Name);
                logger.LogDebug(ex, "[MentorAgent] The scan of the pages stopped.");
                _done.TrySetResult(null);
            }
        });
        return true;
    }

    /// <summary>
    /// The value of <c>WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS</c> with a DevTools port, and the port: the one
    /// <paramref name="existing"/> already names, never a second one, or a free one added to what is there.
    /// <see langword="null"/> when <paramref name="existing"/> names a port that is not one.
    /// </summary>
    internal static (string Arguments, int? Port) WebViewArguments(string? existing, Func<int> freePort)
    {
        var named = Regex.Match(existing ?? "", @"--remote-debugging-port(?:=(?<port>\S*))?");
        if (named.Success)
            return (existing!, int.TryParse(named.Groups["port"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
                               && port is > 0 and <= 65535 ? port : null);
        var free = freePort();
        return (string.IsNullOrWhiteSpace(existing) ? $"--remote-debugging-port={free}" : $"{existing.TrimEnd()} --remote-debugging-port={free}", free);
    }

    /// <summary>Whether <paramref name="app"/> is a Debug build: compiled without the JIT optimizer.</summary>
    internal static bool IsDebugBuild(Assembly? app)
        => app?.GetCustomAttribute<DebuggableAttribute>()?.IsJITOptimizerDisabled == true;

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// Slice D — reads the pages in the WebView behind <paramref name="port"/>, as the app's own user: no user of the
    /// scan, so no roles. It returns to the page it found.
    /// </summary>
    internal async Task<MentorUiScanResult?> ScanWebViewAsync(int port, CancellationToken cancellationToken)
    {
        if (directory is null || !mentorOptions.Value.AutoMentor.Enabled)
        {
            logger.LogWarning("[MentorAgent] The scan of the pages needs AddMentorAgent() with AutoMentor.Enabled; it does not start (page.scan).");
            return null;
        }
        if (Roles().Count > 0)
            logger.LogWarning("[MentorAgent] In a WebView the scan reads the pages as the app's own user: MentorAgent:Scanner:Roles is not used (page.scan-webview).");

        return await ScanAsync(null, "the app's WebView", [], async ct =>
        {
            var browser = await CdpBrowser.AttachAsync(port, WebViewWait, ct).ConfigureAwait(false)
                          ?? throw new CdpException($"No WebView showed a page on 127.0.0.1:{port} in {WebViewWait.TotalMinutes:0} minutes.");
            logger.LogInformation("[MentorAgent] The scan found the page {Page} in the WebView on 127.0.0.1:{Port}; it returns there when it ends (page.scan-webview).",
                browser.Page, port);
            return browser;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Story 1637 — the scan from <c>novior-mentor scan</c> (<c>page.scan-tool</c>): the app runs at
    /// <paramref name="baseAddress"/>, anywhere, and the browser at <paramref name="browserPath"/> here.
    /// </summary>
    /// <remarks>
    /// Slice B: each of <paramref name="accounts"/> is a pass with its role, signed in with its cookies, set for the
    /// app's address only (<c>page.scan-ci</c>); with <paramref name="write"/> false nothing is written, and the result
    /// says what would change.
    /// </remarks>
    internal async Task<MentorUiScanResult?> ScanUrlAsync(Uri baseAddress, string browserPath,
        IReadOnlyList<(string Role, IReadOnlyList<(string Name, string Value)> Cookies)> accounts, bool write, CancellationToken cancellationToken)
    {
        if (directory is null || !mentorOptions.Value.AutoMentor.Enabled) return null;
        var cookies = accounts.ToDictionary(a => a.Role, a => a.Cookies, StringComparer.Ordinal);
        return await ScanAsync(baseAddress, Path.GetFileName(browserPath), [.. accounts.Select(a => a.Role)], async ct =>
        {
            ScanLeftovers.CleanUp(Path.GetTempPath());
            return await CdpBrowser.StartAsync(browserPath, ct).ConfigureAwait(false);
        }, cancellationToken,
        signIn: async (browser, site, roles, ct) =>
        {
            // One account a pass: the previous account's cookies go first.
            await browser.ClearCookiesAsync(ct).ConfigureAwait(false);
            return await browser.SetCookiesAsync(site, cookies[roles[0]], ct).ConfigureAwait(false);
        },
        write: write).ConfigureAwait(false);
    }

    private async Task<MentorUiScanResult?> RunAsync(CancellationToken cancellationToken)
    {
        if (environment is null || lifetime is null || server is null)
            return null;   // not a web host: a MAUI app starts the scan with StartMentorUiScan()
        if (!environment.IsDevelopment())
        {
            logger.LogWarning("[MentorAgent] The scan of the pages runs in Development only; in {Environment} it does nothing (page.scan-development).",
                environment.EnvironmentName);
            return null;
        }
        if (directory is null || !mentorOptions.Value.AutoMentor.Enabled)
        {
            logger.LogWarning("[MentorAgent] The scan of the pages needs AddMentorAgent() with AutoMentor.Enabled; it does not start (page.scan).");
            return null;
        }
        if (CdpBrowser.Find(options.Value.BrowserPath) is not { } browserPath)
        {
            logger.LogWarning(options.Value.BrowserPath is null
                ? "[MentorAgent] The scan of the pages found no Edge or Chrome installed, and downloads none; it does not start (page.scan-browser)."
                : "[MentorAgent] The scan of the pages found no browser at MentorAgent:Scanner:BrowserPath; it does not start (page.scan-browser).");
            return null;
        }

        await StartedAsync(cancellationToken).ConfigureAwait(false);
        if (options.Value.FrontEndUrl is { } frontEnd)
            return await ScanFrontEndAsync(frontEnd, browserPath, cancellationToken).ConfigureAwait(false);
        if (BaseAddress() is not { } baseAddress)
        {
            logger.LogWarning("[MentorAgent] The scan of the pages found no HTTP address of the application on this machine; it does not start (page.scan).");
            return null;
        }

        return await ScanAsync(baseAddress, Path.GetFileName(browserPath), Roles(), async ct =>
        {
            // A browser that an application which died left open, and its profile (page.scan-browser).
            var (closed, deleted) = ScanLeftovers.CleanUp(Path.GetTempPath());
            if (closed + deleted > 0)
                logger.LogInformation("[MentorAgent] The scan closed {Closed} browser(s) and deleted {Deleted} profile(s) left by an earlier scan whose application ended without stopping.",
                    closed, deleted);
            return await CdpBrowser.StartAsync(browserPath, ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Story 1641, slice B — the scan of a front end that this host does not serve (<c>page.scan-front-end</c>): its
    /// pages are those of <see cref="MentorUiScannerOptions.FrontEndRoutes"/>, or those its links reach from its first
    /// page, read with no user, each at every scan.
    /// </summary>
    private async Task<MentorUiScanResult?> ScanFrontEndAsync(Uri frontEnd, string browserPath, CancellationToken cancellationToken)
    {
        if (!frontEnd.IsAbsoluteUri || (frontEnd.Scheme != Uri.UriSchemeHttp && frontEnd.Scheme != Uri.UriSchemeHttps)
            || frontEnd.Query.Length > 0 || frontEnd.Fragment.Length > 0)
        {
            logger.LogWarning("[MentorAgent] MentorAgent:Scanner:FrontEndUrl is not an http or https address without a query or a fragment; the scan of the pages does not start (page.scan-front-end).");
            return null;
        }
        var site = new Uri(frontEnd.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/");
        if (Roles().Count > 0)
            logger.LogWarning("[MentorAgent] The scan reads a front end with no user, since its user signs in to this host only: MentorAgent:Scanner:Roles is not used (page.scan-front-end).");

        // The browser the links were followed in reads the pages too: the scan takes it, and closes it.
        CdpBrowser? started = null;
        async Task<CdpBrowser> OpenAsync(CancellationToken ct)
        {
            if (started is { } ready)
            {
                started = null;
                return ready;
            }
            ScanLeftovers.CleanUp(Path.GetTempPath());
            return await CdpBrowser.StartAsync(browserPath, ct).ConfigureAwait(false);
        }

        try
        {
            var pages = FrontEndRoutes();
            if (pages.Count == 0)
            {
                started = await OpenAsync(cancellationToken).ConfigureAwait(false);
                BrowserProcessId = started.ProcessId;
                pages = await LinksAsync(started, site, cancellationToken).ConfigureAwait(false);
                logger.LogInformation("[MentorAgent] The scan followed the links of the front end at {Address}: {Count} page(s) (page.scan-front-end).",
                    site, pages.Count);
                if (pages.Count == 0)
                {
                    logger.LogWarning("[MentorAgent] The scan found no page of the front end at {Address}; the manifest was not written (page.scan-front-end).", site);
                    return null;
                }
            }
            return await ScanAsync(site, Path.GetFileName(browserPath), [], OpenAsync, cancellationToken, frontEndPages: pages).ConfigureAwait(false);
        }
        finally
        {
            if (started is not null) await started.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The routes of <see cref="MentorUiScannerOptions.FrontEndRoutes"/> the scan can open, once each.</summary>
    private List<(string Route, string? Component)> FrontEndRoutes()
    {
        var routes = new List<(string, string?)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var left = 0;
        foreach (var given in options.Value.FrontEndRoutes ?? [])
        {
            if (MentorUiScanRoutes.FromFrontEnd(given) is not { } route) left++;
            else if (route.Contains("{*", StringComparison.Ordinal)) continue;   // a catch-all is no page to open
            else if (seen.Add(route) && routes.Count < MentorPageRoutes.MaxPages) routes.Add((route, null));
        }
        if (left > 0)
            logger.LogWarning("[MentorAgent] MentorAgent:Scanner:FrontEndRoutes has {Count} route(s) that are not of the shape a route has; they are left out (page.scan-front-end).", left);
        return routes;
    }

    /// <summary>The paths of the links of a page with its own origin, without query or fragment, none inside data-mentor-exclude.</summary>
    internal const string LinksScript = @"(() => {
          const paths = [];
          for (const a of document.querySelectorAll('a[href]')) {
            if (a.closest('[data-mentor-exclude]')) continue;
            let url;
            try { url = new URL(a.getAttribute('href'), location.href); } catch { continue; }
            if (url.origin === location.origin) paths.push(url.pathname);
          }
          return paths;
        })()";

    /// <summary>
    /// The pages of the front end at <paramref name="site"/> its links reach from its first page, breadth first, at most
    /// <see cref="MentorPageRoutes.MaxPages"/>: a page that sends the browser elsewhere is not one, its links are still
    /// followed.
    /// </summary>
    private async Task<List<(string Route, string? Component)>> LinksAsync(CdpBrowser browser, Uri site, CancellationToken cancellationToken)
    {
        var root = site.AbsolutePath;
        var queue = new Queue<string>(["/"]);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "/" };
        var pages = new List<(string, string?)>();
        while (queue.Count > 0 && pages.Count < MentorPageRoutes.MaxPages)
        {
            var route = queue.Dequeue();
            var address = new Uri(site, route.TrimStart('/'));
            try
            {
                await browser.OpenAsync(address, options.Value.PageTimeout, cancellationToken).ConfigureAwait(false);
                if (SamePage(await browser.LocationAsync(cancellationToken).ConfigureAwait(false), address)) pages.Add((route, null));
                var links = await browser.EvaluateAsync(LinksScript, cancellationToken).ConfigureAwait(false);
                if (links.ValueKind != JsonValueKind.Array) continue;
                foreach (var link in links.EnumerateArray())
                {
                    if (link.ValueKind != JsonValueKind.String || link.GetString() is not { } path
                        || !path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                    var next = "/" + path[root.Length..].TrimStart('/');
                    if (MentorPageRoutes.IsRoute(next) && !next.Contains('{') && seen.Count < MentorPageRoutes.MaxPages && seen.Add(next))
                        queue.Enqueue(next);
                }
            }
            catch (Exception ex) when (ex is CdpException or JsonException or InvalidOperationException)
            {
                logger.LogDebug("[MentorAgent] The scan could not follow the links of a page of the front end ({Type}).", ex.GetType().Name);
            }
        }
        return pages;
    }

    /// <summary>
    /// The scan itself, for a browser it starts (<paramref name="baseAddress"/> the application's) or one it attaches
    /// to (the address of the page found there): the pages to read, the passes, the reading and the clicks, the
    /// manifest and the state file. <paramref name="frontEndPages"/> are a front end's (story 1641); without them, the host's.
    /// </summary>
    private async Task<MentorUiScanResult?> ScanAsync(Uri? baseAddress, string browserName, IReadOnlyList<string> roles,
        Func<CancellationToken, Task<CdpBrowser>> open, CancellationToken cancellationToken,
        Func<CdpBrowser, Uri, IReadOnlyList<string>, CancellationToken, Task<bool>>? signIn = null, bool write = true,
        IReadOnlyList<(string Route, string? Component)>? frontEndPages = null)
    {
        // Slice C: a route with parameters is opened with the values of Scanner:RouteParameters, or stays to scan
        // (page.scan-parameters).
        var parameters = Parameters();
        var targets = new List<(string Route, string Path, string? Component)>();
        var waiting = new List<string>();
        var given = frontEndPages ?? [.. directory!.Pages().Where(p => p.Origin == MentorPageDirectory.PageOrigin.Host)
            .DistinctBy(p => p.Route, StringComparer.Ordinal).Select(p => (p.Route, p.Component))];
        foreach (var (route, component) in given)
        {
            if (MentorUiScanRoutes.Fill(route, parameters.GetValueOrDefault(MentorUiScanRoutes.Key(route))) is { } pagePath)
                targets.Add((route, pagePath, component));
            else
                waiting.Add(route);
        }
        if (waiting.Count > 0)
            logger.LogInformation("[MentorAgent] {Count} page(s) have a route parameter with no value in MentorAgent:Scanner:RouteParameters and stay to scan: {Routes} (page.scan-parameters).",
                waiting.Count, string.Join(", ", waiting.Take(20)));
        // A pass with no user, then one for each role of Scanner:Roles, a user with that role alone: an element seen
        // with no user needs no role, one seen only by some roles needs any one of them (roles.any-of).
        var passes = new List<IReadOnlyList<string>> { Array.Empty<string>() };
        passes.AddRange(roles.Select(r => (IReadOnlyList<string>)[r]));
        var path = ManifestPath();
        var statePath = StatePath(path);

        var reader = Reader();
        var clicker = Script("SafeClicks.js");
        // Slice C: only the pages whose fingerprint changed, or that the manifest does not have, are read again
        // (page.scan-changed).
        var components = ComponentTypes(targets.Select(t => t.Component));
        var fingerprints = targets.ToDictionary(t => t.Route,
            t => MentorUiScanState.Fingerprint(t.Component is null ? null : components.GetValueOrDefault(t.Component), t.Route, t.Path, roles,
                [reader, clicker]),
            StringComparer.Ordinal);
        var before = MentorUiScanState.Read(statePath);
        var (oldText, oldPages) = ReadManifest(path);
        var toRead = targets
            .Where(t => options.Value.Full || fingerprints[t.Route] is not { } print || !before.TryGetValue(t.Route, out var last)
                        || last != print || !oldPages.ContainsKey(t.Route))
            .ToList();
        logger.LogInformation("[MentorAgent] Scanning {Count} page(s) of {Address} with {Browser}, with no user{Roles}; {Kept} unchanged since the last scan.",
            toRead.Count, (object?)baseAddress ?? browserName, browserName, roles.Count == 0 ? "" : " and as " + string.Join(", ", roles),
            targets.Count - toRead.Count);

        var found = toRead.ToDictionary(t => t.Route, _ => new Found(), StringComparer.Ordinal);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var clicks = 0;
        try
        {
            // Nothing changed: no browser.
            await using var browser = toRead.Count == 0 ? null : await open(cancellationToken).ConfigureAwait(false);
            BrowserProcessId = browser?.ProcessId;
            var site = browser?.Page is { } attachedPage ? new Uri(attachedPage.GetLeftPart(UriPartial.Authority) + "/") : baseAddress!;
            if (browser?.ProcessId is not null && browser.Job is null && OperatingSystem.IsWindows())
                logger.LogWarning("[MentorAgent] Windows did not put the scan's browser in a job of its own: if the application ends without stopping, the browser and its DevTools port stay open until the next scan or until you close it (page.scan-browser).");
            foreach (var pass in browser is null ? [] : passes)
            {
                if (pass.Count > 0 && !await (signIn ?? SignInAsync)(browser!, site, pass, cancellationToken).ConfigureAwait(false))
                {
                    logger.LogWarning("[MentorAgent] The scan could not sign in its user with the role {Role}; that pass is skipped (page.scan-user).", pass[0]);
                    continue;
                }

                foreach (var (route, pagePath, _) in toRead)
                {
                    var page = found[route];
                    var address = new Uri(site, pagePath.TrimStart('/'));
                    try
                    {
                        await browser!.OpenAsync(address, options.Value.PageTimeout, cancellationToken).ConfigureAwait(false);
                        // Sent elsewhere, to sign in or to be refused: the page is not this user's to read.
                        if (!SamePage(await browser.LocationAsync(cancellationToken).ConfigureAwait(false), address)) continue;
                        page.Add(await browser.EvaluateAsync(reader, cancellationToken).ConfigureAwait(false), pass);

                        var safe = await browser.EvaluateAsync($"({clicker})(null)", cancellationToken).ConfigureAwait(false);
                        var count = safe.ValueKind == JsonValueKind.Number ? safe.GetInt32() : 0;
                        for (var i = 0; i < count; i++)
                        {
                            var clicked = await browser.EvaluateAsync($"({clicker})({i})", cancellationToken).ConfigureAwait(false);
                            if (clicked.ValueKind != JsonValueKind.True) continue;
                            clicks++;
                            await browser.SettleAsync(options.Value.PageTimeout, cancellationToken).ConfigureAwait(false);
                            // A click that leaves the page ends the clicks there: what follows is another page's.
                            if (!SamePage(await browser.LocationAsync(cancellationToken).ConfigureAwait(false), address)) break;
                            page.Add(await browser.EvaluateAsync(reader, cancellationToken).ConfigureAwait(false), pass);
                        }
                    }
                    catch (Exception ex) when (ex is CdpException or JsonException or InvalidOperationException or KeyNotFoundException
                                                   or FormatException)
                    {
                        errors[route] = ex.GetType().Name;
                        logger.LogDebug("[MentorAgent] The scan could not read the page {Route} ({Type}).", route, ex.GetType().Name);
                    }
                }
                gate.End();
            }
        }
        finally
        {
            // The scan's user lasts as long as the scan, however it ends (page.scan-user).
            gate.End();
        }

        var pages = new List<MentorUiManifest.Page>();
        var failed = new List<string>();
        var state = new Dictionary<string, string>(StringComparer.Ordinal);
        int elements = 0, excluded = 0;
        foreach (var (route, _, _) in targets)
        {
            if (!found.TryGetValue(route, out var page))
            {
                // Unchanged since the last scan: what the manifest says of it stays.
                var kept = oldPages[route];
                pages.Add(kept);
                elements += kept.Elements.Count;
                state[route] = fingerprints[route]!;
                continue;
            }
            if (page.ReadAt is not { } readAt)
            {
                failed.Add(route);
                logger.LogWarning("[MentorAgent] The scan could not read the page {Route} ({Type}); it is left out of the manifest.", route,
                    errors.GetValueOrDefault(route, "sent elsewhere"));
                continue;
            }
            var read = page.Elements();
            // Read again and found the same: the page keeps the time it was read, so the manifest does not change.
            if (oldPages.TryGetValue(route, out var old) && old.ScannedAt is { } then && Same(old.Elements, read)) readAt = then;
            pages.Add(new MentorUiManifest.Page(route, readAt, read));
            elements += read.Count;
            excluded += page.Excluded;
            if (fingerprints[route] is { } print) state[route] = print;
        }

        // Written only when what it says changes: the manifest is embedded at the next build, and a file written for
        // nothing would build again (dotnet watch).
        var text = MentorUiManifest.Serialize(pages);
        var changed = Changes(oldPages, pages);
        var different = !string.Equals(text, oldText, StringComparison.Ordinal);
        var written = different && write;
        if (written)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            await File.WriteAllTextAsync(path, text, cancellationToken).ConfigureAwait(false);
        }
        var stateText = MentorUiScanState.Serialize(state);
        if (write && !string.Equals(stateText, ReadText(statePath), StringComparison.Ordinal))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(statePath))!);
            await File.WriteAllTextAsync(statePath, stateText, cancellationToken).ConfigureAwait(false);
        }
        logger.LogInformation(written
                ? "[MentorAgent] The scan read {Read} page(s) and kept {Kept}: {Pages} page(s), {Elements} element(s), {Excluded} left out, with {Clicks} safe click(s); manifest written to {Path}. It is embedded at the next build."
                : different
                    ? "[MentorAgent] The scan read {Read} page(s) and kept {Kept}: {Pages} page(s), {Elements} element(s), {Excluded} left out, with {Clicks} safe click(s); the manifest at {Path} would change, and was not written."
                    : "[MentorAgent] The scan read {Read} page(s) and kept {Kept}: {Pages} page(s), {Elements} element(s), {Excluded} left out, with {Clicks} safe click(s); the manifest at {Path} has not changed.",
            toRead.Count, targets.Count - toRead.Count, pages.Count, elements, excluded, clicks, path);
        return new MentorUiScanResult(path, pages.Count, elements, excluded, failed, clicks, toRead.Count, targets.Count - toRead.Count, waiting,
            written, different && changed.Count == 0 ? ["(the file's form)"] : changed);
    }

    /// <summary>
    /// Signs the scan's user in with <paramref name="roles"/>: a one-time token, sent in a header to
    /// <c>/_mentor/scan/start</c>, never in the address, so no log keeps it.
    /// </summary>
    private async Task<bool> SignInAsync(CdpBrowser browser, Uri baseAddress, IReadOnlyList<string> roles, CancellationToken cancellationToken)
    {
        var sessions = gate.Sessions;
        var token = gate.Issue(roles);
        var answered = await browser.OpenWithHeaderAsync(new Uri(baseAddress, MentorUiScanGate.StartPath.TrimStart('/')),
            MentorUiScanGate.TokenHeader, token, cancellationToken).ConfigureAwait(false);
        return answered && gate.Sessions == sessions + 1;
    }

    /// <summary>The roles of <see cref="MentorUiScannerOptions.Roles"/> the manifest can hold; the others are named in the log.</summary>
    private List<string> Roles()
    {
        var roles = new List<string>();
        foreach (var given in options.Value.Roles ?? [])
        {
            var role = given?.Trim();
            if (string.IsNullOrEmpty(role)) continue;
            if (role.Length > MentorUiManifest.MaxTextLength || role.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format))
            {
                logger.LogWarning("[MentorAgent] MentorAgent:Scanner:Roles has a role the manifest cannot hold; it is left out.");
                continue;
            }
            if (!roles.Contains(role, StringComparer.Ordinal) && roles.Count < MentorUiManifest.MaxRoles) roles.Add(role);
        }
        return roles;
    }

    private static bool SamePage(string? location, Uri address)
        => location is not null && string.Equals(location.TrimEnd('/'), address.GetLeftPart(UriPartial.Query).TrimEnd('/'),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What the passes read of one page: each element once, by its selector, with no role when it was seen with no user,
    /// otherwise with the roles that saw it.
    /// </summary>
    private sealed class Found
    {
        private readonly List<string> _order = [];
        private readonly Dictionary<string, (MentorUiManifest.Element Element, bool Anyone, List<string> Roles)> _kept = new(StringComparer.Ordinal);
        private readonly HashSet<string> _left = new(StringComparer.Ordinal);

        internal DateTimeOffset? ReadAt { get; private set; }

        /// <summary>The elements left out, as MentorAgent never lets them out, that no other read kept.</summary>
        internal int Excluded => _left.Count(s => !_kept.ContainsKey(s));

        internal void Add(JsonElement read, IReadOnlyList<string> roles)
        {
            ReadAt ??= DateTimeOffset.UtcNow;
            foreach (var element in Read(read, [], out _, _left))
            {
                if (!_kept.TryGetValue(element.Selector, out var kept))
                {
                    _order.Add(element.Selector);
                    _kept[element.Selector] = (element, roles.Count == 0, [.. roles]);
                    continue;
                }
                if (roles.Count == 0)
                    _kept[element.Selector] = kept with { Anyone = true };
                else
                    kept.Roles.AddRange(roles.Where(r => !kept.Roles.Contains(r, StringComparer.Ordinal)));
            }
        }

        internal List<MentorUiManifest.Element> Elements()
            => [.. _order.Take(MentorUiManifest.MaxElementsPerPage).Select(s => _kept[s])
                .Select(k => k.Element with { Roles = k.Anyone ? [] : [.. k.Roles] })];
    }

    /// <summary>
    /// The elements <c>MentorPageReader.js</c> returned, as the manifest keeps them (<see cref="MentorUiManifest.FromScan"/>),
    /// within <see cref="MentorUiManifest.MaxElementsPerPage"/>; <paramref name="excluded"/> counts those MentorAgent
    /// never lets out, and <paramref name="left"/>, when given, gets their selectors.
    /// </summary>
    internal static List<MentorUiManifest.Element> Read(JsonElement found, IReadOnlyList<string> roles, out int excluded,
        ISet<string>? left = null)
    {
        excluded = 0;
        var elements = new List<MentorUiManifest.Element>();
        if (found.ValueKind != JsonValueKind.Array) return elements;

        foreach (var e in found.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            var element = MentorUiManifest.FromScan(
                Text(e, "element"), Text(e, "role"), Text(e, "label"), Text(e, "type"),
                e.TryGetProperty("options", out var o) && o.ValueKind == JsonValueKind.Array
                    ? o.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : null)
                    : null,
                Text(e, "form"), Text(e, "selector"), roles, Text(e, "autocomplete"),
                Flag(e, "hidden"), Flag(e, "disabled"), Flag(e, "excluded"), out var leftOut);
            if (leftOut) { excluded++; left?.Add(Text(e, "selector") ?? ""); continue; }
            if (element is null) continue;
            if (elements.Count >= MentorUiManifest.MaxElementsPerPage) break;
            elements.Add(element);
        }
        return elements;

        static string? Text(JsonElement e, string name)
            => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        static bool Flag(JsonElement e, string name)
            => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    }

    /// <summary>
    /// The manifest's path: <see cref="MentorUiScannerOptions.ManifestPath"/>, or the one the package's targets wrote in
    /// the application, or <c>mentor-ui-manifest.json</c> in the content root.
    /// </summary>
    private string ManifestPath()
        => options.Value.ManifestPath
           ?? Assembly.GetEntryAssembly()?.GetCustomAttributes<AssemblyMetadataAttribute>()
                  .FirstOrDefault(a => a.Key == ManifestPathMetadata)?.Value
           ?? Path.Combine(environment?.ContentRootPath ?? AppContext.BaseDirectory, ManifestFileName);

    /// <summary>
    /// Where the fingerprints of the last scan are kept: the project's <c>obj/</c>, as the package's targets wrote it in
    /// the application, or an <c>obj</c> folder next to the manifest.
    /// </summary>
    private static string StatePath(string manifestPath)
        => Assembly.GetEntryAssembly()?.GetCustomAttributes<AssemblyMetadataAttribute>()
               .FirstOrDefault(a => a.Key == StatePathMetadata)?.Value is { Length: > 0 } configured
            ? configured
            : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(manifestPath))!, "obj", MentorUiScanState.FileName);

    /// <summary>
    /// <see cref="MentorUiScannerOptions.RouteParameters"/> by <see cref="MentorUiScanRoutes.Key"/>, the names of the
    /// parameters without regard to case, as the router reads them.
    /// </summary>
    private Dictionary<string, IReadOnlyDictionary<string, string>> Parameters()
    {
        var parameters = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (route, values) in options.Value.RouteParameters ?? new Dictionary<string, IDictionary<string, string>>())
        {
            if (string.IsNullOrWhiteSpace(route) || values is null) continue;
            var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, value) in values) byName.TryAdd(name, value);
            parameters.TryAdd(MentorUiScanRoutes.Key(route), byName);
        }
        return parameters;
    }

    /// <summary>The components that serve the pages, by full name, from the assemblies the pages are read from.</summary>
    private Dictionary<string, Type> ComponentTypes(IEnumerable<string?> names)
    {
        var assemblies = MentorPageRoutes.Assemblies(Assembly.GetEntryAssembly(), mentorOptions.Value.AutoMentor.PageAssemblies, []);
        var types = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var name in names.OfType<string>().Distinct(StringComparer.Ordinal))
        {
            foreach (var assembly in assemblies)
            {
                Type? type;
                try
                {
                    type = assembly.GetType(name, throwOnError: false);
                }
                catch (Exception ex) when (ex is FileLoadException or FileNotFoundException or BadImageFormatException or TypeLoadException)
                {
                    continue;
                }
                if (type is null) continue;
                types[name] = type;
                break;
            }
        }
        return types;
    }

    /// <summary>The manifest as it is now, its text and its pages; nothing when there is none or it is not one.</summary>
    private static (string? Text, Dictionary<string, MentorUiManifest.Page> Pages) ReadManifest(string path)
    {
        var pages = new Dictionary<string, MentorUiManifest.Page>(StringComparer.Ordinal);
        if (ReadText(path) is not { } text) return (null, pages);
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text));
        var manifest = MentorUiManifest.Parse(stream);
        if (manifest.Problem is null)
            foreach (var page in manifest.Pages) pages.TryAdd(page.Route, page);
        return (text, pages);
    }

    private static string? ReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The routes whose page the manifest gained, lost, or holds with other elements.</summary>
    private static List<string> Changes(Dictionary<string, MentorUiManifest.Page> before, IReadOnlyList<MentorUiManifest.Page> now)
    {
        var changes = new List<string>();
        foreach (var page in now)
            if (!before.TryGetValue(page.Route, out var old)) changes.Add("+ " + page.Route);
            else if (!Same(old.Elements, page.Elements)) changes.Add("~ " + page.Route);
        var routes = now.Select(p => p.Route).ToHashSet(StringComparer.Ordinal);
        changes.AddRange(before.Keys.Where(r => !routes.Contains(r)).Order(StringComparer.Ordinal).Select(r => "- " + r));
        return changes;
    }

    private static bool Same(IReadOnlyList<MentorUiManifest.Element> before, IReadOnlyList<MentorUiManifest.Element> now)
        => MentorUiManifest.Serialize([new MentorUiManifest.Page("/", null, before)])
           == MentorUiManifest.Serialize([new MentorUiManifest.Page("/", null, now)]);

    /// <summary>The application's own HTTP address on this machine: http first, a wildcard read as the loopback.</summary>
    private Uri? BaseAddress()
    {
        var addresses = server!.Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
        var local = new List<Uri>();
        foreach (var address in addresses)
        {
            if (!Uri.TryCreate(address.Replace("://+", "://127.0.0.1").Replace("://*", "://127.0.0.1").Replace("://0.0.0.0", "://127.0.0.1")
                    .Replace("://[::]", "://127.0.0.1"), UriKind.Absolute, out var uri))
                continue;
            if (uri.IsLoopback) local.Add(new Uri(uri.GetLeftPart(UriPartial.Authority) + "/"));
        }
        return local.FirstOrDefault(u => u.Scheme == Uri.UriSchemeHttp) ?? local.FirstOrDefault();
    }

    private async Task StartedAsync(CancellationToken cancellationToken)
    {
        if (lifetime!.ApplicationStarted.IsCancellationRequested) return;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        await started.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Story 1640 — the reader of the pages, the widget's module <c>MentorPageReader.js</c> (one reader for the scan and for
    /// the observation), as an expression the scan's browser evaluates: its <c>read</c> function, called for a scan.
    /// </summary>
    internal static string Reader()
    {
        const string export = "export function read(";
        var module = Script("MentorPageReader.js");
        var at = module.IndexOf(export, StringComparison.Ordinal);
        if (at < 0 || module.IndexOf(export, at + 1, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException("MentorPageReader.js does not have one read function.");
        return "(() => {\n" + module.Replace(export, "function read(", StringComparison.Ordinal) + "\nreturn read({ observe: false });\n})()";
    }

    private static string Script(string name)
    {
        using var stream = typeof(MentorUiScanService).Assembly.GetManifestResourceStream("Novior.MentorAgent.Scanner." + name)
                           ?? throw new InvalidOperationException(name + " is not in the package.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
