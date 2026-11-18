using System.IO.Enumeration;

namespace Novior.MentorAgent.Declarative;

/// <summary>
/// Reads the definitions of <see cref="MentorDeclarativeOptions"/>: the inline ones first, then the files of
/// <see cref="MentorDeclarativeOptions.Directory"/> that match a pattern, in name order. The agents' source and the teams'
/// read the same folder this one way.
/// </summary>
internal static class DeclarativeFiles
{
    /// <summary>
    /// Every definition as (source name, YAML). <paramref name="except"/> is a pattern whose files are left to another
    /// source: the agents' source does not read a <c>*.team.yaml</c> its own pattern, say <c>*.yaml</c>, also matches.
    /// </summary>
    internal static List<(string Name, string Yaml)> Read(
        MentorDeclarativeOptions options, string pattern, IList<string> inline, ILogger logger, string? except = null,
        bool warnWhenMissing = true)
    {
        var result = new List<(string, string)>();

        for (var i = 0; i < inline.Count; i++)
            if (!string.IsNullOrWhiteSpace(inline[i]))
                result.Add(($"inline[{i}]", inline[i]));

        if (string.IsNullOrWhiteSpace(options.Directory)) return result;

        var dir = Path.IsPathRooted(options.Directory)
            ? options.Directory
            : Path.Combine(AppContext.BaseDirectory, options.Directory);

        if (!Directory.Exists(dir))
        {
            // Worth a warning rather than silence: a configured directory that is not there is
            // almost always a file that was not copied to the output folder. Said once, by the agents' source.
            if (warnWhenMissing) logger.LogWarning("[MentorAgent:Declarative] Directory '{Dir}' does not exist.", dir);
            return result;
        }

        var option = options.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        foreach (var path in Directory.EnumerateFiles(dir, pattern, option).OrderBy(p => p))
        {
            var name = Path.GetFileName(path);
            if (except is not null && FileSystemName.MatchesSimpleExpression(except, name)) continue;
            try
            {
                result.Add((name, File.ReadAllText(path)));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[MentorAgent:Declarative] Could not read '{Path}'.", path);
            }
        }

        return result;
    }
}
