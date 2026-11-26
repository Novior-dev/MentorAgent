using Microsoft.JSInterop;
using Novior.MentorAgent.Abstractions.Core;

namespace Novior.MentorAgent.Abstractions.Interfaces;

/// <summary>
/// A page's form as a UI action the model can fill: <c>fill_&lt;form&gt;</c>, with the fields to fill as its parameter.
/// The action fills the fields, as the user would type them, and never submits the form: sending it stays an action of
/// the application's own, with its confirmation if it writes.
/// </summary>
public static class MentorFormExtensions
{
    /// <summary>
    /// Registers the form whose model is <typeparamref name="TModel"/> as the UI action <c>fill_<paramref name="name"/></c>.
    /// <para>
    /// The fields are the public properties of <typeparamref name="TModel"/> with a getter and a setter, of a simple type:
    /// text, number, <see cref="bool"/> (a checkbox), an enum (a select, whose options read the enum's names),
    /// <see cref="Guid"/>, a date or a time. Each field is found in the page by its <c>data-mentor-id</c>,
    /// <c>"<paramref name="name"/>.&lt;field&gt;"</c>, the field written as the model sees it (<c>customerName</c>), unless
    /// <see cref="MentorFormOptions.Selector"/> gives another selector.
    /// </para>
    /// <para>
    /// A sensitive field is never offered unless <see cref="MentorFormOptions.IncludeSensitive"/> names it: a property
    /// with <c>[DataType(DataType.Password)]</c>, <c>[DataType(DataType.CreditCard)]</c> or
    /// <c>[PasswordPropertyText]</c>, or whose name says it holds a password, a secret, a token, a PIN, a card's number or
    /// code, an IBAN or a one-time code. In the page, a password field is never written either, whatever its property.
    /// </para>
    /// <para>
    /// The steps run in the browser through <paramref name="js"/>, the page's own <see cref="IJSRuntime"/>: on Blazor
    /// Server, WebAssembly and MAUI Hybrid alike. A field that is not in the page, hidden, disabled, inside
    /// <c>data-mentor-exclude</c> or a password field fails the action, which the gate reports as failed.
    /// </para>
    /// </summary>
    /// <param name="page">The page's context.</param>
    /// <param name="name">The form's name: letters, digits, <c>_</c> and <c>-</c>; the action is <c>fill_&lt;name&gt;</c>.</param>
    /// <param name="description">What the form is, for the model and for the decisions ("The new order's form").</param>
    /// <param name="js">The page's JavaScript runtime (<c>@inject IJSRuntime JS</c>).</param>
    /// <param name="configure">Fields left out, sensitive fields let in, selectors of the page's own.</param>
    public static IMentorPageContext RegisterForm<TModel>(this IMentorPageContext page, string name, string description,
        IJSRuntime js, Action<MentorFormOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(js);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        var options = new MentorFormOptions();
        configure?.Invoke(options);
        var form = MentorForm.Create(typeof(TModel), name, options);
        return page.RegisterUIActionAsync(form.ActionName, description, p => form.FillAsync(js, p), form.Hint);
    }
}
