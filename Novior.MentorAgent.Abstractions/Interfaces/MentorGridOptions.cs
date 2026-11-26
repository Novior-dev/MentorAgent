namespace Novior.MentorAgent.Abstractions.Interfaces;

/// <summary>
/// How <see cref="MentorGridExtensions.RegisterGrid{TRow}"/> reads a grid. A column is named by its property, as written
/// in the row (<c>nameof(Order.Total)</c>) or as the model sees it (<c>total</c>); a name that is no column of the grid
/// throws when the grid is registered.
/// </summary>
public sealed class MentorGridOptions
{
    private readonly HashSet<string> _sensitive = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _selectors = new(StringComparer.OrdinalIgnoreCase);
    private List<string>? _sortable;

    /// <summary>
    /// Whether the grid has a filter field in the page, <c>data-mentor-id="&lt;grid&gt;.filter"</c>: then the model gets
    /// <c>filter_&lt;grid&gt;</c>, and opening a row writes its key there first when the row is not shown. True by default.
    /// </summary>
    public bool Filter { get; set; } = true;

    /// <summary>
    /// The columns the grid sorts by, in place of all its columns of a simple type that are not sensitive. None: the
    /// model gets no <c>sort_&lt;grid&gt;</c>, because the page sorts by nothing.
    /// </summary>
    public MentorGridOptions Sortable(params string[] columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        _sortable = [];
        foreach (var column in columns)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(column, nameof(columns));
            _sortable.Add(column);
        }
        return this;
    }

    /// <summary>
    /// Lets these sensitive columns be sorted by, which a grid leaves out unless they are named here. A sensitive column
    /// is never the grid's key: the model would have to write its value to open a row.
    /// </summary>
    public MentorGridOptions IncludeSensitive(params string[] columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        foreach (var column in columns)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(column, nameof(columns));
            _sensitive.Add(column);
        }
        return this;
    }

    /// <summary>
    /// The CSS selector of a part of the grid in the page, in place of its <c>data-mentor-id</c>. The part is what follows
    /// <c>"&lt;grid&gt;."</c> in that <c>data-mentor-id</c>: <c>row</c> (the rows), <c>cell.&lt;key&gt;</c> (the key's
    /// cell, inside a row), <c>open</c> (what opens a row, inside it), <c>filter</c>, or <c>sort.&lt;column&gt;</c> (the
    /// header clicked to sort by the column). A grid whose rows and headers take no attribute, such as a
    /// <c>QuickGrid</c>, is given its selectors here.
    /// </summary>
    public MentorGridOptions Selector(string part, string selector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(part);
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        _selectors[part] = selector;
        return this;
    }

    internal IReadOnlyList<string>? SortableColumns => _sortable;
    internal IReadOnlyCollection<string> Sensitive => _sensitive;
    internal IReadOnlyDictionary<string, string> Selectors => _selectors;
}
