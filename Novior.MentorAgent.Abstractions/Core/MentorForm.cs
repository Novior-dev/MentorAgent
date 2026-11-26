using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.JSInterop;
using Novior.MentorAgent.Abstractions.Interfaces;

namespace Novior.MentorAgent.Abstractions.Core;

/// <summary>
/// Story 1319 — a form registered with <see cref="MentorFormExtensions.RegisterForm{TModel}"/>: its fields, the hint the
/// model reads, and the steps a fill runs in the browser with the runner of the recipes (<c>MentorRecipeRunner.js</c>).
/// The selectors are the form's, never the model's: the model gives the values only.
/// </summary>
internal sealed partial class MentorForm
{
    /// <summary>The runner of the steps, a static asset of this package.</summary>
    internal const string Module = "./_content/Novior.MentorAgent.Abstractions/js/MentorRecipeRunner.js";

    internal const string ActionPrefix = "fill_";

    private MentorForm(string name, IReadOnlyList<MentorFormField> fields)
    {
        Name = name;
        Fields = fields;
        Hint = "{ " + string.Join(", ", fields.Select(f => $"{f.Name}: {f.Hint}")) + " }"
             + " - only the fields to fill; the form is filled, never submitted";
    }

    internal string Name { get; }

    internal string ActionName => ActionPrefix + Name;

    internal IReadOnlyList<MentorFormField> Fields { get; }

    /// <summary>What the model reads of the parameter: the fields with their types, and that the form is not sent.</summary>
    internal string Hint { get; }

