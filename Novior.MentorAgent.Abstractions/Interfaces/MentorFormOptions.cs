namespace Novior.MentorAgent.Abstractions.Interfaces;

/// <summary>
/// How <see cref="MentorFormExtensions.RegisterForm{TModel}"/> reads a form. A field is named by its property, as
/// written in the model (<c>nameof(Order.CustomerName)</c>) or as the model sees it (<c>customerName</c>); a name that is
/// no field of the form throws when the form is registered.
/// </summary>
public sealed class MentorFormOptions
{
    private readonly HashSet<string> _excluded = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _sensitive = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _selectors = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Leaves these fields out: the model is not told of them and cannot fill them.</summary>
    public MentorFormOptions Exclude(params string[] fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        foreach (var field in fields)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(field, nameof(fields));
            _excluded.Add(field);
        }
        return this;
    }

    /// <summary>
    /// Lets in these sensitive fields, which a form leaves out unless they are named here: the model can then fill
    /// them, and the page's field is written even when it is a password field. What the model writes there it has seen,
    /// and so has its provider: name only what the user may say to the assistant.
    /// </summary>
    public MentorFormOptions IncludeSensitive(params string[] fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        foreach (var field in fields)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(field, nameof(fields));
            _sensitive.Add(field);
        }
        return this;
    }

    /// <summary>The CSS selector of a field in the page, in place of its <c>data-mentor-id</c>.</summary>
    public MentorFormOptions Selector(string field, string selector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        _selectors[field] = selector;
        return this;
    }

    internal IReadOnlyCollection<string> Excluded => _excluded;
    internal IReadOnlyCollection<string> Sensitive => _sensitive;
    internal IReadOnlyDictionary<string, string> Selectors => _selectors;
}
