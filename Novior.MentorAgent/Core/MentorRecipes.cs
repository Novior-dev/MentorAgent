using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Novior.MentorAgent.Abstractions.Core;

namespace Novior.MentorAgent.Core;

/// <summary>
/// Story 1638 (L4) — a recipe: a sequence of steps on one page that the widget runs in the user's browser, offered to
/// the model as one of the page's actions (<c>recipe.offer</c>). The selectors are the recipe's, never the model's: the
/// model gives the values of the parameters only. A recipe is known by its route and its name. Story 1639: the public
/// source is <see cref="IMentorRecipeSource"/>, whose definitions become these.
/// </summary>
internal sealed record MentorRecipe
{
    /// <summary>At most this many steps in a recipe.</summary>
    internal const int MaxSteps = MentorRecipeLimits.MaxSteps;

    /// <summary>The tool's name the model calls.</summary>
    internal required string Name { get; init; }

    /// <summary>What the recipe does, for the model and for the confirmation.</summary>
    internal required string Description { get; init; }

    /// <summary>The route of the page it runs on, as the router writes it ("/orders/{id:int}").</summary>
    internal required string Route { get; init; }

    internal IReadOnlyList<MentorRecipeParameter> Parameters { get; init; } = [];

    internal required IReadOnlyList<MentorRecipeStep> Steps { get; init; }

    /// <summary>The roles a user needs to run it; none, anyone.</summary>
    internal IReadOnlyList<string> Roles { get; init; } = [];

    /// <summary>Whether the user confirms it before it runs, with the values of its parameters shown.</summary>
    internal bool RequiresConfirmation { get; init; }

    /// <summary>The full type name of the <see cref="IMentorRecipeSource"/> that gave it; null for one added in code.</summary>
    internal string? Source { get; init; }

    /// <summary>Story 1639 — who wins over a UI action of the page with its name: its source's answer; the code, for one added in code.</summary>
    internal MentorRecipePrecedence Precedence { get; init; } = MentorRecipePrecedence.Code;

    /// <summary>Story 1639 — a source's definition as a recipe, as it was given: <see cref="MentorRecipeRegistry.Problem"/> checks it.</summary>
    internal static MentorRecipe From(MentorRecipeDefinition definition, string source,
        MentorRecipePrecedence precedence = MentorRecipePrecedence.Code) => new()
    {
        Name = definition.Name ?? "",
        Description = definition.Description ?? "",
        Route = definition.Route ?? "",
        Parameters = [.. (definition.Parameters ?? []).Select(p => p is null
            ? new MentorRecipeParameter("", "")
            : new MentorRecipeParameter(p.Name ?? "", p.Description ?? "", p.Type))],
        Steps = [.. (definition.Steps ?? []).Select(s => s is null
            ? new MentorRecipeStep((MentorRecipeStepKind)(-1), "")
            : new MentorRecipeStep(s.Kind, s.Selector ?? "", s.Value) { Cell = s.Cell, Target = s.Target, Filter = s.Filter })],
        Roles = [.. definition.Roles ?? []],
        RequiresConfirmation = definition.RequiresConfirmation,
        Source = source,
        Precedence = precedence,
    };
}

/// <summary>A value the model gives a recipe.</summary>
internal sealed record MentorRecipeParameter(string Name, string Description, MentorRecipeParameterType Type = MentorRecipeParameterType.String);

/// <summary>The type of a value the model gives a recipe; the value is checked against it before anything runs.</summary>
public enum MentorRecipeParameterType
{
    /// <summary>A text, written as given.</summary>
    String,
    /// <summary>A number, written with a '.' for the decimals.</summary>
    Number,
    /// <summary><c>true</c> or <c>false</c>.</summary>
    Boolean,
}

