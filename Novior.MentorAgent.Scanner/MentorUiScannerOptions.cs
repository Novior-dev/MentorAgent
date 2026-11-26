namespace Novior.MentorAgent.Scanner;

/// <summary>
/// Story 1636 — the options of the scan of the pages, set with <see cref="MentorUiScannerExtensions.AddMentorUiScanner"/>
/// or from the configuration section <c>MentorAgent:Scanner</c>.
/// </summary>
public sealed class MentorUiScannerOptions
{
    /// <summary>The configuration section the options are read from.</summary>
    public const string SectionName = "MentorAgent:Scanner";

    /// <summary>
    /// The browser to start, a full path to Edge's or Chrome's executable. <see langword="null"/>, the default, takes
    /// the first Edge or Chrome installed where their installers put them. The scan never downloads one: with none
    /// found it does not start, and the log says why.
    /// </summary>
    public string? BrowserPath { get; set; }

    /// <summary>
    /// Where the manifest is written. <see langword="null"/>, the default, is the project's
    /// <c>mentor-ui-manifest.json</c>, or the file its <c>MentorUiManifest</c> property names: the one the targets of
    /// Novior.MentorAgent embed in the application at the next build.
    /// </summary>
    public string? ManifestPath { get; set; }

    /// <summary>How long the scan waits for a page to load and settle before it reads it. 15 seconds by default.</summary>
    public TimeSpan PageTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The roles to scan the pages as. The scan reads every page with no user, then once for each role here as a user
    /// of its own that has that role alone, signed in with a one-time token, in <c>Development</c> and from this
    /// machine only. An element seen with no user needs no role in the manifest; one seen only with some of these
    /// roles needs any one of them. Empty, the default, reads with no user only. The scan clicks on what opens tabs,
    /// menus and collapsed sections, as each of these users, on the data of the development environment.
    /// </summary>
    public IList<string> Roles { get; set; } = [];

    /// <summary>
    /// The values of the route parameters, by route, to open a page whose route has parameters: for the page
    /// <c>/orders/{id:int}</c>, <c>{ "/orders/{id}": { "id": "1" } }</c>. The key is the route with each parameter's
    /// name only, since a configuration key cannot hold the colon of a constraint. Each value is escaped and stays in the
    /// page's path; a value of more than 200 characters, with a control character, or that is <c>.</c> or <c>..</c>, is
    /// not used. A route with a parameter that has no value, no default and is not optional is not opened, and stays to
    /// scan.
    /// </summary>
    public IDictionary<string, IDictionary<string, string>> RouteParameters { get; set; }
        = new Dictionary<string, IDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reads every page again. <see langword="false"/>, the default, reads again only the pages whose code, or whose
    /// layout's, changed since the last scan, and those the manifest does not have; the others keep what the manifest
    /// says. A component a page renders is not part of that check: set this, or delete
    /// <c>obj/mentor-ui-scan.state.json</c>, after changing one. Either way the manifest is written only when what it
    /// says changes.
    /// </summary>
    public bool Full { get; set; }

    /// <summary>
    /// Story 1641 — the address of the application's front end, when the pages are not this host's: a React or Angular
    /// application served by its own server, the development server of Vite or of the Angular CLI for instance
    /// (<c>http://localhost:5173/</c>). With it the scan reads the front end's pages instead of the host's
    /// (<c>page.scan-front-end</c>): those of <see cref="FrontEndRoutes"/>, or, with none, those its links reach from its
    /// first page. It reads them with no user, since the scan's user signs in to this host only: <see cref="Roles"/> is
    /// not used. Each page is read at every scan, since no code of the host tells whether it changed. An http or https
    /// address without a query or a fragment; anything else, and the scan does not start.
    /// </summary>
    public Uri? FrontEndUrl { get; set; }

    /// <summary>
    /// Story 1641 — the routes of the front end of <see cref="FrontEndUrl"/>, as its router writes them:
    /// <c>/orders/:id</c> (React Router, Angular) or <c>/orders/{id}</c>; <c>*</c> and <c>**</c> are a catch-all, never
    /// opened. A route with parameters is opened with the values of <see cref="RouteParameters"/>, by the route with
    /// braces (<c>/orders/{id}</c>). Empty, the default: the scan follows the links of the front end from its first page,
    /// those of its own origin, without query or fragment, and none inside <c>data-mentor-exclude</c>.
    /// </summary>
    public IList<string> FrontEndRoutes { get; set; } = [];
}
