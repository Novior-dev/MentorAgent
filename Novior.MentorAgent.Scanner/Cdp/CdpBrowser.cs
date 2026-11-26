using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Novior.MentorAgent.Scanner.Cdp;

/// <summary>
/// Story 1636 — the Edge or Chrome already installed, started headless for one scan: a profile of its own in a
/// temporary folder, deleted at the end, and the DevTools port on the loopback, chosen by the browser and open only for
/// the scan. Nothing is downloaded. Slice B: on Windows the browser runs in a job object that closes it if the
/// application dies without stopping (<see cref="BrowserJob"/>); elsewhere the next scan closes it
/// (<see cref="ScanLeftovers"/>).
/// </summary>
internal sealed class CdpBrowser : IAsyncDisposable
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    private readonly Process? _process;
    private readonly string? _profile;
    private readonly CdpConnection _connection;
    private readonly string _session;

    private CdpBrowser(Process? process, BrowserJob? job, string? profile, CdpConnection connection, string session, Uri? page = null)
    {
        _process = process;
        Job = job;
        _profile = profile;
        _connection = connection;
        _session = session;
        Page = page;
    }

    /// <summary>The job object that closes the browser with the application; <see langword="null"/> where there is none.</summary>
    internal BrowserJob? Job { get; }

    /// <summary>The browser's process id; <see langword="null"/> for a browser the scan did not start.</summary>
    internal int? ProcessId => _process?.Id;

    /// <summary>The browser's profile folder; <see langword="null"/> for a browser the scan did not start.</summary>
    internal string? Profile => _profile;

    /// <summary>
    /// Slice D: the page the scan found in a browser it did not start, a WebView's (<c>page.scan-webview</c>), where it
    /// returns when it ends; <see langword="null"/> for a browser it started.
    /// </summary>
    internal Uri? Page { get; }

    /// <summary>
    /// The browser to start: <paramref name="configured"/> when it is a file, otherwise the first Edge or Chrome found
    /// where the installers put them; <see langword="null"/> when there is none (<c>page.scan-browser</c>).
    /// </summary>
    internal static string? Find(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return File.Exists(configured) ? configured : null;

        foreach (var candidate in Candidates())
            if (File.Exists(candidate)) return candidate;
        return null;
    }

    private static IEnumerable<string> Candidates()
    {
        if (OperatingSystem.IsWindows())
        {
            string[] roots =
            [
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ];
            foreach (var root in roots.Where(r => r.Length > 0))
            {
                yield return Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe");
                yield return Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe");
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge";
            yield return "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
            yield return "/Applications/Chromium.app/Contents/MacOS/Chromium";
        }
        else
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var name in new[] { "microsoft-edge", "microsoft-edge-stable", "google-chrome", "google-chrome-stable", "chromium", "chromium-browser" })
                foreach (var folder in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                    yield return Path.Combine(folder, name);
        }
    }

    /// <summary>Starts <paramref name="executable"/> headless and opens one tab to read the pages in.</summary>
    internal static async Task<CdpBrowser> StartAsync(string executable, CancellationToken cancellationToken)
    {
        var profile = ScanLeftovers.CreateProfile(Path.GetTempPath());

        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[]
                 {
                     "--headless=new", "--remote-debugging-address=127.0.0.1", "--remote-debugging-port=0",
                     "--user-data-dir=" + profile, "--no-first-run", "--no-default-browser-check", "--disable-extensions",
                     "--disable-sync", "--disable-background-networking", "--disable-default-apps", "--mute-audio",
                     "about:blank",
                 })
            start.ArgumentList.Add(argument);

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new CdpException("The browser did not start.");
        }
        catch
        {
            DeleteProfile(profile);
            throw;
        }
        var job = BrowserJob.TryContain(process);
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            ScanLeftovers.Note(profile, process);
            var endpoint = await EndpointAsync(process, profile, cancellationToken).ConfigureAwait(false);
            var connection = await CdpConnection.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
            var target = await connection.SendAsync("Target.createTarget", new JsonObject { ["url"] = "about:blank" }, null,
                CommandTimeout, cancellationToken).ConfigureAwait(false);
            var attached = await connection.SendAsync("Target.attachToTarget",
                new JsonObject { ["targetId"] = target.GetProperty("targetId").GetString(), ["flatten"] = true }, null,
                CommandTimeout, cancellationToken).ConfigureAwait(false);
            return new CdpBrowser(process, job, profile, connection, attached.GetProperty("sessionId").GetString()!);
        }
        catch
        {
            try
            {
                Stop(process, DefaultExitWait, KillTree);
            }
            finally
            {
                job?.Dispose();
                DeleteProfile(profile);
            }
            throw;
        }
    }

    /// <summary>
    /// Slice D — a browser the scan did not start, such as the WebView2 of a MAUI app: the DevTools port on the loopback,
    /// <paramref name="port"/>, and in it the first page with an http or https address, waited for up to
    /// <paramref name="wait"/>. <see langword="null"/> when nothing answered on the port or showed a page in that time.
    /// The scan neither closes that browser nor deletes anything of it: it returns to the page it found.
    /// </summary>
    internal static async Task<CdpBrowser?> AttachAsync(int port, TimeSpan wait, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + wait;
        Uri? endpoint = null;
        using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
        {
            while (endpoint is null)
            {
                try
                {
                    var json = await http.GetStringAsync(new Uri($"http://127.0.0.1:{port}/json/version"), cancellationToken).ConfigureAwait(false);
                    using var version = JsonDocument.Parse(json);
                    if (version.RootElement.TryGetProperty("webSocketDebuggerUrl", out var url)
                        && Uri.TryCreate(url.GetString(), UriKind.Absolute, out var socket) && socket.IsLoopback)
                        endpoint = socket;
                }
                catch (Exception ex) when (ex is HttpRequestException or JsonException
                                               or TaskCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    // Not listening yet: the WebView is created with the app's window.
                }
                if (endpoint is not null) break;
                if (DateTime.UtcNow >= deadline) return null;
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
            }
        }

        var connection = await CdpConnection.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                var targets = await connection.SendAsync("Target.getTargets", null, null, CommandTimeout, cancellationToken).ConfigureAwait(false);
                foreach (var target in targets.GetProperty("targetInfos").EnumerateArray())
                {
                    if (target.GetProperty("type").GetString() != "page"
                        || !Uri.TryCreate(target.GetProperty("url").GetString(), UriKind.Absolute, out var page)
                        || (page.Scheme != Uri.UriSchemeHttp && page.Scheme != Uri.UriSchemeHttps))
                        continue;
                    var attached = await connection.SendAsync("Target.attachToTarget",
                        new JsonObject { ["targetId"] = target.GetProperty("targetId").GetString(), ["flatten"] = true }, null,
                        CommandTimeout, cancellationToken).ConfigureAwait(false);
                    return new CdpBrowser(null, null, null, connection, attached.GetProperty("sessionId").GetString()!, page);
                }
                if (DateTime.UtcNow >= deadline)
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                    return null;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The browser writes its port and path in <c>DevToolsActivePort</c>, in the profile, when it listens.</summary>
    private static async Task<Uri> EndpointAsync(Process process, string profile, CancellationToken cancellationToken)
    {
        var file = Path.Combine(profile, "DevToolsActivePort");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited) throw new CdpException($"The browser exited with code {process.ExitCode} before it listened.");
            if (File.Exists(file))
            {
                string[] lines;
                try
                {
                    lines = await File.ReadAllLinesAsync(file, cancellationToken).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    lines = [];
                }
                if (lines.Length >= 2 && int.TryParse(lines[0], out var port) && lines[1].StartsWith('/'))
                    return new Uri($"ws://127.0.0.1:{port}{lines[1]}");
            }
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
        throw new CdpException("The browser did not open its DevTools port in 30 s.");
    }

    /// <summary>Runs <paramref name="expression"/> in the page and returns its value.</summary>
    internal async Task<JsonElement> EvaluateAsync(string expression, CancellationToken cancellationToken)
    {
        var result = await _connection.SendAsync("Runtime.evaluate",
            new JsonObject { ["expression"] = expression, ["returnByValue"] = true, ["awaitPromise"] = true }, _session,
            CommandTimeout, cancellationToken).ConfigureAwait(false);
        if (result.TryGetProperty("exceptionDetails", out var failure))
            throw new CdpException("The page's script failed: " + (failure.TryGetProperty("text", out var text) ? text.GetString() : "no text"));
        return result.GetProperty("result").TryGetProperty("value", out var value) ? value : default;
    }

    /// <summary>
    /// Sends a DevTools command to the page, as <c>Emulation.setEmulatedMedia</c>: for the accessibility tests (story
    /// 750), which ask for less motion and for a narrow screen. Its result.
    /// </summary>
    internal Task<JsonElement> SendCommandAsync(string method, JsonObject? parameters, CancellationToken cancellationToken)
        => _connection.SendAsync(method, parameters, _session, CommandTimeout, cancellationToken);

    /// <summary>
    /// Opens <paramref name="address"/> and waits until the page is loaded and its elements stop changing, which for an
    /// interactive page is after its circuit renders, or until <paramref name="timeout"/>.
    /// </summary>
    internal async Task OpenAsync(Uri address, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var navigated = await _connection.SendAsync("Page.navigate", new JsonObject { ["url"] = address.AbsoluteUri }, _session,
            CommandTimeout, cancellationToken).ConfigureAwait(false);
        if (navigated.TryGetProperty("errorText", out var error) && error.GetString() is { Length: > 0 } why)
            throw new CdpException($"The page did not open: {why}.");

        await SettleAsync(timeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Story 1637: forgets every cookie, so the next pass starts with no account.</summary>
    internal async Task ClearCookiesAsync(CancellationToken cancellationToken)
        => await _connection.SendAsync("Network.clearBrowserCookies", null, _session, CommandTimeout, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Story 1637: sets <paramref name="cookies"/> for <paramref name="site"/> only, as host-only cookies, HTTP-only,
    /// secure when the address is https; whether the browser took them all.
    /// </summary>
    internal async Task<bool> SetCookiesAsync(Uri site, IReadOnlyList<(string Name, string Value)> cookies, CancellationToken cancellationToken)
    {
        foreach (var (name, value) in cookies)
        {
            var set = await _connection.SendAsync("Network.setCookie", new JsonObject
            {
                ["name"] = name,
                ["value"] = value,
                ["url"] = site.GetLeftPart(UriPartial.Authority) + "/",
                ["httpOnly"] = true,
                ["secure"] = site.Scheme == Uri.UriSchemeHttps,
            }, _session, CommandTimeout, cancellationToken).ConfigureAwait(false);
            if (!set.TryGetProperty("success", out var ok) || ok.ValueKind == JsonValueKind.False) return false;
        }
        return true;
    }

    /// <summary>
    /// Opens <paramref name="address"/> with <paramref name="name"/>: <paramref name="value"/> in the request's headers,
    /// that request only, and returns whether the page answered.
    /// </summary>
    internal async Task<bool> OpenWithHeaderAsync(Uri address, string name, string value, CancellationToken cancellationToken)
    {
        await _connection.SendAsync("Network.enable", null, _session, CommandTimeout, cancellationToken).ConfigureAwait(false);
        try
        {
            await _connection.SendAsync("Network.setExtraHTTPHeaders", new JsonObject { ["headers"] = new JsonObject { [name] = value } },
                _session, CommandTimeout, cancellationToken).ConfigureAwait(false);
            var navigated = await _connection.SendAsync("Page.navigate", new JsonObject { ["url"] = address.AbsoluteUri }, _session,
                CommandTimeout, cancellationToken).ConfigureAwait(false);
            return !(navigated.TryGetProperty("errorText", out var error) && error.GetString() is { Length: > 0 });
        }
        finally
        {
            await _connection.SendAsync("Network.setExtraHTTPHeaders", new JsonObject { ["headers"] = new JsonObject() },
                _session, CommandTimeout, CancellationToken.None).ConfigureAwait(false);
            await _connection.SendAsync("Network.disable", null, _session, CommandTimeout, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>The page's address without its fragment: where the browser is now.</summary>
    internal async Task<string?> LocationAsync(CancellationToken cancellationToken)
    {
        var location = await EvaluateAsync("location.origin + location.pathname + location.search", cancellationToken).ConfigureAwait(false);
        return location.ValueKind == JsonValueKind.String ? location.GetString() : null;
    }

    /// <summary>
    /// Waits until the page is loaded and its elements stop changing, which for an interactive page is after its
    /// circuit renders, or until <paramref name="timeout"/>.
    /// </summary>
    internal async Task SettleAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        var last = -1;
        var stable = 0;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            var state = await EvaluateAsync("[document.readyState, document.querySelectorAll('*').length]", cancellationToken).ConfigureAwait(false);
            if (state.ValueKind != JsonValueKind.Array || state[0].GetString() != "complete") continue;
            var count = state[1].GetInt32();
            stable = count == last ? stable + 1 : 0;
            last = count;
            if (stable >= 2) return;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is null)
        {
            // Not the scan's browser: back to the page it found, and nothing closed.
            try
            {
                if (Page is not null)
                    await _connection.SendAsync("Page.navigate", new JsonObject { ["url"] = Page.AbsoluteUri }, _session, TimeSpan.FromSeconds(10),
                        CancellationToken.None).ConfigureAwait(false);
                await _connection.SendAsync("Target.detachFromTarget", new JsonObject { ["sessionId"] = _session }, null, TimeSpan.FromSeconds(5),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is CdpException or OperationCanceledException or System.Net.WebSockets.WebSocketException
                                           or ObjectDisposedException or InvalidOperationException or TimeoutException)
            {
                // The app may have closed meanwhile.
            }
            await _connection.DisposeAsync().ConfigureAwait(false);
            return;
        }

        try
        {
            await _connection.SendAsync("Browser.close", null, null, TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CdpException or OperationCanceledException or System.Net.WebSockets.WebSocketException
                                       or ObjectDisposedException or InvalidOperationException)
        {
            // It may close before it answers, or be closed already.
        }
        await _connection.DisposeAsync().ConfigureAwait(false);
        try
        {
            Stop(_process, ExitWait, KillProcess);
        }
        finally
        {
            // BUG-255: also when the stop fails, the job and the profile do not stay behind.
            Job?.Dispose();   // whatever of the browser still runs closes with the job
            DeleteProfile(_profile!);
        }
    }

    /// <summary>How long the browser has to exit, after it is asked to and after it is killed.</summary>
    internal TimeSpan ExitWait { get; set; } = DefaultExitWait;

    private static readonly TimeSpan DefaultExitWait = TimeSpan.FromSeconds(5);

    /// <summary>Kills the browser and its helpers when it does not exit by itself (a test replaces it).</summary>
    internal Action<Process> KillProcess { get; set; } = KillTree;

    private static readonly Action<Process> KillTree = process => process.Kill(entireProcessTree: true);

    internal static void Stop(Process process, TimeSpan wait, Action<Process> kill)
    {
        try
        {
            if (!process.WaitForExit(wait)) kill(process);
            process.WaitForExit(wait);
        }
        catch (Exception ex) when (IsGoneOrNotOurs(ex))
        {
            // Exited meanwhile, or a helper that cannot be killed from here: the job closes what is left.
        }
        finally
        {
            process.Dispose();
        }
    }

    /// <summary>
    /// What stopping a browser that is exiting may meet. Killing the whole tree reports each process it could not
    /// kill, a helper on its way out answering "access denied", together in an <see cref="AggregateException"/>
    /// (BUG-255): it is harmless only when every one of them is.
    /// </summary>
    private static bool IsGoneOrNotOurs(Exception ex)
        => ex is InvalidOperationException or System.ComponentModel.Win32Exception
           || ex is AggregateException { InnerExceptions.Count: > 0 } all && all.InnerExceptions.All(IsGoneOrNotOurs);

    /// <summary>How many times the profile is deleted, 200 ms apart, before it is left to the next scan.</summary>
    internal const int ProfileAttempts = 25;

    internal static void DeleteProfile(string profile)
    {
        // The browser's helpers may hold a file for a moment after it exits, longer on a busy machine: up to 5 seconds
        // (BUG-255). What is left the next scan cleans up (ScanLeftovers).
        for (var attempt = 0; attempt < ProfileAttempts && Directory.Exists(profile); attempt++)
        {
            try
            {
                Directory.Delete(profile, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(200);
            }
        }
    }
}