/// <summary>What a step of a recipe does to the element its selector finds.</summary>
public enum MentorRecipeStepKind
{
    /// <summary>Writes the value into a field, as typing would (<c>input</c> and <c>change</c>).</summary>
    Set,
    /// <summary>Chooses the option with the value as its label: a select, or an ARIA combobox.</summary>
    Choose,
    /// <summary>Checks a checkbox or a radio, or unchecks it with the value "false".</summary>
    Check,
    /// <summary>Clicks the element.</summary>
    Click,
    /// <summary>Waits for the element to appear.</summary>
    Wait,
    /// <summary>
    /// Finds the row whose <see cref="MentorRecipeStepDefinition.Cell"/> reads the value, among the rows the selector
    /// finds, using the page's filter first if the step has one, then scrolling the rows' container; then clicks the
    /// step's <see cref="MentorRecipeStepDefinition.Target"/> inside that row. Nothing inside an element marked
    /// <c>data-mentor-exclude</c> is read.
    /// </summary>
    Row,
}

/// <summary>
/// One step. <see cref="Value"/> is a text, or a parameter written <c>{name}</c>, which is the whole value: a step never
/// mixes a parameter with other text. In a text, <c>{{</c> and <c>}}</c> are a brace.
/// </summary>
internal sealed record MentorRecipeStep(MentorRecipeStepKind Kind, string Selector, string? Value = null)
{
    /// <summary>A row step: the cell, inside a row, whose text is the row's key (<c>td:nth-child(1)</c>).</summary>
    internal string? Cell { get; init; }

    /// <summary>A row step: what to click inside the row found; none, the row is only brought into view.</summary>
    internal string? Target { get; init; }

    /// <summary>A row step: the page's own filter field, given the key before the rows are scrolled.</summary>
    internal string? Filter { get; init; }
}

/// <summary>What the browser answers: done, or the step that could not be done and why. Never a value of the page.</summary>
internal sealed record MentorRecipeOutcome(bool Done, int Step, string? Reason);

/// <summary>A step as the browser runs it, with its parameter already replaced by the model's value.</summary>
internal sealed record MentorRecipeStepCall(string Kind, string Selector, string? Value,
    string? Cell = null, string? Target = null, string? Filter = null);

/// <summary>Where a recipe runs: the user's browser, through the scope's JavaScript runtime or the hub.</summary>
internal interface IMentorRecipeRunner
{
    Task<MentorRecipeOutcome> RunAsync(IReadOnlyList<MentorRecipeStepCall> steps, CancellationToken ct);

    /// <summary>
    /// The path of the page the runner acts on, when the runner knows it and the scope's <c>NavigationManager</c> does
    /// not: on the hub, the page the WebAssembly client said it shows. Null: the scope's own page, if it has one.
    /// </summary>
    string? PagePath => null;
}

/// <summary>A recipe that could not be done (<c>recipe.element-missing</c>): it is turned off, and the gate says so.</summary>
internal sealed class MentorRecipeNotDoneException(string recipe, int step, string reason)
    : Exception($"The recipe '{recipe}' could not be done: step {step + 1}, the element is {reason}. It is now off; do not try it again.")
{
    internal string Recipe { get; } = recipe;
    internal int Step { get; } = step;
    internal string Reason { get; } = reason;
}

/// <summary>
/// Story 1638 — the application's recipes, in memory: those added in code, and those turned off because a step could not
/// be done. A recipe turned off is not offered again until the application restarts. Story 1639: a recipe is known by its
/// route and its name, and the recipes of a session's sources are offered with those added in code; one turned off stays
/// off in that version, and a new version is on.
/// </summary>
internal sealed class MentorRecipeRegistry
{
    private readonly ConcurrentDictionary<(string Route, string Name), MentorRecipe> _recipes = new();
    private readonly ConcurrentDictionary<(string Route, string Name, string Version), string> _off = new();
    private static readonly ConditionalWeakTable<MentorRecipe, string> Versions = new();

    private static (string Route, string Name) Key(MentorRecipe recipe) => (recipe.Route, recipe.Name);

    /// <summary>
    /// Story 1639 — what a recipe turned off is known by: its route, its name and its version, a digest of everything it
    /// does. A version changed in anything — a new one the console approved — is a recipe that was never turned off.
    /// </summary>
    private static (string Route, string Name, string Version) OffKey(MentorRecipe recipe)
        => (recipe.Route, recipe.Name, Versions.GetValue(recipe, Version));

