using System.Reflection;
using Microsoft.AspNetCore.Components;

namespace Novior.MentorAgent.Abstractions.Core;

/// <summary>
/// Story 1635 — the pages of a Blazor application, read from its code with no attribute of ours: the components that
/// carry <see cref="RouteAttribute"/> (<c>@page</c>), as the router finds them (public types that implement
/// <see cref="IComponent"/>, the attribute on the type itself, not inherited). One reader for the host, in
/// <c>Novior.MentorAgent</c> (Blazor Server, the server of Auto, MAUI Hybrid), and for the WebAssembly client in
/// <c>Novior.MentorAgent.Blazor</c>. An assembly that does not load, or a type that throws while it is read, is skipped
/// and named in <c>skipped</c>: reading the pages never stops the application (<c>page.read</c>).
/// </summary>
internal static class MentorPageRoutes
{
    /// <summary>The most characters of a route template, and of a component's name (<c>page.route-shape</c>).</summary>
    internal const int MaxLength = 256;

    /// <summary>The most pages one process lists, those of the host and those its clients send (<c>page.route-limit</c>).</summary>
    internal const int MaxPages = 1000;

    internal sealed record Route(string Template, string Component);

    /// <summary>
    /// The assemblies to read: <paramref name="entry"/>, the assemblies it references that use the components (the
    /// client project of an Auto application, a Razor class library given to <c>AddAdditionalAssemblies</c>), then
    /// <paramref name="extra"/>. The framework's and MentorAgent's own are not read.
    /// </summary>
    internal static IReadOnlyList<Assembly> Assemblies(Assembly? entry, IEnumerable<Assembly?> extra, List<string> skipped)
    {
        var result = new List<Assembly>();
        void Add(Assembly assembly)
        {
            if (!result.Contains(assembly)) result.Add(assembly);
        }

        if (entry is not null)
        {
            Add(entry);
            AssemblyName[] references;
            try
            {
                references = entry.GetReferencedAssemblies();
            }
            catch (Exception ex)
            {
                skipped.Add($"{entry.GetName().Name} ({ex.GetType().Name})");
                references = [];
            }

            foreach (var name in references)
            {
                if (IsFramework(name.Name)) continue;
                try
                {
                    var assembly = Assembly.Load(name);
                    if (UsesComponents(assembly)) Add(assembly);
                }
                catch (Exception ex)
                {
                    skipped.Add($"{name.Name} ({ex.GetType().Name})");
                }
            }
        }

        foreach (var assembly in extra)
            if (assembly is not null) Add(assembly);
        return result;
    }

