using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Novior.MentorAgent.Core;

/// <summary>
/// Story 1635 — the AutoMentor's manifest of the pages, <c>mentor-ui-manifest.json</c>: what a scan read of each page,
/// structure only. The build puts it in the application as the resource <see cref="ResourceName"/> (the
/// <c>buildTransitive</c> targets of the package embed the file when the project has one), and the host reads it once,
/// with the pages. A manifest written by hand, or changed after the scan, is read as untrusted: an element MentorAgent
/// never lets out (a password, card data, a hidden, disabled or excluded element) is dropped, a value has no place in
/// the format and is never read, and an element of another shape, or past a limit, is dropped.
/// </summary>
internal static class MentorUiManifest
{
    /// <summary>The logical name of the embedded manifest, set by <c>buildTransitive/Novior.MentorAgent.targets</c>.</summary>
    internal const string ResourceName = "Novior.MentorAgent.UiManifest.json";

    /// <summary>The version of the format this library reads.</summary>
    internal const int FormatVersion = 1;

    internal const int MaxElementsPerPage = 500;
    internal const int MaxTagLength = 32;
    internal const int MaxTextLength = 256;
    internal const int MaxSelectorLength = 512;
    internal const int MaxOptions = 64;
    internal const int MaxRoles = 32;

    internal sealed record Element(
        string Tag, string? Role, string? Label, string? Type, IReadOnlyList<string> Options, string? Form,
        string Selector, IReadOnlyList<string> Roles);

    internal sealed record Page(string Route, DateTimeOffset? ScannedAt, IReadOnlyList<Element> Elements);

