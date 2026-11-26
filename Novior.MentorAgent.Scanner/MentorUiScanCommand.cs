using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Scanner.Cdp;

namespace Novior.MentorAgent.Scanner;

/// <summary>
/// Story 1637 — <c>novior-mentor scan</c>, the scan of the pages from the command line, for a CI pipeline or a
/// WebAssembly standalone app (<c>page.scan-tool</c>): the pages come from the app's assembly, the app runs at
/// <c>--url</c>, and the browser runs here, on the machine that runs the command. Package Novior.MentorAgent.Cli.
/// </summary>
internal static partial class MentorUiScanCommand
{
    internal const int Done = 0;
    internal const int Failed = 1;
    internal const int Usage = 2;
    internal const int Changed = 3;

    internal const string Help = """
        novior-mentor scan --url <address of the running app> (--project <project file or folder> | --assembly <app's dll>)
                           [--manifest <file>] [--parameters <file.json>] [--full] [--browser <Edge or Chrome>]
                           [--account <role>=<environment variable>]... [--fail-on-change]

          --url         The app to scan, running: a test environment, or the development server of a WebAssembly app.
                        The browser runs on this machine, never on the app's server.
          --project     The app's project: the manifest goes next to it (mentor-ui-manifest.json), the state in obj/,
                        and its pages are read from its newest build in bin/.
          --assembly    The app's built dll, where its pages are read from (with --project, instead of the one in bin/).
          --manifest    Where the manifest goes, instead of the project's mentor-ui-manifest.json.
          --parameters  A JSON file with the values of route parameters: { "/orders/{id}": { "id": "1" } }.
          --full        Read every page again, not only those whose code changed.
          --browser     The Edge or Chrome to start, instead of the first one installed.
          --account     An account of the team with that role alone: the variable holds its session cookie(s),
                        name=value; name=value, a secret of the pipeline. A pass of the scan for each account; the
                        cookies are set for the app's address only and never written to the log. A variable that is
                        missing or empty stops the command.
          --fail-on-change
                        Reads every page and writes nothing: if the manifest would change, the command exits with 3 and
                        names the pages.

        Exit codes: 0 done, 1 the scan could not run, 2 the command line is wrong, 3 the manifest would change.
        """;

    /// <summary>What the command line asks for.</summary>
    internal sealed record Request(Uri Url, string? Project, string? Assembly, string? Manifest, string? Parameters, bool Full, string? Browser,
        IReadOnlyList<(string Role, string Variable)> Accounts, bool FailOnChange);

