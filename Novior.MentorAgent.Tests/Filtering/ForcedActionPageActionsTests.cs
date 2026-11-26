using FluentAssertions;
using Microsoft.Extensions.AI;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Tests.App;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Xunit;

namespace Novior.MentorAgent.Tests.Filtering;

/// <summary>
/// BUG-251 — <c>RequireActionForChangeRequests</c> forced a request for one of the page's own actions onto a
/// confirmation-gated action of the application. On a page with a grid, "Togli il filtro e ordina per priorità" (in
/// Italian "ordina" also means "place an order") was a complete request for <c>update_order_status</c> to the
/// classifier, which was not told the page has actions of its own; the forced request declares the actions only, never
/// a UI action, so the model called the order's action and only the confirmation stopped it. BUG-247 had shown the page's
/// actions with a record question only. They are now shown with the actions too: a request for what one of them does is
/// not forced, and a complete request for one of the actions is, page or not. A forced action always asks for a
/// confirmation: only those can be required (<c>IsForcibleAction</c>).
/// </summary>
public sealed class ForcedActionPageActionsTests
{
    private const string SortRequest = "Togli il filtro e ordina per priorità";
    private const string Refund = "Rimborsa l'ordine 1001";
    private const string PageActionsRule = "The page the user has open has actions of its own";

    private static readonly string[] ToolTexts =
    [
        "cancel_order: Cancels an existing order",
        "issue_refund: Issues a refund",
        "export_ledger: Exports the order ledger",
        "count_open_orders: Counts open orders",
        "show_order_card: Shows an order as a card",
        "get_weather: Returns the weather for a city",
        "get_active_orders: Returns the list of active orders",
        "failing_action: Always fails, for exception mapping",
        "mark_order_reviewed: Marks an order as reviewed",
        "get_policy_for_category: Returns the reimbursement policy for a category",
        "submit_expense_report: Submits a new expense report",
    ];

    private static MentorHostFixture Host()
    {
        var host = MentorHostFixture.Create(o =>
        {
            o.EnableToolFiltering = true;
            o.ToolFilterMinScore = 0.35f;
            o.MemoryAutoCapture = false;
            o.RequireActionForChangeRequests = true;
        }, withEmbeddings: true);
        // Both messages select issue_refund, as the order actions ranked first for "ordina" on the sample.
        var emb = host.Embeddings!;
        foreach (var q in new[] { SortRequest, Refund }) emb.OnAxis(q, 0);
        foreach (var t in ToolTexts) emb.OnAxis(t, 1);
        emb.AtSimilarity("issue_refund: Issues a refund", 0, 0.70f);
        return host;
    }

    /// <summary>
    /// The classifier: OTHER for the sort request when it is shown a page action that does it, ACTION otherwise, as the
    /// model measured does. What it was asked is kept.
    /// </summary>
    private static FakeChatClient Classifier(FakeChatClient chat, List<string> asked) => chat.Respond((messages, _) =>
    {
        var question = messages[0].Text ?? "";
        asked.Add(question);
        var page = question.Contains(PageActionsRule, StringComparison.Ordinal) && question.Contains("- sort_tickets", StringComparison.Ordinal);
        var answer = page && messages[^1].Text == SortRequest ? "OTHER" : "ACTION";
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, answer));
    });

    private static void Page(MentorHostFixture host, List<string> sorted)
        => host.Get<IMentorPageContext>().SetPageName("Tickets")
            .RegisterUIAction<string>("sort_tickets", "The tickets: sorts the rows by a column", sorted.Add)
            .SignalReady();

    private static IReadOnlyList<FakeChatClient.RecordedCall> CoordinatorCalls(MentorHostFixture host)
        => host.Chat!.Calls.Where(c => c.ToolNames.Count > 0).ToList();

    private static Func<int> DeclineBanners(MentorHostFixture host)
    {
        var raised = 0;
        var state = host.Get<IMentorStateService>();
        state.OnConfirmationRequired += req =>
        {
            Interlocked.Increment(ref raised);
            state.Cancel(req.ActionId);
        };
        return () => Volatile.Read(ref raised);
    }

    [Fact]
    public async Task A_request_for_what_a_page_action_does_is_not_forced_onto_a_confirmation_gated_action()
    {
        await using var host = Host();
        var asked = new List<string>();
        var sorted = new List<string>();
        Page(host, sorted);
        var banners = DeclineBanners(host);
        Classifier(host.Chat!, asked).CallTool("sort_tickets", new { parameter = "priority" }).Say("Ordinate per priorità.");

        await host.RunTurnAsync(SortRequest);

        asked.Should().ContainSingle().Which.Should().Contain(PageActionsRule).And.Contain("is OTHER, not ACTION")
            .And.Contain("- issue_refund").And.Contain("- sort_tickets: The tickets: sorts the rows by a column");
        var first = CoordinatorCalls(host)[0];
        first.ToolMode.Should().NotBe(ChatToolMode.RequireAny, "the page's action serves the request: nothing is forced");
        first.ToolNames.Should().Contain("sort_tickets");
        sorted.Should().Equal("priority");
        banners().Should().Be(0, "no action of the application was called");
        host.Get<TestOrderService>().Invoked.Should().BeEmpty();
    }

    [Fact]
    public async Task A_complete_request_for_a_confirmation_gated_action_is_still_forced_on_a_page_with_actions()
    {
        await using var host = Host();
        var asked = new List<string>();
        Page(host, []);
        var banners = DeclineBanners(host);
        Classifier(host.Chat!, asked).CallTool("issue_refund", new { orderId = 1001 }).Say("Il rimborso non è stato emesso.");

        await host.RunTurnAsync(Refund);

        asked.Should().ContainSingle().Which.Should().Contain(PageActionsRule);
        var first = CoordinatorCalls(host)[0];
        first.ToolMode.Should().Be(ChatToolMode.RequireAny);
        first.ToolNames.Should().Contain("issue_refund").And.NotContain("sort_tickets", "a forced action is satisfied by an action only");
        banners().Should().Be(1, "the forced action asks for its confirmation");
    }

    [Fact]
    public async Task Without_page_actions_the_classifier_is_asked_the_question_of_the_actions_alone()
    {
        await using var host = Host();
        var asked = new List<string>();
        DeclineBanners(host);
        Classifier(host.Chat!, asked).CallTool("issue_refund", new { orderId = 1001 }).Say("Il rimborso non è stato emesso.");

        await host.RunTurnAsync(Refund);

        asked.Should().ContainSingle().Which.Should().NotContain(PageActionsRule);
        CoordinatorCalls(host)[0].ToolMode.Should().Be(ChatToolMode.RequireAny);
    }
}