    /// <summary>What was read. <see cref="Problem"/> names the rule when the file as a whole could not be used.</summary>
    internal sealed record Result(IReadOnlyList<Page> Pages, string? Problem, int Excluded, int Dropped);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        MaxDepth = 16,
        NumberHandling = JsonNumberHandling.Strict,
    };

    private sealed class FileDto
    {
        public int Version { get; set; }
        public List<PageDto?>? Pages { get; set; }
    }

    private sealed class PageDto
    {
        public string? Route { get; set; }
        public DateTimeOffset? ScannedAt { get; set; }
        public List<ElementDto?>? Elements { get; set; }
    }

    // No property for a value: whatever a file carries under "value", or any other name, is not read.
    private sealed class ElementDto
    {
        public string? Element { get; set; }
        public string? Role { get; set; }
        public string? Label { get; set; }
        public string? Type { get; set; }
        public List<string?>? Options { get; set; }
        public string? Form { get; set; }
        public string? Selector { get; set; }
        public List<string?>? Roles { get; set; }
        public string? Autocomplete { get; set; }
        public bool Hidden { get; set; }
        public bool Disabled { get; set; }
        public bool Excluded { get; set; }
    }

    /// <summary>Reads a manifest. Never throws for what the file holds: a file it cannot use gives no page and a problem.</summary>
    internal static Result Parse(Stream stream)
    {
        FileDto? file;
        try
        {
            file = JsonSerializer.Deserialize<FileDto>(stream, Json);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            return new Result([], "page.manifest-read", 0, 0);
        }

        if (file is null) return new Result([], "page.manifest-read", 0, 0);
        if (file.Version != FormatVersion) return new Result([], "page.manifest-version", 0, 0);

        var pages = new List<Page>();
        var routes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int excluded = 0, dropped = 0;
        foreach (var page in file.Pages ?? [])
        {
            if (pages.Count >= MentorPageRoutes.MaxPages) { dropped++; continue; }
            if (page is null || !MentorPageRoutes.IsRoute(page.Route) || !routes.Add(page.Route!)) { dropped++; continue; }

            var elements = new List<Element>();
            foreach (var element in page.Elements ?? [])
            {
                if (element is null) { dropped++; continue; }
                if (IsExcluded(element)) { excluded++; continue; }
                if (elements.Count >= MaxElementsPerPage || Read(element) is not { } read) { dropped++; continue; }
                elements.Add(read);
            }
            pages.Add(new Page(page.Route!, page.ScannedAt, elements));
        }
        return new Result(pages, null, excluded, dropped);
    }

    /// <summary>
    /// <c>page.manifest-excluded</c>: a password field, a field of card data (an <c>autocomplete</c> token that starts
    /// with <c>cc-</c>), a hidden or a disabled element, and one marked <c>data-mentor-exclude</c>.
    /// </summary>
    internal static bool IsExcluded(string? type, string? autocomplete, bool hidden, bool disabled, bool excluded)
        => hidden || disabled || excluded
           || string.Equals(type?.Trim(), "password", StringComparison.OrdinalIgnoreCase)
           || (autocomplete ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                                  .Any(t => t.StartsWith("cc-", StringComparison.OrdinalIgnoreCase));

    private static bool IsExcluded(ElementDto e) => IsExcluded(e.Type, e.Autocomplete, e.Hidden, e.Disabled, e.Excluded);

    /// <summary><c>page.manifest-shape</c>: the element as MentorAgent lets it out, or null when a field is not of its shape.</summary>
    /// <summary>
    /// Story 1636 — the manifest of <paramref name="pages"/>, as the scan writes it: version <see cref="FormatVersion"/>,
    /// and per page its route, when it was read and its elements, the fields <see cref="Parse"/> reads and nothing else.
    /// </summary>
    internal static string Serialize(IEnumerable<Page> pages)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteNumber("version", FormatVersion);
            json.WriteStartArray("pages");
            foreach (var page in pages)
            {
                json.WriteStartObject();
                json.WriteString("route", page.Route);
                if (page.ScannedAt is { } at) json.WriteString("scannedAt", at);
                json.WriteStartArray("elements");
                foreach (var e in page.Elements)
                {
                    json.WriteStartObject();
                    json.WriteString("element", e.Tag);
                    Optional(json, "role", e.Role);
                    Optional(json, "label", e.Label);
                    Optional(json, "type", e.Type);
                    if (e.Options.Count > 0) Strings(json, "options", e.Options);
                    Optional(json, "form", e.Form);
                    json.WriteString("selector", e.Selector);
                    if (e.Roles.Count > 0) Strings(json, "roles", e.Roles);
                    json.WriteEndObject();
                }
                json.WriteEndArray();
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray()) + Environment.NewLine;

        static void Optional(Utf8JsonWriter json, string name, string? value)
        {
            if (value is not null) json.WriteString(name, value);
        }

        static void Strings(Utf8JsonWriter json, string name, IReadOnlyList<string> values)
        {
            json.WriteStartArray(name);
            foreach (var value in values) json.WriteStringValue(value);
            json.WriteEndArray();
        }
    }

    /// <summary>
    /// BUG-256 — whether a selector the widget observed carries nothing of the page: steps of a tag, each with at most a
    /// position (<c>:nth-of-type(n)</c>, <c>:nth-child(n)</c>) or the developer's <c>[data-mentor-id="…"]</c>, joined by
    /// <c>&gt;</c>. A list of what is allowed, not of what is not: an id, a class, a name, a test id, an aria-label, a
    /// title, any other attribute or pseudo-class may be a record's, and one left off a list of exclusions would pass.
    /// </summary>
    internal static bool IsObservedSelector(string? selector)
        => selector is { Length: > 0 and <= MaxSelectorLength } && ObservedSelector.IsMatch(selector);

    private const string ObservedStep =
        """[a-z][a-z0-9-]*(?::nth-(?:of-type|child)\([1-9][0-9]{0,3}\)|\[data-mentor-id="(?:[^"\\\r\n]|\\[^\r\n])+"\])?""";

    private static readonly Regex ObservedSelector = new(
        "^" + ObservedStep + "(?: > " + ObservedStep + ")*$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// Story 1636 — an element a scan read, as the manifest keeps it: <see langword="null"/> when MentorAgent never lets
    /// it out (<c>page.manifest-excluded</c>, <paramref name="excluded"/> set) or when its tag, its role, its type or its
    /// selector is not of the format's shape. Texts are cut to the format's limits, their control characters turned to
    /// spaces, and the options and the roles past their limits left out: the scan never writes what the reader drops.
    /// </summary>
    internal static Element? FromScan(string? tag, string? role, string? label, string? type, IEnumerable<string?>? options,
        string? form, string? selector, IEnumerable<string>? roles, string? autocomplete, bool hidden, bool disabled,
        bool excludedMark, out bool excluded)
    {
        excluded = IsExcluded(type, autocomplete, hidden, disabled, excludedMark);
        if (excluded) return null;

        tag = tag?.ToLowerInvariant();
        role = Empty(role?.Trim());
        type = Empty(type?.Trim().ToLowerInvariant());
        if (!IsName(tag, MaxTagLength) || !IsName(role, MaxTextLength, optional: true) || !IsName(type, MaxTextLength, optional: true))
            return null;
        if (!IsText(selector, MaxSelectorLength, required: true)) return null;

        var cleanOptions = (options ?? []).Select(o => Fit(o)).OfType<string>().Take(MaxOptions).ToList();
        var cleanRoles = (roles ?? []).Select(r => Fit(r)).OfType<string>().Distinct(StringComparer.Ordinal).Take(MaxRoles).ToList();
        return new Element(tag!, role, Fit(label), type, cleanOptions, Fit(form), selector!, cleanRoles);
    }

    /// <summary>A text of the page in the format's shape: spaces for control, format and lone surrogate characters, cut to the limit.</summary>
    private static string? Fit(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var clean = new StringBuilder(Math.Min(text.Length, MaxTextLength));
        foreach (var rune in text.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            var keep = rune != Rune.ReplacementChar
                       && category is not (System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format);
            var next = keep ? rune.ToString() : " ";
            if (clean.Length + next.Length > MaxTextLength) break;
            clean.Append(next);
        }
        var fitted = clean.ToString().Trim();
        return fitted.Length == 0 ? null : fitted;
    }

    private static Element? Read(ElementDto e)
    {
        if (!IsName(e.Element, MaxTagLength) || !IsText(e.Selector, MaxSelectorLength, required: true)) return null;
        if (!IsName(e.Role, MaxTextLength, optional: true) || !IsName(e.Type, MaxTextLength, optional: true)) return null;
        if (!IsText(e.Label, MaxTextLength) || !IsText(e.Form, MaxTextLength)) return null;

        var options = e.Options ?? [];
        var roles = e.Roles ?? [];
        if (options.Count > MaxOptions || roles.Count > MaxRoles) return null;
        if (!options.All(o => IsText(o, MaxTextLength, required: true)) || !roles.All(r => IsText(r, MaxTextLength, required: true))) return null;

        return new Element(e.Element!, Empty(e.Role), Empty(e.Label), Empty(e.Type), [.. options!], Empty(e.Form), e.Selector!, [.. roles!]);
    }

    private static string? Empty(string? text) => string.IsNullOrEmpty(text) ? null : text;

    // A tag, a role or a type: letters, digits, '-' and '_'.
    private static bool IsName(string? text, int max, bool optional = false)
    {
        if (string.IsNullOrEmpty(text)) return optional;
        if (text.Length > max) return false;
        foreach (var c in text)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return false;
        return true;
    }

    // Text of the page: no control, format or lone surrogate character, no line break.
    private static bool IsText(string? text, int max, bool required = false)
    {
        if (string.IsNullOrEmpty(text)) return !required;
        if (text.Length > max) return false;
        for (var i = 0; i < text.Length;)
        {
            if (Rune.DecodeFromUtf16(text.AsSpan(i), out var rune, out var used) != System.Buffers.OperationStatus.Done)
                return false;
            if (Rune.GetUnicodeCategory(rune) is System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format)
                return false;
            i += used;
        }
        return true;
    }
}