    /// <summary>
    /// Runs <c>novior-mentor</c> with <paramref name="args"/>; the exit code: 0 done, 1 the scan could not run, 2 the
    /// command line is wrong, 3 with <c>--fail-on-change</c> the manifest would change. <paramref name="variables"/>
    /// reads the environment, by default the process's.
    /// </summary>
    internal static async Task<int> RunAsync(string[] args, ILoggerFactory loggers, TextWriter error, CancellationToken cancellationToken,
        Func<string, string?>? variables = null)
    {
        if (Parse(args, out var problem) is not { } request)
        {
            await error.WriteLineAsync(problem + Environment.NewLine + Environment.NewLine + Help).ConfigureAwait(false);
            return Usage;
        }

        var logger = loggers.CreateLogger("Novior.MentorAgent.Cli");
        var dll = request.Assembly ?? Built(request.Project!);
        if (dll is null || !File.Exists(dll))
        {
            logger.LogError("[MentorAgent] The app's built dll was not found{Where}: build the app first, or give it with --assembly (page.scan-tool).",
                request.Project is null ? "" : " in the bin folder of " + request.Project);
            return Failed;
        }
        // The accounts' cookies, from the environment: never from the command line, never in the log (page.scan-ci).
        var accounts = new List<(string Role, IReadOnlyList<(string Name, string Value)> Cookies)>();
        foreach (var (role, variable) in request.Accounts)
        {
            if (Cookies((variables ?? Environment.GetEnvironmentVariable)(variable)) is not { } cookies)
            {
                logger.LogError("[MentorAgent] The account for {Role} needs its session cookie in the environment variable {Variable}, as name=value; it is missing, empty or of another form. Nothing was scanned (page.scan-ci).",
                    role, variable);
                return Failed;
            }
            accounts.Add((role, cookies));
        }

        var options = new MentorUiScannerOptions
        {
            ManifestPath = request.Manifest ?? Path.Combine(ProjectFolder(request.Project) ?? Path.GetDirectoryName(Path.GetFullPath(dll))!,
                MentorUiScanService.ManifestFileName),
            // A check in CI reads every page: what the app shows now, not what the last scan kept.
            Full = request.Full || request.FailOnChange,
        };
        if (request.Parameters is { } parameters)
        {
            if (ReadParameters(parameters) is not { } values)
            {
                logger.LogError("[MentorAgent] --parameters {File} is not a JSON object of routes, each an object of names and values (page.scan-parameters).",
                    parameters);
                return Failed;
            }
            options.RouteParameters = values;
        }

        var browser = CdpBrowser.Find(request.Browser);
        if (browser is null)
        {
            logger.LogError("[MentorAgent] No Edge or Chrome was found on this machine{Where}, and none is downloaded (page.scan-browser).",
                request.Browser is null ? "" : " at --browser");
            return Failed;
        }

        Assembly app;
        try
        {
            app = Load(dll);
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or FileNotFoundException)
        {
            logger.LogError("[MentorAgent] The app's dll {Dll} could not be loaded ({Type}) (page.scan-tool).", dll, ex.GetType().Name);
            return Failed;
        }

        options.BrowserPath = browser;
        var scan = ForTool(app, options, loggers);
        logger.LogInformation("[MentorAgent] Scanning the pages of {App} at {Url}{Accounts}; manifest {Manifest} (page.scan-tool).",
            app.GetName().Name, request.Url, accounts.Count == 0 ? "" : " with no user and as " + string.Join(", ", accounts.Select(a => a.Role)),
            options.ManifestPath);
        var result = await scan.ScanUrlAsync(request.Url, browser, accounts, write: !request.FailOnChange, cancellationToken).ConfigureAwait(false);
        if (result is null) return Failed;
        if (request.FailOnChange && result.Changed.Count > 0)
        {
            logger.LogError("[MentorAgent] The manifest would change ({Count} page(s): {Pages}); nothing was written. Scan the app and commit its manifest (page.scan-ci).",
                result.Changed.Count, string.Join(", ", result.Changed.Take(50)));
            return Changed;
        }
        return Done;
    }