    internal static MentorForm Create(Type model, string name, MentorFormOptions options)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrEmpty(name) || !NameShape().IsMatch(name) || ActionPrefix.Length + name.Length > MaxActionName)
            throw new ArgumentException(
                $"The form's name '{name}' is not a tool's name: letters, digits, '_' and '-', with 'fill_' at most {MaxActionName} of them.",
                nameof(name));

        var candidates = model.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0 && p.GetMethod?.IsPublic == true && p.SetMethod?.IsPublic == true)
            .Select(p => (Property: p, Kind: KindOf(p.PropertyType)))
            .Where(c => c.Kind is not null)
            .OrderBy(c => c.Property.MetadataToken)
            .ToList();

        // A name the options give that is no field is the application's mistake: said now, not at the first fill.
        foreach (var named in options.Excluded.Concat(options.Sensitive).Concat(options.Selectors.Keys))
            if (!candidates.Any(c => Is(c.Property, named)))
                throw new ArgumentException($"'{named}' is not a field of the form '{name}' ({model.Name}).", nameof(options));

        var fields = new List<MentorFormField>();
        foreach (var (property, kind) in candidates)
        {
            if (options.Excluded.Any(e => Is(property, e))) continue;
            var included = options.Sensitive.Any(s => Is(property, s));
            if (IsSensitive(property) && !included) continue;

            var fieldName = Camel(property.Name);
            var selector = options.Selectors.FirstOrDefault(s => Is(property, s.Key)).Value
                           ?? $"[data-mentor-id=\"{name}.{fieldName}\"]";
            fields.Add(new MentorFormField(fieldName, property.PropertyType, kind!.Value, selector, included));
        }

        if (fields.Count == 0)
            throw new ArgumentException($"The form '{name}' ({model.Name}) has no field the model can fill.", nameof(model));
        return new MentorForm(name, fields);
    }

    // -- Sensitive fields ----------------------------------------------------------

    /// <summary>The words of a property's name that say it holds a secret, a credential or a payment's detail.</summary>
    private static readonly HashSet<string> SensitiveWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "passwd", "pwd", "passcode", "passphrase", "secret", "token", "pin", "cvv", "cvc", "cvv2",
        "iban", "otp", "ssn", "credential", "credentials",
    };

    /// <summary>Two words in a row of a property's name that say the same.</summary>
    private static readonly HashSet<(string, string)> SensitivePairs =
    [
        ("api", "key"), ("access", "key"), ("private", "key"), ("secret", "key"), ("card", "number"), ("credit", "card"),
        ("security", "code"), ("verification", "code"), ("one", "time"),
    ];

    /// <summary>
    /// Whether the property holds what never goes to the model unless the application says so: a password, a secret, a
    /// token, a PIN, a card's number or code, an IBAN, a one-time code. By its attributes, then by the words of its
    /// name (<c>NewPassword</c>, <c>api_key</c>, <c>CardNumber</c>), and by the longer of those words anywhere in a
    /// name written in one piece (<c>newpassword</c>). A short word counts only as a word: <c>Pin</c> and
    /// <c>SimPin</c>, never <c>Shipping</c>.
    /// </summary>
    internal static bool IsSensitive(PropertyInfo property)
    {
        if (property.GetCustomAttributes<DataTypeAttribute>(true).Any(d => d.DataType is DataType.Password or DataType.CreditCard))
            return true;
        if (property.GetCustomAttribute<PasswordPropertyTextAttribute>(true) is { Password: true })
            return true;

        var words = Words(property.Name);
        if (words.Any(SensitiveWords.Contains)) return true;
        for (var i = 0; i + 1 < words.Count; i++)
            if (SensitivePairs.Contains((words[i].ToLowerInvariant(), words[i + 1].ToLowerInvariant())))
                return true;

        var flat = property.Name.ToLowerInvariant();
        return LongSensitiveWords.Any(flat.Contains);
    }

    private static readonly string[] LongSensitiveWords = ["password", "passwd", "passcode", "passphrase", "secret", "token", "iban", "apikey", "cardnumber"];

    /// <summary>The words of a name: <c>NewPIN_code</c> is New, PIN, code.</summary>
    private static List<string> Words(string name) => [.. WordShape().Matches(name).Select(m => m.Value)];

    // -- Filling -------------------------------------------------------------------

    /// <summary>The steps of a fill: one for each field given, in the form's order, with the value checked and written as the page reads it.</summary>
    internal IReadOnlyList<MentorFormStep> Steps(object? parameter)
    {
        var given = Given(parameter);
        if (given.Count == 0)
            throw new ArgumentException($"No field to fill was given. The fields of the form '{Name}' are: {FieldList}.");

        foreach (var key in given.Keys)
            if (!Fields.Any(f => string.Equals(f.Name, key, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"'{key}' is not a field of the form '{Name}'. Its fields are: {FieldList}.");

        var steps = new List<MentorFormStep>();
        foreach (var field in Fields)
        {
            var value = given.FirstOrDefault(g => string.Equals(g.Key, field.Name, StringComparison.OrdinalIgnoreCase));
            if (value.Key is null) continue;
            steps.Add(new MentorFormStep(field.Kind.ToString().ToLowerInvariant(), field.Selector, field.Write(value.Value),
                field.AllowSensitive) { Field = field.Name });
        }
        return steps;
    }

    /// <summary>Fills the form in the page: the steps run in the browser, and a step not done fails the action.</summary>
    internal async Task FillAsync(IJSRuntime js, object? parameter)
    {
        var steps = Steps(parameter);
        var outcome = await RunAsync(js, steps).ConfigureAwait(false);
        if (outcome.Done) return;
        var field = outcome.Step >= 0 && outcome.Step < steps.Count ? steps[outcome.Step].Field : "?";
        throw new InvalidOperationException(outcome.Reason == "sensitive"
            ? $"The form '{Name}' was not filled: '{field}' is a password field, which the assistant never fills. The user fills it."
            : $"The form '{Name}' was not filled: the field '{field}' is {outcome.Reason ?? "missing"} in the page. The fields before it are filled.");
    }

    /// <summary>Runs the steps in the page with the runner's module, let go afterwards: a form's, or a grid's (1319 B).</summary>
    internal static async Task<MentorFormOutcome> RunAsync<TStep>(IJSRuntime js, IReadOnlyList<TStep> steps)
    {
        var module = await js.InvokeAsync<IJSObjectReference>("import", Module).ConfigureAwait(false);
        try
        {
            return await module.InvokeAsync<MentorFormOutcome>("run", steps).ConfigureAwait(false);
        }
        finally
        {
            try { await module.DisposeAsync().ConfigureAwait(false); }
            catch (JSDisconnectedException) { }
            catch (ObjectDisposedException) { }
        }
    }

    private string FieldList => string.Join(", ", Fields.Select(f => f.Name));

    /// <summary>The fields the model gave: an object, or an object written as a text.</summary>
    private Dictionary<string, JsonElement> Given(object? parameter)
    {
        JsonElement element;
        switch (parameter)
        {
            case JsonElement e when e.ValueKind == JsonValueKind.String && e.GetString() is { } text && text.TrimStart().StartsWith('{'):
                element = Parse(text);
                break;
            case JsonElement e:
                element = e;
                break;
            case string text when text.TrimStart().StartsWith('{'):
                element = Parse(text);
                break;
            case null:
                element = default;
                break;
            default:
                element = JsonSerializer.SerializeToElement(parameter);
                break;
        }

        if (element.ValueKind != JsonValueKind.Object)
            throw new ArgumentException($"Give the fields to fill as one object, {{ field: value }}. The fields of the form '{Name}' are: {FieldList}.");
        return element.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.OrdinalIgnoreCase);
    }

    private JsonElement Parse(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new ArgumentException($"Give the fields to fill as one object, {{ field: value }}. The fields of the form '{Name}' are: {FieldList}.");
        }
    }

    // -- Types -----------------------------------------------------------------------

    internal const int MaxActionName = 64;

    /// <summary>What a step does to a field of the type; null, the type is no field.</summary>
    internal static MentorFormFieldKind? KindOf(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        if (t == typeof(bool)) return MentorFormFieldKind.Check;
        if (t.IsEnum) return MentorFormFieldKind.Choose;
        if (t == typeof(string) || t == typeof(Guid) || t == typeof(DateTime) || t == typeof(DateTimeOffset)
            || t == typeof(DateOnly) || t == typeof(TimeOnly) || MentorFormField.IsNumber(t))
            return MentorFormFieldKind.Set;
        return null;
    }

    private static bool Is(PropertyInfo property, string named)
        => string.Equals(property.Name, named, StringComparison.OrdinalIgnoreCase);

    internal static string Camel(string name) => char.ToLowerInvariant(name[0]) + name[1..];

    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    internal static partial Regex NameShape();

    [GeneratedRegex("[A-Z]{2,}(?=[A-Z][a-z]|[0-9]|_|-|$)|[A-Z]?[a-z]+|[A-Z]+|[0-9]+")]
    private static partial Regex WordShape();
}

