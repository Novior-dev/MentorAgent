using System.Collections.Concurrent;
using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.JSInterop;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Tests.Harness;
using Xunit;

namespace Novior.MentorAgent.Tests.Pages;

/// <summary>
/// Story 1319, slice A — a form registered with <c>RegisterForm&lt;TModel&gt;</c>, filled by the model's call in a real
/// browser (Edge or Chrome, headless) on an interactive Blazor Server page: an <c>EditForm</c> with <c>InputText</c>,
/// <c>InputNumber</c>, <c>InputSelect</c> of an enum and <c>InputCheckbox</c>, each found by its <c>data-mentor-id</c>.
/// The form is filled and never submitted, and a password field of the page is never written unless the application
/// let its field in.
/// </summary>
[Trait("Rule", "form.fill")]
public sealed class FormBrowserTests
{
    private static async Task<FormFillPage.Order> BoundAsync(RecipeBrowserTests.Site site, Func<FormFillPage.Order, bool> until)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!(FormFillPage.Bound.TryGetValue(site.Run, out var order) && until(order)))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The form's model did not get the values.");
            await Task.Delay(100);
        }
        return FormFillPage.Bound[site.Run];
    }

    [BrowserFact]
    public async Task The_model_fills_an_EditForm_by_its_data_mentor_ids_and_the_form_is_not_submitted()
    {
        await using var site = await RecipeBrowserTests.OpenAsync(null, "fill");
        site.Chat.CallTool("fill_order", new { parameter = new { customer = "Anna Rossi", quantity = 3, country = "france", express = true } })
            .Say("The form is filled: check it and press Save.");

        await RecipeBrowserTests.TurnAsync(site, "Order 3 for Anna Rossi to France, express");

        var order = await BoundAsync(site, o => o.Express);
        order.Should().BeEquivalentTo(new FormFillPage.Order
        {
            Customer = "Anna Rossi", Quantity = 3, Country = FormFillPage.Country.France, Express = true,
        });
        FormFillPage.Submitted.ContainsKey(site.Run).Should().BeFalse("fill_order fills, and never submits the form");
        site.Observer.Steps("fill_order").Should().Equal(["Allowed/allowed", "Executed/completed"]);
    }

    [BrowserFact]
    [Trait("Rule", "form.sensitive")]
    public async Task A_password_field_of_the_page_is_never_written_unless_the_application_let_its_field_in()
    {
        await using (var site = await RecipeBrowserTests.OpenAsync(null, "fill"))
        {
            site.Chat.CallTool("fill_order", new { parameter = new { customer = "Anna", reference = "4711" } }).Say("Sorry.");

            await RecipeBrowserTests.TurnAsync(site, "Anna, reference 4711");

            site.Observer.Steps("fill_order").Should().Equal(["Allowed/allowed", "Failed/exception"],
                "the reference's field is a password field: the fill stops there");
            (await site.Browser.EvaluateAsync("document.querySelector('[data-mentor-id=\"order.reference\"]').value", CancellationToken.None))
                .GetString().Should().BeEmpty("the runner never writes a password field");
            (await BoundAsync(site, o => o.Customer is not null)).Customer.Should().Be("Anna", "the fields before it are filled");
        }

        FormFillPage.LetIn.Add("reference");
        try
        {
            await using var site = await RecipeBrowserTests.OpenAsync(null, "fill");
            site.Chat.CallTool("fill_order", new { parameter = new { reference = "4711" } }).Say("Done.");

            await RecipeBrowserTests.TurnAsync(site, "reference 4711");

            site.Observer.Steps("fill_order").Should().Equal(["Allowed/allowed", "Executed/completed"]);
            (await BoundAsync(site, o => o.Reference is not null)).Reference.Should().Be("4711",
                "the application let the field in: the step may write a password field");
        }
        finally
        {
            FormFillPage.LetIn.Remove("reference");
        }
    }
}

/// <summary>
/// An order form as an application writes it for <c>RegisterForm</c>: each field with its <c>data-mentor-id</c>. It tells
/// the test its circuit's services, its model after every change and whether it was submitted, under the <c>run</c> of its
/// address.
/// </summary>
public sealed class FormFillPage : ComponentBase, IDisposable
{
    internal static readonly ConcurrentDictionary<string, Order> Bound = new();
    internal static readonly ConcurrentDictionary<string, bool> Submitted = new();

