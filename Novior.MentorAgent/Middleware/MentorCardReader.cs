using System.Text.Json;
using System.Text.Json.Serialization;

namespace Novior.MentorAgent.Middleware;

/// <summary>
/// Recognises generative-UI cards (#14 L2) in a tool result.
///
/// <para>
/// This exists because of a subtlety that makes the feature silently do nothing if you miss it:
/// <c>AIFunctionFactory</c> marshals a tool's return value <b>through JSON</b>. A method declared as
/// returning <c>IEnumerable&lt;MentorCard&gt;</c> reaches the middleware as a
/// <see cref="JsonElement"/>, so <c>result is MentorCard</c> never matches. The cards then travel on
/// to the model as raw JSON and it describes them in a Markdown table — a correct answer, in the
/// wrong form, with nothing in the logs to suggest a bug.
/// </para>
///
/// <para>
/// Detection is therefore structural, and deliberately strict: every ordinary tool result arrives as
/// a <see cref="JsonElement"/> too, and a false positive would swallow a real answer and render an
/// empty card in its place. An object counts as a card only if it carries a non-empty string
/// <c>kind</c>; an array counts only if it is non-empty and <i>every</i> element does.
/// </para>
/// </summary>
internal static class MentorCardReader
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>
    /// Returns the cards in <paramref name="element"/>, or <see langword="null"/> if it is not a
    /// card payload.
    /// </summary>
    public static IReadOnlyList<MentorCard>? TryRead(JsonElement element)
    {
        try
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object when HasKind(element):
                    var single = element.Deserialize<MentorCard>(Options);
                    return single is null ? null : [single];

                case JsonValueKind.Array:
                    var count = element.GetArrayLength();
                    if (count == 0) return null;
                    foreach (var item in element.EnumerateArray())
                        if (item.ValueKind != JsonValueKind.Object || !HasKind(item))
                            return null;

                    var many = element.Deserialize<List<MentorCard>>(Options);
                    return many is { Count: > 0 } ? many : null;

                default:
                    return null;
            }
        }
        catch (JsonException)
        {
            // Shaped like a card, not deserialisable as one. Leave it to the model as-is rather
            // than losing the tool's answer.
            return null;
        }
    }

    /// <summary><c>kind</c> is required and must be a non-empty string — that is what makes it a card.</summary>
    private static bool HasKind(JsonElement obj)
        => obj.TryGetProperty("kind", out var kind)
           && kind.ValueKind == JsonValueKind.String
           && !string.IsNullOrWhiteSpace(kind.GetString());
}