/// <summary>What a fill's step does to a field.</summary>
internal enum MentorFormFieldKind
{
    /// <summary>Writes the value, as typing would.</summary>
    Set,
    /// <summary>Chooses the option whose label is the value.</summary>
    Choose,
    /// <summary>Checks or unchecks the box.</summary>
    Check,
}

/// <summary>A field of a form: its name as the model sees it, its type, its step and its selector.</summary>
internal sealed record MentorFormField(string Name, Type Type, MentorFormFieldKind Kind, string Selector, bool AllowSensitive)
{
    private Type Underlying => Nullable.GetUnderlyingType(Type) ?? Type;

    private bool IsNullable => !Type.IsValueType || System.Nullable.GetUnderlyingType(Type) is not null;

    /// <summary>The field's type as the hint says it.</summary>
    internal string Hint => Underlying == typeof(DateOnly) ? "string (date, yyyy-MM-dd)" + Optional
        : Underlying == typeof(TimeOnly) ? "string (time, HH:mm)" + Optional
        : MentorParameterHint.For(Type);

    private string Optional => System.Nullable.GetUnderlyingType(Type) is null ? "" : "?";

    /// <summary>The value the model gave, checked against the field's type and written as the page reads it.</summary>
    internal string Write(JsonElement value)
    {
        var t = Underlying;
        if (value.ValueKind == JsonValueKind.Null)
        {
            if (Kind == MentorFormFieldKind.Set && IsNullable) return "";
            throw Wrong(value);
        }

        if (Kind == MentorFormFieldKind.Check)
        {
            if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.GetBoolean() ? "true" : "false";
            if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var b)) return b ? "true" : "false";
            throw Wrong(value);
        }

        var text = value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText();

        if (Kind == MentorFormFieldKind.Choose)
        {
            var match = Enum.GetNames(t).FirstOrDefault(n => string.Equals(n, text.Trim(), StringComparison.OrdinalIgnoreCase));
            return match ?? throw Wrong(value);
        }

        if (IsNumber(t))
        {
            if (value.ValueKind is not (JsonValueKind.Number or JsonValueKind.String)) throw Wrong(value);
            if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) throw Wrong(value);
            if (IsInteger(t) && number != decimal.Truncate(number)) throw Wrong(value);
            return number.ToString(CultureInfo.InvariantCulture);
        }

        if (value.ValueKind != JsonValueKind.String && t != typeof(string)) throw Wrong(value);
        if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) throw Wrong(value);

        if (t == typeof(Guid))
            return Guid.TryParse(text, out var guid) ? guid.ToString() : throw Wrong(value);
        if (t == typeof(DateOnly))
            return DateOnly.TryParse(text, CultureInfo.InvariantCulture, out var date)
                ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : throw Wrong(value);
        if (t == typeof(TimeOnly))
            return TimeOnly.TryParse(text, CultureInfo.InvariantCulture, out var time)
                ? time.ToString(time.Second == 0 ? "HH:mm" : "HH:mm:ss", CultureInfo.InvariantCulture) : throw Wrong(value);
        if (t == typeof(DateTime) || t == typeof(DateTimeOffset))
            return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ? text : throw Wrong(value);
        return text;
    }

    private ArgumentException Wrong(JsonElement value)
        => new($"The value {value.GetRawText()} is not one the field '{Name}' takes: {Hint}.");

    internal static bool IsNumber(Type t) => IsInteger(t) || t == typeof(float) || t == typeof(double) || t == typeof(decimal);

    private static bool IsInteger(Type t)
        => t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte)
           || t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort) || t == typeof(sbyte);
}

/// <summary>A step of a fill as the browser runs it (<c>MentorRecipeRunner.js</c>): <c>allowSensitive</c> lets it write a password field.</summary>
internal sealed record MentorFormStep(string Kind, string Selector, string? Value, bool AllowSensitive)
{
    /// <summary>The field the step fills, for the message of a step not done; not sent to the browser.</summary>
    internal string Field { get; init; } = "";
}

/// <summary>What the browser answers: done, or the step not done and why.</summary>
internal sealed record MentorFormOutcome(bool Done, int Step, string? Reason);
