using System.Security.Claims;
using System.Security.Cryptography;

namespace Novior.MentorAgent.Core;

/// <summary>
/// Story 1635 — the pages of the application, known with no attribute of ours, for the AutoMentor
/// (<see cref="MentorAutoMentorOptions.Enabled"/>): the host's, read once from its code (<see cref="MentorPageRoutes"/>)
/// and kept, those a WebAssembly client sends to the hub (<c>ReportPages</c>), which the host cannot read, and what
/// the manifest a scan wrote says of each (<see cref="MentorUiManifest"/>), read once with the host's. A route is listed
/// once, the first time it is seen. A page the manifest has is scanned, with its elements; the others are to scan.
/// Story 1640 — and what the widget observed of each page in the users' browsers (<see cref="Observe"/>): an element
/// is the pages' only once two users saw it the same (<c>page.observe-shared</c>). Structure only, kept in memory.
/// </summary>
internal sealed class MentorPageDirectory(IOptions<MentorOptions> options, ILogger<MentorPageDirectory>? logger = null)
{
    internal enum PageOrigin { Host, Client, Manifest }

    /// <summary>
    /// A page: <paramref name="Scan"/> is what the manifest says of it, null when it is to scan; <paramref name="Observed"/>
    /// what the widget observed of it that the scan did not read.
    /// </summary>
    internal sealed record Page(string Route, string? Component, PageOrigin Origin, MentorUiManifest.Page? Scan,
        IReadOnlyList<MentorUiManifest.Element> Observed);

    /// <summary>The most messages one connection or circuit sends (<c>page.observe-limit</c>), as the browser's own limit.</summary>
    internal const int MaxObservedReports = 200;

    /// <summary>The most elements waiting for a second user, over all the pages (<c>page.observe-limit</c>).</summary>
    internal const int MaxWaiting = 10_000;

    /// <summary>How many different users must see an element the same before it is the pages' (<c>page.observe-shared</c>).</summary>
    internal const int SharedAfter = 2;

    /// <summary>An element seen by one user so far: who, as a hash, never the connection or the name.</summary>
    private sealed record Waiting(MentorUiManifest.Element Element, long Viewer);

    private static readonly byte[] Salt = RandomNumberGenerator.GetBytes(32);
    private readonly Dictionary<string, OrderedDictionary<string, MentorUiManifest.Element>> _observed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Waiting> _waiting = new(StringComparer.Ordinal);
    private bool _warnedObserveLimit;
    private bool _warnedBrowserLimit;

    private sealed record RoutePage(string Route, string Component, PageOrigin Origin);

    private readonly Lock _lock = new();
    private readonly List<RoutePage> _pages = [];
    private readonly HashSet<string> _routes = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<MentorUiManifest.Page> _manifest = [];
    private bool _read;
    private bool _warnedShape;
    private bool _warnedLimit;

    /// <summary>How many times the host's code was read for its pages: once at most.</summary>
    internal int Reads { get; private set; }

    /// <summary>For the tests: opens the manifest instead of the application's embedded one.</summary>
    internal Func<Stream?>? OpenManifest { get; init; }

    /// <summary>
    /// The pages, in the order first seen: the host's, then those of its clients, then those only the manifest has
    /// (a WebAssembly client's that has not sent its pages yet), within <see cref="MentorPageRoutes.MaxPages"/>.
    /// </summary>
    internal IReadOnlyList<Page> Pages()
    {
        lock (_lock)
        {
            ReadHost();
            var scans = new Dictionary<string, MentorUiManifest.Page>(StringComparer.OrdinalIgnoreCase);
            foreach (var scan in _manifest) scans.TryAdd(scan.Route, scan);

            var pages = _pages.Select(p => new Page(p.Route, p.Component, p.Origin, scans.GetValueOrDefault(p.Route), ObservedOf(p.Route))).ToList();
            foreach (var scan in _manifest)
            {
                if (pages.Count >= MentorPageRoutes.MaxPages) break;
                if (!_routes.Contains(scan.Route)) pages.Add(new Page(scan.Route, null, PageOrigin.Manifest, scan, ObservedOf(scan.Route)));
            }
            return pages;
        }
    }

    /// <summary>
    /// The pages a WebAssembly client sends. They come from the browser: a route or a component's name of another shape
    /// (<c>page.route-shape</c>) is not listed, nor a page past <see cref="MentorPageRoutes.MaxPages"/>
    /// (<c>page.route-limit</c>); the log says so once, and never with what the client sent.
    /// </summary>
    internal void Report(IEnumerable<PageRouteInfo?>? pages)
    {
        if (pages is null || !options.Value.AutoMentor.Enabled) return;

        lock (_lock)
        {
            ReadHost();
            foreach (var page in pages.Take(MentorPageRoutes.MaxPages))
            {
                if (page is null) continue;
                if (!MentorPageRoutes.IsRoute(page.Route) || !MentorPageRoutes.IsComponent(page.Component))
                {
                    if (!_warnedShape)
                    {
                        _warnedShape = true;
                        logger?.LogWarning(
                            "[MentorAgent] page.route-shape: a client sent a page whose route or component is not of the shape a page has (a route that starts with '/', up to {Max} characters, no white space or control character). It is not listed. This is written once.",
                            MentorPageRoutes.MaxLength);
                    }
                    continue;
                }
                if (!Add(new RoutePage(page.Route, page.Component, PageOrigin.Client))) break;
            }
        }
    }