    /// <summary>The sensitive fields the next page lets in.</summary>
    internal static readonly HashSet<string> LetIn = [];

    public enum Country { Italy, France, Germany }

    public sealed class Order
    {
        public string? Customer { get; set; }
        public int Quantity { get; set; }
        public Country Country { get; set; }
        public bool Express { get; set; }
        public string? Reference { get; set; }
    }

    [Inject] private IServiceProvider Services { get; set; } = null!;
    [Inject] private NavigationManager Navigation { get; set; } = null!;
    [Inject] private IMentorPageContext PageContext { get; set; } = null!;
    [Inject] private IJSRuntime JS { get; set; } = null!;

    private readonly Order _order = new();

    private string Run => Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(Navigation.Uri).Query)
        .TryGetValue("run", out var run) ? run.ToString() : "";

    protected override void OnInitialized()
    {
        var letIn = LetIn.ToArray();
        PageContext.SetPageName("Order form")
            .RegisterForm<Order>("order", "The form of a new order", JS, o => o.IncludeSensitive(letIn));
        PageContext.SignalReady();
    }

    protected override void OnAfterRender(bool firstRender)
    {
        if (firstRender) RecipeFormPage.Circuits[Run] = Services;
        Bound[Run] = new Order
        {
            Customer = _order.Customer, Quantity = _order.Quantity, Country = _order.Country, Express = _order.Express,
            Reference = _order.Reference,
        };
    }

    public void Dispose()
    {
        PageContext.Clear("Order form");
        Bound.TryRemove(Run, out _);
        Submitted.TryRemove(Run, out _);
    }

    protected override void BuildRenderTree(RenderTreeBuilder b)
    {
        b.OpenComponent<EditForm>(0);
        b.AddAttribute(1, "Model", _order);
        b.AddAttribute(2, "OnSubmit", EventCallback.Factory.Create<EditContext>(this, () => Submitted[Run] = true));
        b.AddAttribute(3, "ChildContent", (RenderFragment<EditContext>)(_ => f =>
        {
            Input<InputText, string?>(f, 0, "customer", _order.Customer, v => _order.Customer = v, () => _order.Customer);
            Input<InputNumber<int>, int>(f, 10, "quantity", _order.Quantity, v => _order.Quantity = v, () => _order.Quantity);

            f.OpenComponent<InputSelect<Country>>(20);
            f.AddAttribute(21, "data-mentor-id", "order.country");
            f.AddAttribute(22, "Value", _order.Country);
            f.AddAttribute(23, "ValueChanged", EventCallback.Factory.Create<Country>(this, v => _order.Country = v));
            f.AddAttribute(24, "ValueExpression", (Expression<Func<Country>>)(() => _order.Country));
            f.AddAttribute(25, "ChildContent", (RenderFragment)(o => o.AddMarkupContent(0,
                "<option value=\"Italy\">Italy</option><option value=\"France\">France</option><option value=\"Germany\">Germany</option>")));
            f.CloseComponent();

            Input<InputCheckbox, bool>(f, 30, "express", _order.Express, v => _order.Express = v, () => _order.Express);
            Input<InputText, string?>(f, 40, "reference", _order.Reference, v => _order.Reference = v, () => _order.Reference, password: true);

            f.AddMarkupContent(50, "<button type=\"submit\" id=\"save\">Save</button>");
        }));
        b.CloseComponent();
    }

    private void Input<TInput, TValue>(RenderTreeBuilder f, int seq, string field, TValue value, Action<TValue> set,
        Expression<Func<TValue>> expression, bool password = false)
        where TInput : InputBase<TValue>
    {
#pragma warning disable ASP0006 // a region of its own for each field: the sequence numbers inside it are literals
        f.OpenRegion(seq);
#pragma warning restore ASP0006
        f.OpenComponent<TInput>(0);
        f.AddAttribute(1, "data-mentor-id", "order." + field);
        if (password) f.AddAttribute(2, "type", "password");
        f.AddAttribute(3, "Value", value);
        f.AddAttribute(4, "ValueChanged", EventCallback.Factory.Create<TValue>(this, set));
        f.AddAttribute(5, "ValueExpression", expression);
        f.CloseComponent();
        f.CloseRegion();
    }
}
