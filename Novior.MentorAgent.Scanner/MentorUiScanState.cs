using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Components;

namespace Novior.MentorAgent.Scanner;

/// <summary>
/// Story 1636, slice C — what the scan read last time, to read again only the pages that changed
/// (<c>page.scan-changed</c>). Each page has a fingerprint: a hash of the code that renders it and its layouts (the IL
/// of <c>BuildRenderTree</c> and of the render fragments compiled with it), of the route and the address opened, of the
/// roles of the scan and of its scripts. The fingerprints are kept in a state file in <c>obj/</c>, never in the
/// manifest. A component the page renders is not in its fingerprint: <see cref="MentorUiScannerOptions.Full"/>, or
/// deleting the state file, reads every page again.
/// </summary>
internal static class MentorUiScanState
{
    internal const string FileName = "mentor-ui-scan.state.json";
    private const int Version = 1;
    internal const int MaxLayouts = 8;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private sealed class FileDto
    {
        public int Version { get; set; }
        public SortedDictionary<string, string>? Pages { get; set; }
    }

    /// <summary>The fingerprints of the last scan, by route; empty when there is no state file or it cannot be read.</summary>
    internal static IReadOnlyDictionary<string, string> Read(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var file = JsonSerializer.Deserialize<FileDto>(stream, Json);
            if (file?.Version != Version || file.Pages is null) return new Dictionary<string, string>();
            return new Dictionary<string, string>(file.Pages, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new Dictionary<string, string>();
        }
    }

    /// <summary>The text of a state file with <paramref name="pages"/>' fingerprints.</summary>
    internal static string Serialize(IEnumerable<KeyValuePair<string, string>> pages)
        => JsonSerializer.Serialize(new FileDto { Version = Version, Pages = new SortedDictionary<string, string>(
            pages.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal), StringComparer.Ordinal) }, Json);

    /// <summary>
    /// The fingerprint of a page served by <paramref name="component"/>, or <see langword="null"/> when its code cannot
    /// be read: then the page is always read again.
    /// </summary>
    internal static string? Fingerprint(Type? component, string route, string path, IEnumerable<string> roles, IEnumerable<string> scripts)
    {
        if (component is null) return null;
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            void Add(string text)
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                hash.AppendData(BitConverter.GetBytes(bytes.Length));
                hash.AppendData(bytes);
            }

            Add("v" + Version);
            Add(route);
            Add(path);
            foreach (var role in roles) Add("role:" + role);
            foreach (var script in scripts) Add("script:" + script);

            foreach (var method in Rendered(component).SelectMany(RenderingMethods))
            {
                Add(method.DeclaringType!.FullName + "." + method.Name + "("
                    + string.Join(",", method.GetParameters().Select(p => p.ParameterType.FullName)) + ")");
                var il = method.GetMethodBody()?.GetILAsByteArray() ?? [];
                hash.AppendData(BitConverter.GetBytes(il.Length));
                hash.AppendData(il);
            }
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        catch (Exception ex) when (ex is TypeLoadException or FileNotFoundException or FileLoadException or BadImageFormatException
                                       or InvalidOperationException or NotSupportedException or MemberAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The page's component, then its layout, the layout's layout and so on: at most <see cref="MaxLayouts"/> layouts,
    /// each once.
    /// </summary>
    internal static IReadOnlyList<Type> Rendered(Type component)
    {
        var rendered = new List<Type>();
        for (Type? type = component; type is not null && rendered.Count <= MaxLayouts && !rendered.Contains(type);
             type = type.GetCustomAttribute<LayoutAttribute>()?.LayoutType)
            rendered.Add(type);
        return rendered;
    }

    /// <summary>
    /// The code that renders <paramref name="component"/>: <c>BuildRenderTree</c> of the class and of its base classes up
    /// to <see cref="ComponentBase"/>, and the methods the compiler made of their render fragments, which it names after
    /// <c>BuildRenderTree</c>, in the classes and in their nested types; in a fixed order.
    /// </summary>
    internal static IReadOnlyList<MethodInfo> RenderingMethods(Type component)
    {
        const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic
                                      | BindingFlags.DeclaredOnly;
        var methods = new List<MethodInfo>();
        for (var type = component; type is not null && type != typeof(ComponentBase) && type != typeof(object); type = type.BaseType)
            methods.AddRange(new[] { type }.Concat(type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                .SelectMany(t => t.GetMethods(Declared))
                .Where(m => m.Name.Contains("BuildRenderTree", StringComparison.Ordinal))
                .OrderBy(m => m.DeclaringType!.FullName, StringComparer.Ordinal)
                .ThenBy(m => m.Name, StringComparer.Ordinal)
                .ThenBy(m => m.ToString(), StringComparer.Ordinal));
        return methods;
    }
}
