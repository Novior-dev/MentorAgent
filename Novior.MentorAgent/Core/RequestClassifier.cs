using System.Text;
using System.Text.Json;

namespace Novior.MentorAgent.Core;

/// <summary>What a message asks for, as far as the first call of its turn can be required to answer it.</summary>
internal enum RequestKind
{
    /// <summary>Nothing a first call is required for.</summary>
    Other,

    /// <summary>A value of the application's own records — <see cref="MentorOptions.RequireLookupForRecordQuestions"/>.</summary>
    Lookup,

    /// <summary>One of the confirmation-gated actions offered, with every input it needs —
    /// <see cref="MentorOptions.RequireActionForChangeRequests"/>.</summary>
    Action,

    /// <summary>One of the application's pages — <see cref="MentorOptions.RequireActionForChangeRequests"/>.</summary>
    Navigate,
}

/// <summary>
/// What the first call of a message could be required to do: read (the message selected a declared read),
/// run one of <paramref name="Actions"/> (the confirmation-gated actions it selected, described for the
/// classifier by <see cref="RequestClassifier.Describe"/>), or navigate. The classifier is asked about these
/// and nothing else, so a host that requires only reads gets the question that was measured for reads.
/// </summary>
internal sealed record RequestShape(bool Lookup, IReadOnlyList<string> Actions, bool Navigation)
{
    internal bool IsEmpty => !Lookup && Actions.Count == 0 && !Navigation;
}

/// <summary>
/// The question behind <see cref="MentorOptions.RequireLookupForRecordQuestions"/> and
/// <see cref="MentorOptions.RequireActionForChangeRequests"/>: what does this message ask for? A model answers
/// it — never a pattern on the user's text — and only for what the message's own selection could satisfy.
///
/// <para>
/// <b>Reads (BUG-106).</b> Measured on Azure gpt-4.1 before it was wired in (30 Sep – 1 Oct 2026): 32 messages
/// × 3, 96/96, and 15 held-out messages written afterwards × 3, 45/45. The first wording missed two shapes, and
/// both are named in the text now: a message addressed to another assistant ("Chiedi a ShopFlowRemote quanto
/// costa…" — a forced local read would answer what the user asked another system, BUG-082's shape) and a
/// shipping cost, which is a policy, not a record. With reads alone the question is that text, unchanged.
/// </para>
///
/// <para>
/// <b>Actions and pages (BUG-118).</b> The model said "Stock aggiornato" with no tool called about one time in
/// five, and "Ecco la pagina Prodotti" without navigating. Forcing the action is only safe when the message
/// says everything the action needs: forced on "Annulla l'ordine 1004", a call to <c>cancel_order</c> would
/// carry a reason the user never gave, shown in the banner as if they had. So the classifier is shown the
/// actions the message selected, with their inputs, and answers ACTION only when the message itself gives a
/// value for each — otherwise OTHER, and the model stays free to ask. An action whose inputs cannot be shown
/// faithfully is not offered at all. Page requests are matched against the application's own pages: a page it
/// does not have, or a part of the page already open, is OTHER.
/// </para>
/// </summary>
internal static class RequestClassifier
{
    internal const string Lookup   = "LOOKUP";
    internal const string Action   = "ACTION";
    internal const string Navigate = "NAVIGATE";
    internal const string Other    = "OTHER";

