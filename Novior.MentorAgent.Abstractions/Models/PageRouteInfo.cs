namespace Novior.MentorAgent.Abstractions.Models;

/// <summary>
/// One page of a client application, as its router knows it: sent by the WebAssembly client of
/// <c>Novior.MentorAgent.Blazor</c> to the hub of <c>Novior.MentorAgent.Server</c> (<c>ReportPages</c>) once a
/// connection opens, so the server knows the pages it cannot read itself. Structure only: no value of a page.
/// </summary>
public sealed class PageRouteInfo
{
    /// <summary>The route template, as the page's <c>@page</c> writes it (for example <c>/orders/{id:int}</c>).</summary>
    public string Route { get; set; } = string.Empty;

    /// <summary>The full name of the component that serves it (for example <c>Shop.Client.Pages.Orders</c>).</summary>
    public string Component { get; set; } = string.Empty;
}
