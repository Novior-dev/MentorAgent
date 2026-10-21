using FluentAssertions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Blazor.Services;
using Novior.MentorAgent.Core;
using Xunit;

namespace Novior.MentorAgent.Tests.Pages;

/// <summary>
/// BUG-192 — a page's <c>Dispose</c> cleared the actions of the page that replaced it.
///
/// <para>
/// The READMEs taught <c>Dispose() => PageContext.Clear()</c>. Blazor disposes the previous page after it has
/// initialized the next one, so going from one page with UI actions to another, the previous page's Clear() ran
/// after the new page had registered its actions, and wiped them: the assistant then answered that the action did
/// not exist. Found live in the demo, from the claims list to the new-claim form. <c>Clear(pageName)</c> removes
/// what that page registered and nothing else.
/// </para>
/// </summary>
public sealed class PageSwitchClearTests
{
    public static TheoryData<string> Contexts => new() { "Server", "WebAssembly" };

    private static IMentorPageContext New(string kind)
        => kind == "Server" ? new MentorPageContext() : new WasmMentorPageContext();

    [Theory]
    [MemberData(nameof(Contexts))]
    public void The_previous_pages_clear_leaves_the_new_page_whole(string kind)
    {
        var ctx = New(kind);
        ctx.SetPageName("Expenses").Set("ClaimsShown", 6).RegisterUIAction("highlight_expense", "d", _ => { });

        // Blazor's order on a navigation: the new page's OnInitialized first...
        ctx.SetPageName("New expense").Set("Draft", "empty").RegisterUIAction("fill_expense_form", "d", _ => { });
        // ...then the previous page's Dispose.
        ctx.Clear("Expenses");

        ctx.PageName.Should().Be("New expense");
        ctx.UIActions.Keys.Should().Equal(["fill_expense_form"],
            "the new page's action must survive, and the previous page's must not reach the model");
        ctx.ContextData.Keys.Should().Equal(["Draft"]);
    }

    [Theory]
    [MemberData(nameof(Contexts))]
    public void A_key_or_an_action_both_pages_register_stays_with_the_new_page(string kind)
    {
        var ctx = New(kind);
        ctx.SetPageName("Orders").Set("Filter", "Pending").RegisterUIAction("highlight_row", "Highlights an order", _ => { });

        ctx.SetPageName("Customers").Set("Filter", "Active").RegisterUIAction("highlight_row", "Highlights a customer", _ => { });
        ctx.Clear("Orders");

        ctx.ContextData["Filter"].Should().Be("Active");
        ctx.UIActions["highlight_row"].Description.Should().Be("Highlights a customer");
    }

    [Theory]
    [MemberData(nameof(Contexts))]
    public void Clearing_the_page_still_shown_empties_the_context(string kind)
    {
        // From a page with actions to one that does not use the page context: nobody else has taken it over.
        var ctx = New(kind);
        ctx.SetPageName("Expenses").Set("ClaimsShown", 6).RegisterUIAction("highlight_expense", "d", _ => { });

        ctx.Clear("Expenses");

        ctx.PageName.Should().BeNull();
        ctx.ContextData.Should().BeEmpty();
        ctx.UIActions.Should().BeEmpty();
    }

    [Fact]
    public async Task The_new_pages_ready_signal_survives_the_previous_pages_clear()
    {
        IMentorPageContext ctx = new MentorPageContext();
        ctx.SetPageName("Expenses").SignalReady();

        ctx.SetPageName("New expense").RegisterUIAction("fill_expense_form", "d", _ => { }).SignalReady();
        ctx.Clear("Expenses");

        (await ctx.WaitForPageReadyAsync(50)).Should().BeTrue(
            "the page on screen has signalled, and the page that left must not take its signal back");
    }

    [Fact]
    public async Task Clearing_the_page_still_shown_takes_the_ready_signal_back()
    {
        IMentorPageContext ctx = new MentorPageContext();
        ctx.SetPageName("Expenses").SignalReady();

        ctx.Clear("Expenses");

        (await ctx.WaitForPageReadyAsync(50)).Should().BeFalse();
    }

    [Fact]
    public void An_implementation_without_its_own_Clear_by_page_clears_only_the_page_still_shown()
    {
        // The interface's default body, for an IMentorPageContext written against 1.0.0.
        IMentorPageContext ctx = new WithoutClearByPage();
        ctx.SetPageName("Expenses").RegisterUIAction("highlight_expense", "d", _ => { });
        ctx.SetPageName("New expense").RegisterUIAction("fill_expense_form", "d", _ => { });

        ctx.Clear("Expenses");
        ctx.UIActions.Should().ContainKey("fill_expense_form");

        ctx.Clear("New expense");
        ctx.UIActions.Should().BeEmpty();
    }

    /// <summary>Implements every member but <c>Clear(string)</c>, as an implementation written before it would.</summary>
    private sealed class WithoutClearByPage : IMentorPageContext
    {
        private readonly MentorPageContext _inner = new();

        public IMentorPageContext SetPageName(string pageName) { _inner.SetPageName(pageName); return this; }
        public IMentorPageContext Set(string key, object? value) { _inner.Set(key, value); return this; }
        public IMentorPageContext Remove(string key) { _inner.Remove(key); return this; }
        public void Clear() => _inner.Clear();

        public IMentorPageContext RegisterUIAction(string actionName, string description, Action<object?> handler)
        { _inner.RegisterUIAction(actionName, description, handler); return this; }

        public IMentorPageContext RegisterUIAction<TParam>(string actionName, string description, Action<TParam> handler, string? parameterHint = null)
        { _inner.RegisterUIAction(actionName, description, handler, parameterHint); return this; }

        public IMentorPageContext RegisterUIActionAsync(string actionName, string description, Func<object?, Task> handler, string? parameterHint = null)
        { _inner.RegisterUIActionAsync(actionName, description, handler, parameterHint); return this; }

        public IMentorPageContext RegisterUIActionAsync<TParam>(string actionName, string description, Func<TParam, Task> handler, string? parameterHint = null)
        { _inner.RegisterUIActionAsync(actionName, description, handler, parameterHint); return this; }

        public IMentorPageContext UnregisterUIAction(string actionName) { _inner.UnregisterUIAction(actionName); return this; }
        public IMentorPageContext SignalReady() { _inner.SignalReady(); return this; }
        public Task<bool> WaitForPageReadyAsync(int timeoutMs = 2000, CancellationToken ct = default) => _inner.WaitForPageReadyAsync(timeoutMs, ct);

        public string? PageName => _inner.PageName;
        public IReadOnlyDictionary<string, object?> ContextData => _inner.ContextData;
        public IReadOnlyDictionary<string, UIActionRegistration> UIActions => _inner.UIActions;
    }
}