    /// <summary>
    /// Story 1640 — what the widget observed on the page at <paramref name="path"/>, for <paramref name="viewer"/>
    /// (<see cref="ViewerOf"/>, <see cref="NewViewer"/>), in its <paramref name="report"/>-th message; <paramref name="limit"/>
    /// names the browser's limit when this is its last. The path finds its page (<c>page.observe-route</c>) and is not
    /// kept; each element is checked as the manifest's are (<c>page.observe-values</c>). An element is the page's once
    /// <see cref="SharedAfter"/> users saw it the same, and until then waits in memory (<c>page.observe-shared</c>); in an
    /// app with one user, <paramref name="oneUser"/> (MAUI Hybrid, on the user's own device), at once. The log never has
    /// the path or a text of the page. False: the browser stops observing (off, or past a limit).
    /// </summary>
    internal bool Observe(string? path, IEnumerable<PageObservedElement?>? elements, long viewer, int report, string? limit = null,
        bool oneUser = false)
    {
        var auto = options.Value.AutoMentor;
        if (!auto.Enabled || !auto.ObservePages) return false;

        lock (_lock)
        {
            if (limit is not null && !_warnedBrowserLimit)
            {
                _warnedBrowserLimit = true;
                logger?.LogWarning(
                    "[MentorAgent] page.observe-limit: a browser stopped observing the pages ({Limit}). This is written once.",
                    limit is "elements" or "reports" ? limit : "another limit");
            }
            if (report > MaxObservedReports)
            {
                WarnObserveLimit();
                return false;
            }

            ReadHost();
            var routes = _pages.Select(p => p.Route).Concat(_manifest.Select(m => m.Route));
            if (MentorPageRoutes.Match(path, routes) is not { } route)
            {
                logger?.LogDebug("[MentorAgent] page.observe-route: an observed path is no page's; it is left out.");
                return limit is null;
            }

            var scanned = _manifest.FirstOrDefault(m => string.Equals(m.Route, route, StringComparison.OrdinalIgnoreCase))?.Elements
                          .Select(e => e.Selector).ToHashSet(StringComparer.Ordinal) ?? [];
            if (!_observed.TryGetValue(route, out var observed)) _observed[route] = observed = new(StringComparer.Ordinal);
            int added = 0, waiting = 0;
            foreach (var sent in (elements ?? []).Take(MentorUiManifest.MaxElementsPerPage))
            {
                if (sent is null) continue;
                // No options and no roles: the options of a select may be records, and the users' roles are not the scan's.
                var element = MentorUiManifest.FromScan(sent.Element, sent.Role, sent.Label, sent.Type, null, sent.Form,
                    sent.Selector, null, null, false, false, false, out _);
                if (element is null || scanned.Contains(element.Selector) || observed.ContainsKey(element.Selector)) continue;
                // BUG-256: a selector with anything of the page in it (an id, a class, a name…) is left out, whoever sent it.
                if (!MentorUiManifest.IsObservedSelector(element.Selector)) continue;

                var key = route + "\u0000" + Key(element);
                if (oneUser) { }
                else if (!_waiting.TryGetValue(key, out var first))
                {
                    if (_waiting.Count >= MaxWaiting)
                    {
                        WarnObserveLimit();
                        continue;
                    }
                    _waiting[key] = new Waiting(element, viewer);
                    waiting++;
                    continue;
                }
                else if (first.Viewer == viewer) continue;
                // SharedAfter = 2: the second user who sees it the same makes it the page's.
                else _waiting.Remove(key);
                if (observed.Count >= MentorUiManifest.MaxElementsPerPage)
                {
                    WarnObserveLimit();
                    continue;
                }
                observed[element.Selector] = element;
                added++;
            }
            if (added > 0 || waiting > 0)
                logger?.LogDebug("[MentorAgent] page.observe: {Route}: {Added} element(s) observed by more than one user, {Waiting} waiting for another.",
                    route, added, waiting);
            return limit is null;
        }
    }