    /// <summary>The digest of what a recipe does: not where it came from, nor who wins over a UI action.</summary>
    internal static string Version(MentorRecipe recipe) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
    {
        recipe.Name,
        recipe.Description,
        recipe.Route,
        Parameters = recipe.Parameters.Select(p => new { p.Name, p.Description, p.Type }),
        Steps = recipe.Steps.Select(s => new { s.Kind, s.Selector, s.Value, s.Cell, s.Target, s.Filter }),
        recipe.Roles,
        recipe.RequiresConfirmation,
    })));

    /// <summary>Adds a recipe, checked as a source's is (<see cref="Problem"/>).</summary>
    internal void Add(MentorRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        Check(recipe);
        _recipes[Key(recipe)] = recipe;
        _off.TryRemove(OffKey(recipe), out _);
    }

    /// <summary>Throws an <see cref="ArgumentException"/> saying what does not hold in the recipe.</summary>
    internal static void Check(MentorRecipe recipe)
    {
        if (Problem(recipe) is { } problem)
            throw new ArgumentException(problem.Detail, nameof(recipe));
    }

    /// <summary>
    /// Story 1639 — what does not hold in a recipe, as a <see cref="MentorRecipeRejectionReasons"/> code and what to correct;
    /// null when it holds. The limits are <see cref="MentorRecipeLimits"/>.
    /// </summary>
    internal static (string Code, string Detail)? Problem(MentorRecipe recipe)
    {
        var label = string.IsNullOrEmpty(recipe.Name) ? "(unnamed)" : recipe.Name;

        if (!IsName(recipe.Name))
            return (MentorRecipeRejectionReasons.InvalidName,
                $"A recipe's name is 1 to {MentorRecipeLimits.MaxNameLength} letters, digits, '_' and '-'.");
        if (string.IsNullOrWhiteSpace(recipe.Description))
            return (MentorRecipeRejectionReasons.InvalidDefinition, $"The recipe '{label}' has no description.");
        if (recipe.Description.Length > MentorRecipeLimits.MaxDescriptionLength)
            return TooLong(label, "description", MentorRecipeLimits.MaxDescriptionLength);
        if (recipe.Route.Length > MentorRecipeLimits.MaxRouteLength)
            return TooLong(label, "route", MentorRecipeLimits.MaxRouteLength);
        if (!MentorPageRoutes.IsRoute(recipe.Route) || recipe.Route.IndexOfAny(['?', '#']) >= 0)
            return (MentorRecipeRejectionReasons.InvalidRoute, $"The recipe '{label}' has no route of a page.");

        if (recipe.Roles.Count > MentorRecipeLimits.MaxRoles)
            return (MentorRecipeRejectionReasons.TooLong, $"The recipe '{label}' has more than {MentorRecipeLimits.MaxRoles} roles.");
        foreach (var role in recipe.Roles)
        {
            if (string.IsNullOrWhiteSpace(role))
                return (MentorRecipeRejectionReasons.InvalidDefinition, $"A role of the recipe '{label}' is empty.");
            if (role.Length > MentorRecipeLimits.MaxRoleLength)
                return TooLong(label, "role", MentorRecipeLimits.MaxRoleLength);
        }

        if (recipe.Parameters.Count > MentorRecipeLimits.MaxParameters)
            return (MentorRecipeRejectionReasons.InvalidParameters,
                $"The recipe '{label}' has {recipe.Parameters.Count} parameters; it has at most {MentorRecipeLimits.MaxParameters}.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in recipe.Parameters)
        {
            if (!IsName(parameter.Name) || !names.Add(parameter.Name))
                return (MentorRecipeRejectionReasons.InvalidParameters,
                    $"A parameter of the recipe '{label}' has no valid name, or the name of another.");
            if (!Enum.IsDefined(parameter.Type))
                return (MentorRecipeRejectionReasons.InvalidParameters, $"The parameter '{parameter.Name}' of the recipe '{label}' has no known type.");
            if (string.IsNullOrWhiteSpace(parameter.Description))
                return (MentorRecipeRejectionReasons.InvalidDefinition, $"The parameter '{parameter.Name}' of the recipe '{label}' has no description.");
            if (parameter.Description.Length > MentorRecipeLimits.MaxDescriptionLength)
                return TooLong(label, "parameter's description", MentorRecipeLimits.MaxDescriptionLength);
        }

        if (recipe.Steps.Count is 0 or > MentorRecipeLimits.MaxSteps)
            return (MentorRecipeRejectionReasons.InvalidSteps,
                $"The recipe '{label}' has {recipe.Steps.Count} steps; it has 1 to {MentorRecipeLimits.MaxSteps}.");
        foreach (var step in recipe.Steps)
        {
            if (!Enum.IsDefined(step.Kind))
                return (MentorRecipeRejectionReasons.InvalidSteps, $"A step of the recipe '{label}' is of a kind MentorAgent does not know.");
            if (string.IsNullOrWhiteSpace(step.Selector))
                return (MentorRecipeRejectionReasons.InvalidDefinition, $"A step of the recipe '{label}' has no selector.");
            if (new[] { step.Selector, step.Cell, step.Target, step.Filter }.Any(s => s?.Length > MentorRecipeLimits.MaxSelectorLength))
                return TooLong(label, "selector", MentorRecipeLimits.MaxSelectorLength);
            if (step.Value?.Length > MentorRecipeLimits.MaxValueLength)
                return TooLong(label, "value", MentorRecipeLimits.MaxValueLength);
            if (step.Kind == MentorRecipeStepKind.Row && (string.IsNullOrWhiteSpace(step.Cell) || string.IsNullOrWhiteSpace(step.Value)))
                return (MentorRecipeRejectionReasons.InvalidDefinition, $"A row step of the recipe '{label}' has no key cell or no key.");
            if (ParameterOf(step.Value) is { } name)
            {
                if (!names.Contains(name))
                    return (MentorRecipeRejectionReasons.UnknownParameter,
                        $"A step of the recipe '{label}' uses '{{{name}}}', which is not one of its parameters.");
            }
            else if (Literal(step.Value) is null)
            {
                return (MentorRecipeRejectionReasons.InvalidValue,
                    $"A step of the recipe '{label}' has a single brace in its text: a brace is written '{{{{' or '}}}}'.");
            }
        }

        return null;
    }

    private static (string, string) TooLong(string label, string field, int limit)
        => (MentorRecipeRejectionReasons.TooLong, $"A {field} of the recipe '{label}' is longer than {limit} characters.");

    /// <summary>A name of a recipe or of a parameter: 1 to 64 letters, digits, '_' and '-'.</summary>
    internal static bool IsName(string? name)
        => name is { Length: > 0 and <= MentorRecipeLimits.MaxNameLength } && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    /// <summary>
    /// The recipes on, whose route is the page at <paramref name="path"/>: those added in code, then those of the
    /// session's sources (<paramref name="session"/>), by name.
    /// </summary>
    internal IReadOnlyList<MentorRecipe> For(string? path, IReadOnlyList<MentorRecipe>? session = null)
    {
        if (path is null || (_recipes.IsEmpty && session is not { Count: > 0 })) return [];
        return
        [
            .. _recipes.Values.OrderBy(r => r.Name, StringComparer.Ordinal).Concat(session ?? [])
                .Where(r => !_off.ContainsKey(OffKey(r)) && MentorPageRoutes.Match(path, [r.Route]) is not null),
        ];
    }

    /// <summary>
    /// Whether the recipe offered is still one to run: on, and still the one added in code or one of the session's.
    /// </summary>
    internal bool IsOn(MentorRecipe recipe, IReadOnlyList<MentorRecipe>? session = null)
        => !_off.ContainsKey(OffKey(recipe))
           && ((_recipes.TryGetValue(Key(recipe), out var added) && ReferenceEquals(added, recipe))
               || (session ?? []).Any(r => ReferenceEquals(r, recipe)));

    /// <summary>Turns a recipe off, with why.</summary>
    internal void TurnOff(MentorRecipe recipe, string reason) => _off[OffKey(recipe)] = reason;

    internal bool IsOff(MentorRecipe recipe) => _off.ContainsKey(OffKey(recipe));

    /// <summary>Whether a recipe of that name is off, on any route.</summary>
    internal bool IsOff(string name) => _off.Keys.Any(k => k.Name == name);

    /// <summary>Turns off the recipes added in code with that name, on every route.</summary>
    internal void TurnOff(string name, string reason)
    {
        foreach (var recipe in _recipes.Values.Where(r => r.Name == name))
            TurnOff(recipe, reason);
    }

    /// <summary>The parameter a value names (<c>{name}</c>, the whole value), or null for a text.</summary>
    internal static string? ParameterOf(string? value)
        => value is { Length: > 2 } && value[0] == '{' && value[^1] == '}' && IsName(value[1..^1]) ? value[1..^1] : null;

    /// <summary>
    /// The text a value writes, with <c>{{</c> and <c>}}</c> as a brace; null when it has a single brace. No value writes
    /// an empty text.
    /// </summary>
    internal static string? Literal(string? value)
    {
        if (value is null) return "";
        if (value.IndexOfAny(['{', '}']) < 0) return value;

        var text = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c is '{' or '}')
            {
                if (i + 1 >= value.Length || value[i + 1] != c) return null;
                i++;
            }

            text.Append(c);
        }

        return text.ToString();
    }

    /// <summary>
    /// The most the steps of one run may weigh, as JSON: they reach the browser in one message, and a hub connection drops,
    /// without an error, a message past its size limit (32 KB by default).
    /// </summary>
    internal const int MaxCallBytes = 16 * 1024;

    /// <summary>
    /// The steps with the model's values in place of the parameters. A parameter the model did not give, one of the
    /// wrong type, or values that make the steps heavier than <see cref="MaxCallBytes"/>, is an error the model reads:
    /// nothing runs.
    /// </summary>
    internal static IReadOnlyList<MentorRecipeStepCall> Steps(MentorRecipe recipe, IReadOnlyDictionary<string, object?> arguments)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var parameter in recipe.Parameters)
        {
            if (!arguments.TryGetValue(parameter.Name, out var given) || Text(given) is not { } text)
                throw new ArgumentException($"The recipe '{recipe.Name}' needs '{parameter.Name}'.");
            values[parameter.Name] = parameter.Type switch
            {
                MentorRecipeParameterType.Number when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
                    => n.ToString(CultureInfo.InvariantCulture),
                MentorRecipeParameterType.Boolean when bool.TryParse(text, out var b) => b ? "true" : "false",
                MentorRecipeParameterType.String => text,
                _ => throw new ArgumentException($"'{parameter.Name}' of the recipe '{recipe.Name}' is a {parameter.Type.ToString().ToLowerInvariant()}."),
            };
        }

        List<MentorRecipeStepCall> calls = [.. recipe.Steps.Select(s => new MentorRecipeStepCall(
            s.Kind.ToString().ToLowerInvariant(),
            s.Selector,
            ParameterOf(s.Value) is { } name ? values[name] : s.Value is null ? null : Literal(s.Value),
            s.Cell, s.Target, s.Filter))];

        var size = JsonSerializer.SerializeToUtf8Bytes(calls, JsonSerializerOptions.Web).Length;
        if (size > MaxCallBytes)
            throw new ArgumentException(
                $"The values given to the recipe '{recipe.Name}' are too long: its steps would weigh {size} bytes, at most {MaxCallBytes}.");
        return calls;
    }

    private static string? Text(object? value) => value switch
    {
        null => null,
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
        JsonElement { ValueKind: JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False } e => e.GetRawText(),
        JsonElement => null,
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };
}
