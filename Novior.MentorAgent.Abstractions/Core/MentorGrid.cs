using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using Microsoft.JSInterop;
using Novior.MentorAgent.Abstractions.Interfaces;

namespace Novior.MentorAgent.Abstractions.Core;

/// <summary>
/// Story 1319, slice B — a grid registered with <see cref="MentorGridExtensions.RegisterGrid{TRow}"/>: its key, the
/// columns it sorts by, its selectors, and the steps its actions run in the browser with the runner of the recipes
/// (<c>MentorRecipeRunner.js</c>). The selectors are the grid's, never the model's: the model gives a key, a column, a
/// text. Nothing of the rows comes back: each step answers done, or not done and why.
/// </summary>
internal sealed class MentorGrid
{
    /// <summary>What the model reads of the filter's parameter.</summary>
    internal const string FilterHint = "string - the text written in the grid's filter; an empty text clears it";

    private MentorGrid(string name, MentorFormField key, string rows, string open, string? filter,
        IReadOnlyList<MentorGridColumn> sortable)
    {
        Name = name;
        Key = key;
        RowSelector = rows;
        OpenSelector = open;
        FilterSelector = filter;
        Sortable = sortable;
        OpenHint = $"{{ key: {key.Hint} }} - the {key.Name} of the row to open, as the grid shows it";
        SortHint = $"{{ column: string ({string.Join("|", sortable.Select(c => c.Name))}), descending: boolean? }}";
    }

    internal string Name { get; }

    internal string OpenAction => $"open_{Name}_row";
    internal string SortAction => $"sort_{Name}";
    internal string FilterAction => $"filter_{Name}";

    /// <summary>The key: its name as the model sees it, its type, and the selector of its cell inside a row.</summary>
    internal MentorFormField Key { get; }

    internal string RowSelector { get; }
    internal string OpenSelector { get; }

    /// <summary>The grid's filter field; null, the grid has none and the model gets no <c>filter_</c>.</summary>
    internal string? FilterSelector { get; }

    /// <summary>The columns the model may sort by, with their headers' selectors; none, the model gets no <c>sort_</c>.</summary>
    internal IReadOnlyList<MentorGridColumn> Sortable { get; }

    internal string OpenHint { get; }
    internal string SortHint { get; }

    /// <summary>The property a key's expression reads: <c>r =&gt; r.Id</c>, the boxing of a value type let through.</summary>
    internal static PropertyInfo KeyOf(LambdaExpression key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var body = key.Body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert
            ? convert.Operand : key.Body;
        if (body is MemberExpression { Member: PropertyInfo property } member && member.Expression == key.Parameters[0])
            return property;
        throw new ArgumentException($"The grid's key must be a property of the row, as in r => r.Id, not '{key}'.", nameof(key));
    }

