using System.Text.Json;
using System.Text.Json.Serialization;

namespace Novior.MentorAgent.Abstractions.Core;

/// <summary>
/// A <see cref="MentorLanguage"/> as a page sends it: the name (<c>"Italian"</c>) or the ISO code (<c>"it"</c>), in
/// any case. Nothing else is a language. <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/> alone also takes
/// <c>"5"</c>, and a page that sent a number it did not mean would get Portuguese.
/// </summary>
internal static class MentorLanguageWire
{
    public static bool TryParse(string? value, out MentorLanguage language)
    {
        language = default;
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text)) return false;

        foreach (var candidate in Enum.GetValues<MentorLanguage>())
        {
            if (string.Equals(candidate.ToString(), text, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.ToIsoCode(), text, StringComparison.OrdinalIgnoreCase))
            {
                language = candidate;
                return true;
            }
        }
        return false;
    }
}

/// <summary>
/// <see cref="MentorUserMessage.Language"/> on the hub: written as the name, read by <see cref="MentorLanguageWire"/>.
/// A value that is not a language, a number included, is read as <see langword="null"/> rather than failing the
/// invocation: the turn is answered in the application's language.
/// </summary>
internal sealed class MentorLanguageWireConverter : JsonConverter<MentorLanguage?>
{
    public override bool HandleNull => true;

    public override MentorLanguage? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return MentorLanguageWire.TryParse(reader.GetString(), out var language) ? language : null;

        reader.Skip();
        return null;
    }

    public override void Write(Utf8JsonWriter writer, MentorLanguage? value, JsonSerializerOptions options)
    {
        if (value is { } language) writer.WriteStringValue(language.ToString());
        else writer.WriteNullValue();
    }
}
