using System.Reflection;

namespace Novior.MentorAgent.Abstractions.Core;

/// <summary>
/// The parameter hint a UI action shows the model ("integer", "string (Low|High)",
/// "{ id: integer, note: string? }") — one generator for every host. It lived in the Blazor Server
/// page context only; the WebAssembly one sent the bare type name ("Int32", "List`1") and the hub
/// then dropped even that (rc.11 README audit).
/// </summary>
internal static class MentorParameterHint
{
    /// <summary>
    /// Generates a human-readable schema hint from a .NET type for the AI tool description.
    /// Used as the automatic <c>parameterHint</c> when the developer does not supply one.
    ///
    /// Examples:
    /// <list type="bullet">
    ///   <item><c>int</c>  → <c>"integer"</c></item>
    ///   <item><c>string</c> → <c>"string"</c></item>
    ///   <item><c>Status</c> (enum) → <c>"string (Active|Inactive|Pending)"</c></item>
    ///   <item><c>OrderModel</c> → <c>"{ id: integer, name: string, total: number }"</c></item>
    ///   <item><c>List&lt;int&gt;</c> → <c>"array&lt;integer&gt;"</c></item>
    /// </list>
    ///
    /// <para>
    /// Depth is capped at 3 levels to prevent <see cref="StackOverflowException"/> on
    /// self-referential or mutually-referential types (e.g. tree nodes, EF navigation
    /// properties). Beyond the cap the type is represented as <c>"object"</c>.
    /// </para>
    /// </summary>
    public static string For(Type type, int depth = 0)
    {
        // Guard against circular / deeply-nested types
        if (depth > 3) return "object";

        // Unwrap Nullable<T>
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null)
            return For(underlying, depth) + "?";

        // Primitives
        if (type == typeof(string))                              return "string";
        if (type == typeof(bool))                               return "boolean";
        if (type == typeof(Guid))                               return "string (GUID)";
        if (type == typeof(DateTime) || type == typeof(DateTimeOffset))
                                                                return "string (ISO 8601 date)";
        if (type == typeof(int)   || type == typeof(long)  ||
            type == typeof(short) || type == typeof(byte))      return "integer";
        if (type == typeof(float) || type == typeof(double) ||
            type == typeof(decimal))                            return "number";

        // Enum → list allowed values
        if (type.IsEnum)
            return $"string ({string.Join("|", Enum.GetNames(type))})";

        // Arrays
        if (type.IsArray && type.GetElementType() is { } elem)
            return $"array<{For(elem, depth + 1)}>";

        // Generic collections and dictionaries
        if (type.IsGenericType)
        {
            var def  = type.GetGenericTypeDefinition();
            var args = type.GetGenericArguments();

            bool isList = def == typeof(List<>)        || def == typeof(IList<>)  ||
                          def == typeof(IEnumerable<>) || def == typeof(ICollection<>);
            if (isList) return $"array<{For(args[0], depth + 1)}>";

            bool isDict = def == typeof(Dictionary<,>) || def == typeof(IDictionary<,>);
            if (isDict) return $"object<{For(args[0], depth + 1)},{For(args[1], depth + 1)}>";
        }

        // Complex object — list readable public properties (camelCase names)
        if (type.IsClass || (type.IsValueType && !type.IsPrimitive))
        {
            var props = type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
                .Select(p =>
                {
                    var name = char.ToLowerInvariant(p.Name[0]) + p.Name[1..];
                    return $"{name}: {For(p.PropertyType, depth + 1)}";
                })
                .ToList();

            return props.Count > 0
                ? "{ " + string.Join(", ", props) + " }"
                : "object";
        }

        return type.Name;
    }
}