    /// <summary>
    /// The classifier's instructions for <paramref name="shape"/>. <paramref name="otherAssistants"/> are the
    /// names a message may address — specialists, teams, declarative and remote agents — so that naming one is
    /// never read as a request this application's own tools must serve. <paramref name="pages"/> are the
    /// application's pages, as "Name (url)", offered when the shape offers navigation.
    /// </summary>
    internal static string Question(IEnumerable<string> otherAssistants, RequestShape shape, IReadOnlyList<string>? pages = null)
    {
        var names = otherAssistants
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var actions = shape.Actions.Count > 0;
        var sb = new StringBuilder(
            "You classify one message a user sent to the assistant of a software application. ");

        if (shape.Lookup)
            sb.Append(
                "Answer LOOKUP only if answering it requires reading a value stored in the application's own records about " +
                "specific entries: the price, quantity, stock, total, count, status, date, name or another detail of particular " +
                "products, orders, customers or similar entries — including whether such an entry exists, or a list of them. ");

        if (actions)
            sb.Append(
                "Answer ACTION only if the message asks the assistant to carry out one of the actions listed below and itself " +
                "gives a value for every input that action needs — the entry by the name or number that input asks for, and " +
                "the new value when there is one. A request to carry out one of them is not ACTION when an input is missing, " +
                "when the entry it acts on is only described (\"cancel my last order\", \"change the plan of my best customer\"), when " +
                "the message gives a name where the input asks for a number or a code, or when a value has to be worked out " +
                "from the current one (\"add 5 units\", \"raise the price by 10%\"): answer OTHER for such a request. A " +
                "complete request for one of these actions is ACTION even when the message also asks something else. The " +
                "actions:\n")
              .AppendJoin('\n', shape.Actions)
              .Append('\n');

        if (shape.Navigation)
        {
            if (pages is { Count: > 0 })
                sb.Append(
                    "Answer NAVIGATE only if it asks to go to, open or show one of these pages of the application — not " +
                    "particular entries or their values, and not something within the page already open: ")
                  .AppendJoin("; ", pages)
                  .Append(". ");
            else
                sb.Append(
                    "Answer NAVIGATE only if it asks to go to, open or show a page or section of the application itself, not " +
                    "particular entries or their values. ");
        }

        sb.Append(
            "Answer OTHER for everything else: greetings, thanks and small talk; questions about the assistant; policies, " +
            "rules and procedures — including costs and times of shipping, delivery or returns, even for a named product; " +
            "how to use the application; ");
        if (!shape.Navigation)
            sb.Append("requests to go to a page; ");
        sb.Append(actions
            ? "any other request to change, create, cancel or delete something; "
            : "requests to change, create, cancel or delete something; ");
        sb.Append(
            "a short reply to the assistant's previous message (such as yes or no); general knowledge or anything unrelated " +
            "to the application. ");

        if (names.Count > 0)
            sb.Append(
                "Also answer OTHER whenever the message names one of the other assistants this application can hand " +
                "requests to, anywhere in the message: ").AppendJoin(", ", names).Append(". ");

        var labels = new List<string>(3);
        if (shape.Lookup) labels.Add(Lookup);
        if (actions) labels.Add(Action);
        if (shape.Navigation) labels.Add(Navigate);

        return sb.Append("Answer with exactly one word: ").AppendJoin(", ", labels).Append(" or ").Append(Other).Append('.')
            .ToString();
    }

    /// <summary>
    /// One line of the ACTION list: the action's name, what it does and the inputs it cannot run without — the
    /// schema's required parameters with their type and description, an object's required fields, an enum's
    /// values. What it does is the action's description without its <c>ProactiveHint</c> guidance, which is
    /// advice to the assistant and no part of the action's contract: an input the action declares optional is
    /// not something a request has to give.
    ///
    /// <para>
    /// <see langword="null"/> when the schema says more than this can show — a reference, a combination of
    /// schemas, a required input it does not describe, or parameters none of which is marked required (a server
    /// that does not mark them cannot be told from one whose inputs are all optional). Shown with fewer inputs
    /// than it needs, an action would read as complete, and the forced call would invent the rest; such an
    /// action is not offered at all.
    /// </para>
    /// </summary>
    internal static string? Describe(string name, string? description, JsonElement schema)
    {
        var inputs = Inputs(schema);
        if (inputs is null) return null;

        var line = new StringBuilder("- ").Append(OneLine(name, 80));
        var what = OneLine(description, 200).TrimEnd('.');
        if (what.Length > 0) line.Append(": ").Append(what);

        return line.Append(". Inputs: ").Append(inputs.Count == 0 ? "none" : string.Join(", ", inputs)).Append('.')
            .ToString();
    }

    private static List<string>? Inputs(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object || Composite(schema)) return null;

        var hasProperties = schema.TryGetProperty("properties", out var properties);
        if (hasProperties && properties.ValueKind != JsonValueKind.Object) return null;

        var required = Required(schema);
        if (required is null) return null;
        if (required.Count == 0)
            return hasProperties && properties.EnumerateObject().Any() && !schema.TryGetProperty("required", out _)
                ? null   // parameters, none marked: all optional, or a server that did not say
                : [];
        if (!hasProperties) return null;