    /// <summary>The request, or <see langword="null"/> and why.</summary>
    internal static Request? Parse(string[] args, out string problem)
    {
        problem = "";
        if (args.Length == 0 || args[0] is not "scan")
        {
            problem = args.Length == 0 || args[0] is "--help" or "-h" or "help" ? "Usage:" : $"Unknown command '{args[0]}'.";
            return null;
        }

        string? url = null, project = null, assembly = null, manifest = null, parameters = null, browser = null;
        bool full = false, failOnChange = false;
        var accounts = new List<(string Role, string Variable)>();
        for (var i = 1; i < args.Length; i++)
        {
            var name = args[i];
            if (name is "--full" or "--fail-on-change")
            {
                if (name is "--full") full = true;
                else failOnChange = true;
                continue;
            }
            if (name is not ("--url" or "--project" or "--assembly" or "--manifest" or "--parameters" or "--browser" or "--account"))
            {
                problem = $"Unknown option '{name}'.";
                return null;
            }
            if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]) || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                problem = $"{name} needs a value.";
                return null;
            }
            var value = args[++i];
            switch (name)
            {
                case "--url": url = value; break;
                case "--project": project = value; break;
                case "--assembly": assembly = value; break;
                case "--manifest": manifest = value; break;
                case "--parameters": parameters = value; break;
                case "--browser": browser = value; break;
                case "--account":
                    if (Account(value) is not { } account)
                    {
                        problem = "--account needs <role>=<environment variable>: a role the manifest can hold, and a variable's name, never its value.";
                        return null;
                    }
                    if (accounts.Any(a => a.Role == account.Role))
                    {
                        problem = $"--account names the role '{account.Role}' twice.";
                        return null;
                    }
                    accounts.Add(account);
                    break;
            }
        }

        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var address) || (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps))
        {
            problem = "--url needs the http or https address of the running app.";
            return null;
        }
        if (project is null && assembly is null)
        {
            problem = "--project or --assembly is needed: the pages are read from the app's built dll.";
            return null;
        }
        if (project is null && manifest is null)
        {
            problem = "--manifest is needed without --project: the manifest goes next to the project.";
            return null;
        }
        return new Request(new Uri(address.GetLeftPart(UriPartial.Authority) + "/"), project, assembly, manifest, parameters, full, browser,
            accounts, failOnChange);
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex VariableName();

    /// <summary><c>role=VARIABLE</c>: a role the manifest can hold and the name of an environment variable.</summary>
    private static (string Role, string Variable)? Account(string value)
    {
        // At the first '=': a cookie pasted in its place (name=value) leaves a variable name that is not one.
        var equals = value.IndexOf('=');
        if (equals <= 0) return null;
        var role = value[..equals].Trim();
        var variable = value[(equals + 1)..].Trim();
        if (role.Length == 0 || role.Length > MentorUiManifest.MaxTextLength
            || role.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format)
            || !VariableName().IsMatch(variable))
            return null;
        return (role, variable);
    }

    /// <summary>
    /// The cookies a variable holds, <c>name=value; name=value</c>, or <see langword="null"/> when it is missing, empty or
    /// of another form. The values are never written anywhere.
    /// </summary>
    internal static IReadOnlyList<(string Name, string Value)>? Cookies(string? held)
    {
        if (string.IsNullOrWhiteSpace(held)) return null;
        var cookies = new List<(string, string)>();
        foreach (var part in held.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equals = part.IndexOf('=');
            if (equals <= 0 || equals == part.Length - 1 || part.Any(char.IsControl)) return null;
            cookies.Add((part[..equals].Trim(), part[(equals + 1)..].Trim()));
        }
        return cookies.Count == 0 ? null : cookies;
    }

    /// <summary>A scan of the pages of <paramref name="app"/>, with no host: the pages from its assembly, the options given.</summary>
    internal static MentorUiScanService ForTool(Assembly app, MentorUiScannerOptions options, ILoggerFactory loggers)
    {
        var mentor = new MentorOptions { AppName = app.GetName().Name ?? "app", ScanAssemblies = [] };
        mentor.AutoMentor.Enabled = true;
        mentor.AutoMentor.PageAssemblies = [app];
        var directory = new MentorPageDirectory(Microsoft.Extensions.Options.Options.Create(mentor), loggers.CreateLogger<MentorPageDirectory>());
        return new MentorUiScanService(Microsoft.Extensions.Options.Options.Create(mentor), Microsoft.Extensions.Options.Options.Create(options),
            new MentorUiScanGate(), loggers.CreateLogger<MentorUiScanService>(), directory: directory);
    }

    /// <summary>The project's folder: <paramref name="project"/> itself, or the folder of a project file.</summary>
    private static string? ProjectFolder(string? project)
        => project is null ? null : Directory.Exists(project) ? Path.GetFullPath(project) : Path.GetDirectoryName(Path.GetFullPath(project));

    /// <summary>The newest build of the project's dll in its bin folder, by the project's assembly name.</summary>
    internal static string? Built(string project)
    {
        var folder = ProjectFolder(project);
        if (folder is null || !Directory.Exists(folder)) return null;
        var file = File.Exists(project) ? project : Directory.EnumerateFiles(folder, "*.csproj").FirstOrDefault();
        if (file is null) return null;

        var name = Path.GetFileNameWithoutExtension(file);
        try
        {
            if (XDocument.Load(file).Descendants().FirstOrDefault(e => e.Name.LocalName == "AssemblyName")?.Value.Trim() is { Length: > 0 } declared
                && !declared.Contains('$'))
                name = declared;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }

        var bin = Path.Combine(folder, "bin");
        if (!Directory.Exists(bin)) return null;
        return Directory.EnumerateFiles(bin, name + ".dll", SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(s => s is "ref" or "refint" or "obj"))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    /// <summary>
    /// Loads the app's dll, with what it references from its own folder when this process does not have it. Reading its
    /// pages runs the constructors of its attributes: the app's own code.
    /// </summary>
    private static Assembly Load(string dll)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(dll))!;
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            var candidate = Path.Combine(folder, name.Name + ".dll");
            return File.Exists(candidate) ? context.LoadFromAssemblyPath(candidate) : null;
        };
        return AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(dll));
    }

    private static IDictionary<string, IDictionary<string, string>>? ReadParameters(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            var read = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(stream);
            if (read is null) return null;
            var values = new Dictionary<string, IDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (route, names) in read) values[route] = names;
            return values;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
