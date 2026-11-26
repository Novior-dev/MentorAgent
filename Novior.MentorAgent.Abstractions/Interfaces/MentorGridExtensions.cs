using System.Linq.Expressions;
using Microsoft.JSInterop;
using Novior.MentorAgent.Abstractions.Core;

namespace Novior.MentorAgent.Abstractions.Interfaces;

/// <summary>
/// A page's grid, a table of rows, as UI actions the model can use: open a row by its key, sort by a column, write in the
/// grid's filter. Nothing of the rows goes back to the model: each action answers done, or not done and why.
/// </summary>
public static class MentorGridExtensions
{
    /// <summary>
    /// Registers the grid whose rows are <typeparamref name="TRow"/> as the UI actions <c>open_<paramref name="name"/>_row</c>,
    /// <c>sort_<paramref name="name"/></c> and <c>filter_<paramref name="name"/></c>.
    /// <para>
    /// <c>open_&lt;grid&gt;_row({ key })</c> finds the row whose key cell reads the key, as the page shows it, after the
    /// grid's filter or scrolling the rows (a virtualized grid renders them as they come into view), and clicks what opens
    /// it. The key is <paramref name="key"/>, a property of the row: a text, a whole number, a <see cref="Guid"/> or an
    /// enum, and never a sensitive one.
    /// </para>
    /// <para>
    /// <c>sort_&lt;grid&gt;({ column, descending? })</c> clicks the column's header until its <c>aria-sort</c> says the
    /// order asked, three clicks at most; a header that does not say its order is not sorted. It is offered when the grid
    /// sorts by a column: by default every column of a simple type that is not sensitive
    /// (<see cref="MentorGridOptions.Sortable"/>). <c>filter_&lt;grid&gt;(text)</c> writes the text in the grid's filter,
    /// and an empty text clears it; it is offered unless <see cref="MentorGridOptions.Filter"/> is false.
    /// </para>
    /// <para>
    /// Each part is found by its <c>data-mentor-id</c>: <c>"&lt;grid&gt;.row"</c>, <c>"&lt;grid&gt;.cell.&lt;key&gt;"</c>,
    /// <c>"&lt;grid&gt;.open"</c>, <c>"&lt;grid&gt;.filter"</c> and <c>"&lt;grid&gt;.sort.&lt;column&gt;"</c>, the
    /// column written as the model sees it, unless <see cref="MentorGridOptions.Selector"/> gives another selector. The
    /// steps run in the browser through <paramref name="js"/>, the page's own <see cref="IJSRuntime"/>, with the recipes'
    /// runner, on Blazor Server, WebAssembly and MAUI Hybrid. A part that is not in the page, hidden, disabled or inside
    /// <c>data-mentor-exclude</c> fails the action, which the gate reports as failed.
    /// </para>
    /// </summary>
    /// <param name="page">The page's context.</param>
    /// <param name="name">The grid's name: letters, digits, <c>_</c> and <c>-</c>.</param>
    /// <param name="description">What the grid is, for the model and for the decisions ("The orders of the customer").</param>
    /// <param name="js">The page's JavaScript runtime (<c>@inject IJSRuntime JS</c>).</param>
    /// <param name="key">The row's key, a property of <typeparamref name="TRow"/>: <c>r =&gt; r.Id</c>.</param>
    /// <param name="configure">The columns sorted by, the filter, sensitive columns let in, selectors of the page's own.</param>
    public static IMentorPageContext RegisterGrid<TRow>(this IMentorPageContext page, string name, string description,
        IJSRuntime js, Expression<Func<TRow, object?>> key, Action<MentorGridOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(js);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        var options = new MentorGridOptions();
        configure?.Invoke(options);
        var grid = MentorGrid.Create(typeof(TRow), name, MentorGrid.KeyOf(key), options);

        page.RegisterUIActionAsync(grid.OpenAction, $"{description}: opens the row whose {grid.Key.Name} is the key",
            p => grid.OpenAsync(js, p), grid.OpenHint);
        if (grid.FilterSelector is not null)
            page.RegisterUIActionAsync(grid.FilterAction, $"{description}: writes a text in the grid's filter",
                p => grid.FilterAsync(js, p), MentorGrid.FilterHint);
        if (grid.Sortable.Count > 0)
            page.RegisterUIActionAsync(grid.SortAction, $"{description}: sorts the rows by a column",
                p => grid.SortAsync(js, p), grid.SortHint);
        return page;
    }
}
