namespace Novior.MentorAgent.Tests.Docs;

/// <summary>
/// Locates the repository's own files from a test run.
///
/// <para>
/// The documentation suites treat the READMEs as a specification and check them against the real
/// source, so they need the working tree rather than the build output. Nothing here is copied to
/// <c>bin/</c> on purpose: a copied README would let the docs rot while the tests kept passing
/// against a stale snapshot, which is the exact failure these suites exist to prevent.
/// </para>
/// </summary>
internal static class RepoFiles
{
    // The repository's own names, in one place for every test that reads a file by its path: the
    // solution that marks the root, and the project folders, each holding the .csproj of the same name
    // (for a package, also its PackageId). A rename changes these lines; a folder that moves without
    // them stops the source enumeration below instead of leaving a check with nothing to read.
    internal const string Solution     = "Novior.MentorAgent.slnx";
    internal const string Core         = "Novior.MentorAgent";
    internal const string Abstractions = "Novior.MentorAgent.Abstractions";
    internal const string Blazor       = "Novior.MentorAgent.Blazor";
    internal const string Declarative  = "Novior.MentorAgent.Declarative";
    internal const string Server       = "Novior.MentorAgent.Server";
    internal const string Tests        = "Novior.MentorAgent.Tests";

    /// <summary>The five package projects.</summary>
    internal static IReadOnlyList<string> Packages { get; } = [Core, Abstractions, Blazor, Declarative, Server];

    /// <summary>Repository root — the folder holding <see cref="Solution"/>.</summary>
    internal static string Root { get; } = LocateRoot();

    /// <summary>The five package READMEs, keyed by the short name used in failure messages.</summary>
    internal static IReadOnlyDictionary<string, string> Readmes { get; } =
        new Dictionary<string, string>
        {
            ["core"]   = Path.Combine(Root, "README.md"),
            ["server"] = Path.Combine(Root, Server, "README.md"),
            ["blazor"] = Path.Combine(Root, Blazor, "README.md"),
            ["abstr"]  = Path.Combine(Root, Abstractions, "README.md"),
            ["decl"]   = Path.Combine(Root, Declarative, "README.md"),
        };

    /// <summary>Reads one README by its short name.</summary>
    internal static string Readme(string name) => File.ReadAllText(Readmes[name]);

    /// <summary>
    /// Every source file of the five packages. Excludes build output and the test project, so a
    /// symbol that exists only in a test cannot make a documentation claim look satisfied.
    /// </summary>
    internal static IReadOnlyList<string> SourceFiles { get; } = EnumerateSource();

    /// <summary>All source concatenated — for "does this symbol exist anywhere" checks.</summary>
    internal static string SourceText { get; } =
        string.Join('\n', SourceFiles.Select(File.ReadAllText));

    private static string LocateRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir, Solution))) return dir;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }

        throw new InvalidOperationException(
            $"Could not locate the repository root ({Solution}) walking up from {AppContext.BaseDirectory}.");
    }

    private static List<string> EnumerateSource()
    {
        var files = new List<string>();
        foreach (var package in Packages)
        {
            // Not skipped: a missing folder used to leave SourceText without that package, and every
            // "does this symbol exist" check against it passed or failed on the remaining four.
            var root = Path.Combine(Root, package);
            if (!Directory.Exists(root))
                throw new DirectoryNotFoundException(
                    $"The package folder {package} is not in {Root}: RepoFiles names a project that is not there.");

            files.AddRange(Directory
                .EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".css", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                         && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")));
        }

        return files;
    }
}