    internal static MentorGrid Create(Type row, string name, PropertyInfo key, MentorGridOptions options)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrEmpty(name) || !MentorForm.NameShape().IsMatch(name) || "open_".Length + name.Length + "_row".Length > MentorForm.MaxActionName)
            throw new ArgumentException(
                $"The grid's name '{name}' is not a tool's name: letters, digits, '_' and '-', with 'open_' and '_row' at most {MentorForm.MaxActionName} of them.",
                nameof(name));

        var columns = row.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0 && p.GetMethod?.IsPublic == true && MentorForm.KindOf(p.PropertyType) is not null)
            .OrderBy(p => p.MetadataToken)
            .ToList();

        // A name the options give that is no column is the application's mistake: said now, not at the first call.
        foreach (var named in (options.SortableColumns ?? []).Concat(options.Sensitive))
            if (!columns.Any(c => Is(c, named)))
                throw new ArgumentException($"'{named}' is not a column of the grid '{name}' ({row.Name}).", nameof(options));

        // The key: a column the page shows as a text the model can write, and never a sensitive one.
        if (MentorForm.IsSensitive(key))
            throw new ArgumentException(
                $"The key '{key.Name}' of the grid '{name}' is sensitive: the model would have to write it to open a row. Choose another key.",
                nameof(key));
        var keyType = Nullable.GetUnderlyingType(key.PropertyType) ?? key.PropertyType;
        if (!(keyType == typeof(string) || keyType == typeof(Guid) || keyType.IsEnum || IsWholeNumber(keyType)))
            throw new ArgumentException(
                $"The key '{key.Name}' of the grid '{name}' is a {keyType.Name}: a key is a text, a whole number, a Guid or an enum, which the page shows as the model writes it.",
                nameof(key));

        var keyName = MentorForm.Camel(key.Name);
        var sortable = new List<MentorGridColumn>();
        foreach (var column in columns)
        {
            if (options.SortableColumns is { } chosen && !chosen.Any(c => Is(column, c))) continue;
            var included = options.Sensitive.Any(s => Is(column, s));
            if (MentorForm.IsSensitive(column) && !included)
            {
                if (options.SortableColumns is not null)
                    throw new ArgumentException(
                        $"'{column.Name}' is a sensitive column of the grid '{name}': let it in with IncludeSensitive to sort by it.",
                        nameof(options));
                continue;
            }
            sortable.Add(new MentorGridColumn(MentorForm.Camel(column.Name), $"[data-mentor-id=\"{name}.sort.{MentorForm.Camel(column.Name)}\"]"));
        }

        // The selectors of the page's own, each a part the grid has.
        string? rows = null, cell = null, open = null, filter = null;
        foreach (var (part, selector) in options.Selectors)
        {
            if (Same(part, "row")) rows = selector;
            else if (Same(part, "open")) open = selector;
            else if (Same(part, "filter") && options.Filter) filter = selector;
            else if (part.StartsWith("cell.", StringComparison.OrdinalIgnoreCase) && Same(part[5..], key.Name)) cell = selector;
            else if (part.StartsWith("sort.", StringComparison.OrdinalIgnoreCase)
                     && sortable.FindIndex(c => Same(c.Name, part[5..])) is var i and >= 0)
                sortable[i] = sortable[i] with { Selector = selector };
            else
                throw new ArgumentException(
                    $"'{part}' is no part of the grid '{name}': its parts are row, cell.{keyName}, open"
                    + (options.Filter ? ", filter" : "") + string.Concat(sortable.Select(c => $", sort.{c.Name}")) + ".",
                    nameof(options));
        }

        var keyField = new MentorFormField(keyName, key.PropertyType, MentorForm.KindOf(key.PropertyType)!.Value,
            cell ?? $"[data-mentor-id=\"{name}.cell.{keyName}\"]", AllowSensitive: false);
        return new MentorGrid(name, keyField,
            rows ?? $"[data-mentor-id=\"{name}.row\"]",
            open ?? $"[data-mentor-id=\"{name}.open\"]",
            options.Filter ? filter ?? $"[data-mentor-id=\"{name}.filter\"]" : null,
            sortable);
    }

    // -- The steps -------------------------------------------------------------------

    /// <summary>Opening a row: the runner's row step, with the key checked against its type and written as the page shows it.</summary>
    internal IReadOnlyList<MentorGridStep> OpenSteps(object? parameter)
    {
        var element = Element(parameter);
        if (element.ValueKind == JsonValueKind.Object)
        {
            var key = element.EnumerateObject().Where(p => Same(p.Name, "key") || Same(p.Name, Key.Name)).Select(p => p.Value).ToList();
            if (key.Count != 1)
                throw new ArgumentException($"Give the row to open as {{ key: {Key.Hint} }}, the row's {Key.Name}.");
            element = key[0];
        }
        if (element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.Object or JsonValueKind.Array)
            throw new ArgumentException($"Give the row to open as {{ key: {Key.Hint} }}, the row's {Key.Name}.");

        var value = Key.Write(element);
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"Give the row to open as {{ key: {Key.Hint} }}, the row's {Key.Name}.");
        return [new MentorGridStep("row", RowSelector, value, Key.Selector, OpenSelector, FilterSelector)];
    }

    /// <summary>Sorting: the runner's sort step on the column's header, with the order asked.</summary>
    internal IReadOnlyList<MentorGridStep> SortSteps(object? parameter)
    {
        var element = Element(parameter);
        string? column = null;
        var descending = false;
        if (element.ValueKind == JsonValueKind.String)
            column = element.GetString();
        else if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
            {
                if (Same(property.Name, "column") && property.Value.ValueKind == JsonValueKind.String)
                    column = property.Value.GetString();
                else if (Same(property.Name, "descending"))
                    descending = property.Value.ValueKind switch
                    {
                        JsonValueKind.True => true,
                        JsonValueKind.False or JsonValueKind.Null => false,
                        JsonValueKind.String when bool.TryParse(property.Value.GetString(), out var b) => b,
                        _ => throw new ArgumentException($"'descending' is true or false, not {property.Value.GetRawText()}."),
                    };
            }

        var found = Sortable.FirstOrDefault(c => Same(c.Name, column?.Trim() ?? ""));
        if (found is null)
            throw new ArgumentException(column is null
                ? $"Give the column to sort by as {SortHint}."
                : $"'{column}' is not a column the grid '{Name}' sorts by. Its columns are: {string.Join(", ", Sortable.Select(c => c.Name))}.");
        return [new MentorGridStep("sort", found.Selector, descending ? "descending" : "ascending") { Column = found.Name }];
    }

    /// <summary>Filtering: the text written in the grid's filter field, as typing would.</summary>
    internal IReadOnlyList<MentorGridStep> FilterSteps(object? parameter)
    {
        if (parameter is null) return [new MentorGridStep("set", FilterSelector!, "")];
        var element = Element(parameter);
        if (element.ValueKind == JsonValueKind.Object)
        {
            var text = element.EnumerateObject().Where(p => Same(p.Name, "text")).Select(p => p.Value).ToList();
            element = text.Count == 1 ? text[0] : default;
        }
        var value = element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? "",
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.Null => "",
            _ => throw new ArgumentException($"Give the text to write in the filter of the grid '{Name}' as one string."),
        };
        return [new MentorGridStep("set", FilterSelector!, value)];
    }

    // -- Running ---------------------------------------------------------------------

    internal async Task OpenAsync(IJSRuntime js, object? parameter)
    {
        var steps = OpenSteps(parameter);
        var outcome = await MentorForm.RunAsync(js, steps).ConfigureAwait(false);
        if (outcome.Done) return;
        throw new InvalidOperationException(outcome.Reason switch
        {
            "no-row" => $"No row of the grid '{Name}' has the {Key.Name} '{steps[0].Value}' in the page, after its filter and scrolling. Nothing was opened.",
            "excluded" => $"The grid '{Name}' is not one the assistant reads. Nothing was opened.",
            var reason => $"The row of the grid '{Name}' was not opened: a part of the grid is {reason ?? "missing"} in the page.",
        });
    }

    internal async Task SortAsync(IJSRuntime js, object? parameter)
    {
        var steps = SortSteps(parameter);
        var outcome = await MentorForm.RunAsync(js, steps).ConfigureAwait(false);
        if (outcome.Done) return;
        var column = steps[0].Column;
        throw new InvalidOperationException(outcome.Reason switch
        {
            "no-sort-state" => $"The grid '{Name}' was not sorted by '{column}': its header does not say how the rows are sorted (aria-sort), so the order asked cannot be told.",
            "not-sorted" => $"The grid '{Name}' was not sorted by '{column}' {steps[0].Value}: after three clicks on its header the order is another.",
            var reason => $"The grid '{Name}' was not sorted: the header of '{column}' is {reason ?? "missing"} in the page.",
        });
    }

    internal async Task FilterAsync(IJSRuntime js, object? parameter)
    {
        var steps = FilterSteps(parameter);
        var outcome = await MentorForm.RunAsync(js, steps).ConfigureAwait(false);
        if (outcome.Done) return;
        throw new InvalidOperationException($"The grid '{Name}' was not filtered: its filter field is {outcome.Reason ?? "missing"} in the page.");
    }

    // -- Helpers ---------------------------------------------------------------------

    /// <summary>The parameter the model gave: a value, an object, or an object written as a text.</summary>
    private static JsonElement Element(object? parameter)
    {
        var element = parameter switch
        {
            null => default,
            JsonElement e => e,
            string text => JsonSerializer.SerializeToElement(text),
            _ => JsonSerializer.SerializeToElement(parameter),
        };
        if (element.ValueKind == JsonValueKind.String && element.GetString() is { } s && s.TrimStart().StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(s);
                return doc.RootElement.Clone();
            }
            catch (JsonException) { }
        }
        return element;
    }

    private static bool IsWholeNumber(Type t)
        => t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte)
           || t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort) || t == typeof(sbyte);

    private static bool Is(PropertyInfo property, string named) => Same(property.Name, named);

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>A column the grid sorts by: its name as the model sees it, and the selector of the header clicked.</summary>
internal sealed record MentorGridColumn(string Name, string Selector);

/// <summary>
/// A step of a grid's action as the browser runs it (<c>MentorRecipeRunner.js</c>): <c>row</c> with the key's cell,
/// the target clicked and the filter; <c>sort</c> with the order asked; <c>set</c> in the filter.
/// </summary>
internal sealed record MentorGridStep(string Kind, string Selector, string? Value,
    string? Cell = null, string? Target = null, string? Filter = null)
{
    /// <summary>The column a sort step sorts by, for the message of a step not done; not sent to the browser.</summary>
    internal string Column { get; init; } = "";
}