    /// <summary>The pages of <paramref name="assemblies"/>, one per route template (the router's match ignores case).</summary>
    internal static IReadOnlyList<Route> Read(IEnumerable<Assembly> assemblies, List<string> skipped)
    {
        var routes = new List<Route>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var assembly in assemblies)
        {
            Type[] types;
            try
            {
                types = assembly.GetExportedTypes();
            }
            catch (Exception ex)
            {
                skipped.Add($"{assembly.GetName().Name} ({ex.GetType().Name})");
                continue;
            }

            foreach (var type in types.OrderBy(t => t.FullName, StringComparer.Ordinal))
            {
                RouteAttribute[] attributes;
                try
                {
                    if (!typeof(IComponent).IsAssignableFrom(type)) continue;
                    attributes = (RouteAttribute[])type.GetCustomAttributes(typeof(RouteAttribute), inherit: false);
                }
                catch (Exception ex)
                {
                    skipped.Add($"{type.FullName} ({ex.GetType().Name})");
                    continue;
                }

                var component = type.FullName ?? type.Name;
                foreach (var attribute in attributes.OrderBy(a => a.Template, StringComparer.Ordinal))
                    if (IsRoute(attribute.Template) && IsComponent(component) && seen.Add(attribute.Template))
                        routes.Add(new Route(attribute.Template, component));
            }
        }
        return routes;
    }

    /// <summary>The longest path <see cref="Match"/> reads.</summary>
    internal const int MaxPathLength = 2048;

    /// <summary>
    /// Story 1640 — the page a path opens (<c>page.observe-route</c>): the one of <paramref name="templates"/> the router
    /// would take, a literal segment before a parameter with a constraint, before a plain one, before an optional one,
    /// before a catch-all. Null when the path is no page's. The constraints the router knows are checked
    /// (<c>bool</c>, <c>datetime</c>, <c>decimal</c>, <c>double</c>, <c>float</c>, <c>guid</c>, <c>int</c>, <c>long</c>,
    /// <c>nonfile</c>); another lets any value through. A segment that mixes text and a parameter is not read.
    /// </summary>
    internal static string? Match(string? path, IEnumerable<string> templates)
    {
        if (path is not { Length: > 0 and <= MaxPathLength } || path[0] != '/') return null;
        string[] segments;
        try
        {
            segments = [.. path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString)];
        }
        catch (UriFormatException)
        {
            return null;
        }

        string? best = null;
        int[]? bestRanks = null;
        foreach (var template in templates)
        {
            if (Ranks(template, segments) is not { } ranks) continue;
            if (best is null || Better(ranks, bestRanks!))
            {
                best = template;
                bestRanks = ranks;
            }
        }
        return best;
    }

    // What each of the template's segments is, if the template takes the path: 4 a literal, 3 a parameter with a
    // constraint, 2 a plain one, 1 an optional one, 0 a catch-all.
    private static int[]? Ranks(string template, string[] segments)
    {
        var parts = template.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var ranks = new int[parts.Length];
        var at = 0;
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (!part.StartsWith('{') || !part.EndsWith('}'))
            {
                if (part.Contains('{') || part.Contains('}')) return null;
                if (at >= segments.Length || !string.Equals(part, segments[at], StringComparison.OrdinalIgnoreCase)) return null;
                ranks[i] = 4;
                at++;
                continue;
            }

            var inner = part[1..^1];
            if (inner.Contains('{') || inner.Contains('}')) return null;
            if (inner.StartsWith('*'))
            {
                ranks[i] = 0;
                return i == parts.Length - 1 ? ranks : null;
            }
            var optional = inner.EndsWith('?');
            if (optional) inner = inner[..^1];
            if (inner.IndexOf('=') is var eq and >= 0)
            {
                optional = true;
                inner = inner[..eq];
            }
            var constraints = inner.Split(':')[1..];
            if (at >= segments.Length)
            {
                if (!optional) return null;
                ranks[i] = 1;
                continue;
            }
            if (!constraints.All(c => Fits(c, segments[at]))) return null;
            ranks[i] = optional ? 1 : constraints.Length > 0 ? 3 : 2;
            at++;
        }
        return at == segments.Length ? ranks : null;
    }

    private static bool Better(int[] ranks, int[] than)
    {
        for (var i = 0; i < Math.Min(ranks.Length, than.Length); i++)
            if (ranks[i] != than[i]) return ranks[i] > than[i];
        return ranks.Length > than.Length;
    }

    private static bool Fits(string constraint, string value)
    {
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        return constraint.Trim().ToLowerInvariant() switch
        {
            "bool"     => bool.TryParse(value, out _),
            "datetime" => DateTime.TryParse(value, invariant, System.Globalization.DateTimeStyles.None, out _),
            "decimal"  => decimal.TryParse(value, System.Globalization.NumberStyles.Number, invariant, out _),
            "double"   => double.TryParse(value, System.Globalization.NumberStyles.Float, invariant, out _),
            "float"    => float.TryParse(value, System.Globalization.NumberStyles.Float, invariant, out _),
            "guid"     => Guid.TryParse(value, out _),
            "int"      => int.TryParse(value, System.Globalization.NumberStyles.Integer, invariant, out _),
            "long"     => long.TryParse(value, System.Globalization.NumberStyles.Integer, invariant, out _),
            "nonfile"  => !value.Contains('.'),
            _          => true,
        };
    }

    /// <summary><c>page.route-shape</c>: a template that starts with <c>/</c>, of 1 to 256 characters, with no white space or control character.</summary>
    internal static bool IsRoute(string? template)
        => template is { Length: > 0 and <= MaxLength } && template[0] == '/' && Printable(template);

    /// <summary><c>page.route-shape</c>: a type's name of 1 to 256 characters, with no white space or control character.</summary>
    internal static bool IsComponent(string? component)
        => component is { Length: > 0 and <= MaxLength } && Printable(component);

    private static bool Printable(string text)
    {
        foreach (var c in text)
            if (char.IsWhiteSpace(c) || char.IsControl(c) || char.IsSurrogate(c)
                || char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.Format)
                return false;
        return true;
    }

    private static bool IsFramework(string? name)
        => name is null or "netstandard" or "mscorlib" or "System"
           || name.StartsWith("System.", StringComparison.Ordinal)
           || name.StartsWith("Microsoft.", StringComparison.Ordinal)
           || name.StartsWith("Novior.MentorAgent", StringComparison.Ordinal);

    private static bool UsesComponents(Assembly assembly)
        => assembly.GetReferencedAssemblies().Any(r => r.Name == "Microsoft.AspNetCore.Components");
}
