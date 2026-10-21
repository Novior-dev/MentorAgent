using System.Text.Json;

namespace MentorAgentDemo.Localization;

/// <summary>A text in English and in Italian.</summary>
public sealed record Bilingual(string En, string It)
{
    public string In(bool italian) => italian ? It : En;
}

/// <summary>Lines of one file of the demo, in the order they appear there.</summary>
public sealed record CodeLines(string File, IReadOnlyList<string> Lines);

/// <summary>One feature on the What to try page: what it does, a question to ask, and the code that turns it on.</summary>
public sealed record TryFeature(
    string Id, Bilingual Title, Bilingual What, Bilingual? Ask, string? Page, IReadOnlyList<CodeLines> Code);

/// <summary>
/// The features of the What to try page, read from TryCatalog.json, which is embedded in the assembly. A test of the
/// repository checks that every line of code the page quotes is in the file it names, in that order, so the page
/// cannot show code the demo no longer has.
/// </summary>
public static class TryCatalog
{
    public static IReadOnlyList<TryFeature> Features { get; } = Load();

    private static List<TryFeature> Load()
    {
        using var stream = typeof(TryCatalog).Assembly.GetManifestResourceStream("MentorAgentDemo.TryCatalog.json")
            ?? throw new InvalidOperationException("TryCatalog.json is not embedded in the demo.");
        return JsonSerializer.Deserialize<List<TryFeature>>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? [];
    }
}