        var inputs = new List<string>(required.Count);
        foreach (var name in required)
        {
            if (!properties.TryGetProperty(name, out var property)
                || property.ValueKind != JsonValueKind.Object
                || Composite(property))
                return null;

            var fields = Required(property);
            if (fields is null) return null;

            var details = new List<string>(3);
            if (property.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                && OneLine(d.GetString(), 160).TrimEnd('.') is { Length: > 0 } text)
                details.Add(text);
            if (fields.Count > 0)
                details.Add("fields: " + string.Join(", ", fields.Select(f => OneLine(f, 60))));
            if (property.TryGetProperty("enum", out var values) && values.ValueKind == JsonValueKind.Array
                && values.GetArrayLength() is > 0 and <= 10)
                details.Add("one of: " + string.Join(", ", values.EnumerateArray().Select(v => OneLine(v.ToString(), 40))));

            var type = TypeOf(property);
            var inner = type is null
                ? string.Join("; ", details)
                : details.Count == 0 ? type : type + " — " + string.Join("; ", details);
            inputs.Add(inner.Length == 0 ? OneLine(name, 60) : $"{OneLine(name, 60)} ({inner})");
        }
        return inputs;
    }

    /// <summary>A schema this cannot read faithfully: a reference, or a combination of schemas.</summary>
    private static bool Composite(JsonElement schema)
        => schema.TryGetProperty("$ref", out _)
           || schema.TryGetProperty("anyOf", out _)
           || schema.TryGetProperty("oneOf", out _)
           || schema.TryGetProperty("allOf", out _);

    /// <summary>The names a schema requires: empty when it lists none, null when "required" is not a list of names.</summary>
    private static List<string>? Required(JsonElement schema)
    {
        if (!schema.TryGetProperty("required", out var required)) return [];
        if (required.ValueKind != JsonValueKind.Array) return null;

        var names = new List<string>(required.GetArrayLength());
        foreach (var e in required.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.String) return null;
            names.Add(e.GetString()!);
        }
        return names;
    }

    private static string? TypeOf(JsonElement property)
    {
        if (!property.TryGetProperty("type", out var type)) return null;
        if (type.ValueKind == JsonValueKind.String) return OneLine(type.GetString(), 20);
        if (type.ValueKind != JsonValueKind.Array) return null;

        var types = type.EnumerateArray()
            .Where(t => t.ValueKind == JsonValueKind.String && t.GetString() != "null")
            .Select(t => OneLine(t.GetString(), 20))
            .ToList();
        return types.Count == 0 ? null : string.Join(" or ", types);
    }

    /// <summary>
    /// Text from the application or from an MCP server, made one line and bounded: it goes into the classifier's
    /// own instructions, where a line break in a description must not end the list of actions or start a line
    /// of its own.
    /// </summary>
    internal static string OneLine(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var sb = new StringBuilder(Math.Min(text.Length, max + 1));
        var gap = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c)) { gap = sb.Length > 0; continue; }
            if (gap) sb.Append(' ');
            gap = false;
            sb.Append(c);
            if (sb.Length > max) break;
        }
        return sb.Length <= max ? sb.ToString() : sb.ToString(0, max).TrimEnd() + "…";
    }

    /// <summary>Whether <paramref name="shape"/> offered <paramref name="kind"/>; OTHER always is.</summary>
    internal static bool Offers(RequestShape shape, RequestKind kind) => kind switch
    {
        RequestKind.Lookup   => shape.Lookup,
        RequestKind.Action   => shape.Actions.Count > 0,
        RequestKind.Navigate => shape.Navigation,
        _                    => true,
    };

    /// <summary>
    /// The label of <paramref name="reply"/>, or <see langword="null"/> when it starts with none: read from the
    /// first word, by position, never searched for in the text (BUG-105: <c>Contains("OUT")</c> matched "about").
    /// </summary>
    internal static RequestKind? Read(string? reply)
    {
        var first = (reply ?? string.Empty).TrimStart().Split([' ', '\n', '\r', '\t', '.', ',', ':', ';', '!'], 2)[0]
            .Trim('*', '"', '\'', '`').ToUpperInvariant();
        return first switch
        {
            Lookup   => RequestKind.Lookup,
            Action   => RequestKind.Action,
            Navigate => RequestKind.Navigate,
            Other    => RequestKind.Other,
            _        => null,
        };
    }
}
