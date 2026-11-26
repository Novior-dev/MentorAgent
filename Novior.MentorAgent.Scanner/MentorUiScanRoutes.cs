using System.Text.RegularExpressions;
using Novior.MentorAgent.Abstractions.Core;

namespace Novior.MentorAgent.Scanner;

/// <summary>
/// Story 1636, slice C — the address the scan opens for a route with parameters (<c>page.scan-parameters</c>): the values
/// of <see cref="MentorUiScannerOptions.RouteParameters"/>, each escaped, and never <c>.</c> or <c>..</c>, so a value
/// never leaves the page's path. A parameter with no value and no default, unless optional, leaves the route to scan.
/// </summary>
internal static partial class MentorUiScanRoutes
{
    /// <summary>The longest value used; a longer one is not.</summary>
    internal const int MaxValueLength = 200;

    [GeneratedRegex(@"\{(?<star>\*{0,2})(?<name>[^{}:?=*]+)(?<rest>[^{}]*)\}")]
    private static partial Regex Parameter();

    [GeneratedRegex(@"^:(?<name>[A-Za-z_][A-Za-z0-9_]*)(?<optional>\?)?$")]
    private static partial Regex FrontEndParameter();

    /// <summary>
    /// Story 1641 — a route of <see cref="MentorUiScannerOptions.FrontEndRoutes"/> as the scan writes it: <c>:id</c> is
    /// <c>{id}</c>, <c>:id?</c> is <c>{id?}</c>, <c>*</c> and <c>**</c> a catch-all, and a leading <c>/</c> when it has
    /// none. Null when it is not a route (<c>page.route-shape</c>).
    /// </summary>
    internal static string? FromFrontEnd(string? given)
    {
        if (string.IsNullOrWhiteSpace(given)) return null;
        var route = given.Trim();
        if (route[0] != '/') route = "/" + route;
        route = string.Join('/', route.Split('/').Select(segment => segment is "*" or "**"
            ? "{*rest}"
            : FrontEndParameter().Match(segment) is { Success: true } m
                ? "{" + m.Groups["name"].Value + m.Groups["optional"].Value + "}"
                : segment));
        return MentorPageRoutes.IsRoute(route) ? route : null;
    }

    /// <summary>
    /// The route's key in <see cref="MentorUiScannerOptions.RouteParameters"/>: the template with each parameter's
    /// name only, <c>/orders/{id}</c> for <c>/orders/{id:int}</c>. A configuration key cannot hold a colon.
    /// </summary>
    internal static string Key(string template)
        => Parameter().Replace(template, m => "{" + m.Groups["name"].Value.Trim() + "}");

    /// <summary>
    /// The path to open for <paramref name="template"/>, or <see langword="null"/> when a parameter it needs has no usable
    /// value. A template with no parameter is its own path.
    /// </summary>
    internal static string? Fill(string template, IReadOnlyDictionary<string, string>? values)
    {
        var missing = false;
        var path = Parameter().Replace(template, m =>
        {
            var name = m.Groups["name"].Value.Trim();
            var rest = m.Groups["rest"].Value;
            var catchAll = m.Groups["star"].Value.Length > 0;
            string? value = null;
            if (values is not null && values.TryGetValue(name, out var given) && Usable(given, catchAll)) value = given;
            else if (rest.IndexOf('=') is var eq and >= 0) value = rest[(eq + 1)..];
            else if (!rest.EndsWith('?') && !catchAll)
            {
                missing = true;
                return "";
            }
            if (string.IsNullOrEmpty(value)) return "";
            return catchAll
                ? string.Join('/', value.Split('/').Select(Uri.EscapeDataString))
                : Uri.EscapeDataString(value);
        });
        if (missing) return null;

        // An optional parameter with no value leaves its segment empty: the router reads the path without it.
        while (path.Contains("//", StringComparison.Ordinal)) path = path.Replace("//", "/", StringComparison.Ordinal);
        return path.Length > 1 ? path.TrimEnd('/') : path;
    }

    /// <summary>
    /// A value of the right size, with no control character, and no <c>.</c> or <c>..</c> segment: the browser would
    /// read one as a step out of the page's path.
    /// </summary>
    private static bool Usable(string? value, bool catchAll)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= MaxValueLength && !value.Any(char.IsControl)
           && (catchAll ? value.Split('/') : [value]).All(s => s is not ("." or ".."));
}