    /// <summary>
    /// Who observed, for <see cref="Observe"/>: a hash of the signed-in user's id, with a key of this process, so two tabs
    /// of one user are one user. Null for no signed-in user: then each connection or circuit is one
    /// (<see cref="NewViewer"/>).
    /// </summary>
    internal static long? ViewerOf(ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated != true) return null;
        var id = user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? user.Identity.Name;
        if (string.IsNullOrEmpty(id)) return null;
        var hash = HMACSHA256.HashData(Salt, Encoding.UTF8.GetBytes(user.Identity.AuthenticationType + "\u0000" + id));
        return BitConverter.ToInt64(hash, 0);
    }

    /// <summary>A viewer for a connection or a circuit with no signed-in user: a random number, nothing of the connection.</summary>
    internal static long NewViewer() => BitConverter.ToInt64(RandomNumberGenerator.GetBytes(8), 0);

    private IReadOnlyList<MentorUiManifest.Element> ObservedOf(string route)
        => _observed.TryGetValue(route, out var observed) ? [.. observed.Values] : [];

    private static string Key(MentorUiManifest.Element e)
        => string.Join("\u0000", e.Tag, e.Role, e.Label, e.Type, e.Form, e.Selector);

    private void WarnObserveLimit()
    {
        if (_warnedObserveLimit) return;
        _warnedObserveLimit = true;
        logger?.LogWarning(
            "[MentorAgent] page.observe-limit: the observation of the pages reached one of its limits ({Reports} messages a connection, {Elements} elements a page, {Waiting} elements waiting for a second user); what is past it is not kept. This is written once.",
            MaxObservedReports, MentorUiManifest.MaxElementsPerPage, MaxWaiting);
    }

    private void ReadHost()
    {
        if (_read) return;
        _read = true;

        var auto = options.Value.AutoMentor;
        if (!auto.Enabled) return;

        Reads++;
        var skipped = new List<string>();
        var assemblies = MentorPageRoutes.Assemblies(Assembly.GetEntryAssembly(), auto.PageAssemblies, skipped);
        foreach (var route in MentorPageRoutes.Read(assemblies, skipped))
            if (!Add(new RoutePage(route.Template, route.Component, PageOrigin.Host))) break;

        if (skipped.Count > 0)
            logger?.LogWarning(
                "[MentorAgent] page.read: {Count} assembly(ies) or type(s) could not be read for their pages and were skipped: {Skipped}. The other pages are listed.",
                skipped.Count, string.Join(", ", skipped));
        logger?.LogInformation("[MentorAgent] AutoMentor: {Count} page(s) from the routes of the application.", _pages.Count);

        ReadManifest(assemblies);
    }

    /// <summary>
    /// <c>page.manifest</c>: the manifest the build embedded in the application, the first of the assemblies that has
    /// one. None: every page is to scan, and nothing is logged. One that cannot be used: every page is to scan, and a
    /// warning says so (<c>page.manifest-read</c>, <c>page.manifest-version</c>).
    /// </summary>
    private void ReadManifest(IReadOnlyList<Assembly> assemblies)
    {
        MentorUiManifest.Result result;
        try
        {
            using var stream = OpenManifest is { } open ? open() : Embedded(assemblies);
            if (stream is null) return;
            result = MentorUiManifest.Parse(stream);
        }
        catch (Exception ex)
        {
            logger?.LogWarning("[MentorAgent] page.manifest-read: the manifest of the pages could not be read ({Error}). Every page is to scan.", ex.GetType().Name);
            return;
        }

        if (result.Problem is { } problem)
        {
            logger?.LogWarning(problem == "page.manifest-version"
                ? "[MentorAgent] page.manifest-version: the manifest of the pages is not of version {Version}, the one this library reads. Every page is to scan; scan the application again."
                : "[MentorAgent] page.manifest-read: the manifest of the pages is not a manifest of version {Version}. Every page is to scan.",
                MentorUiManifest.FormatVersion);
            return;
        }

        _manifest = result.Pages;
        if (result.Excluded > 0)
            logger?.LogInformation(
                "[MentorAgent] page.manifest-excluded: {Count} element(s) of the manifest are never let out (a password, card data, a hidden, disabled or excluded element) and were left out.",
                result.Excluded);
        if (result.Dropped > 0)
            logger?.LogWarning(
                "[MentorAgent] page.manifest-shape: {Count} page(s) or element(s) of the manifest are not of the shape MentorAgent reads, or past its limits, and were left out.",
                result.Dropped);
        logger?.LogInformation("[MentorAgent] AutoMentor: the manifest describes {Count} page(s).", _manifest.Count);
    }

    private static Stream? Embedded(IReadOnlyList<Assembly> assemblies)
    {
        foreach (var assembly in assemblies)
        {
            try
            {
                if (assembly.GetManifestResourceStream(MentorUiManifest.ResourceName) is { } stream) return stream;
            }
            catch (Exception ex) when (ex is NotSupportedException or FileNotFoundException or BadImageFormatException)
            {
                // A dynamic assembly has no resources; page.read already named one that does not load.
            }
        }
        return null;
    }

    private bool Add(RoutePage page)
    {
        if (_routes.Contains(page.Route)) return true;
        if (_pages.Count >= MentorPageRoutes.MaxPages)
        {
            if (!_warnedLimit)
            {
                _warnedLimit = true;
                logger?.LogWarning(
                    "[MentorAgent] page.route-limit: the application already lists {Max} pages; the pages after them are not listed. This is written once.",
                    MentorPageRoutes.MaxPages);
            }
            return false;
        }

        _routes.Add(page.Route);
        _pages.Add(page);
        return true;
    }
}
