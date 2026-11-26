using FluentAssertions;
using Microsoft.Extensions.AI;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Xunit;

namespace Novior.MentorAgent.Tests.Filtering;

/// <summary>
/// BUG-247 — <c>RequireLookupForRecordQuestions</c> forced a request for one of the page's own actions onto the
/// application's reads. "Mostra solo quelle della stampante" and "Apri la segnalazione 5", on a page with a grid
/// (story 1319), were record questions for the classifier, which was never told the page has actions of its own; the
/// forced request declares the reads only, never a UI action, so the model called a read that had nothing to do with
/// the page and answered "no ticket found", or showed unrelated order cards before opening the row. The classifier is
/// now shown the page's actions offered on the turn, and a request for what one of them does is not forced: the turn
/// has every tool. A question about a value is still forced onto the reads, page or not. Without page actions the
/// question is the one measured for BUG-106, word for word. With the tool filter and without it.
/// </summary>
public sealed class ForcedReadPageActionsTests
{
    private const string PageRequest = "Apri la segnalazione 5";
    private const string ValueQuestion = "Quanti ordini sono aperti?";
    private const string PageActionsRule = "The page the user has open has actions of its own";

    private static readonly string[] Reads = ["count_open_orders", "get_active_orders", "show_order_card"];

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

    private static MentorHostFixture Host(bool filtering)
    {
        var host = MentorHostFixture.Create(o =>
        {
            o.RequireLookupForRecordQuestions = true;
            o.IsReadOnlyAction = name => Reads.Contains(name);
            o.MemoryAutoCapture = false;
            if (filtering)
            {
                o.EnableToolFiltering = true;
                o.ToolFilterMinScore = 0.35f;
            }
        }, withEmbeddings: filtering);
        host.Auth.SignIn("ada", "Manager", "Admin");
        if (filtering)
        {
            // Nothing clears the line: the zero-match fallback sends the reads, as on the sample.
            var emb = host.Embeddings!;
            foreach (var q in new[] { PageRequest, ValueQuestion }) emb.OnAxis(q, 0);
            foreach (var t in ToolTexts) emb.OnAxis(t, 1);
            emb.AtSimilarity("count_open_orders: Counts open orders", 0, 0.209f)
               .AtSimilarity("get_active_orders: Returns the list of active orders", 0, 0.198f)
               .AtSimilarity("show_order_card: Shows an order as a card", 0, 0.174f);
        }
        return host;
    }

    /// <summary>
    /// The classifier: it answers OTHER for the page request when it is shown a page action that does it, LOOKUP
    /// otherwise, as the model measured does. What it was asked is kept.
    /// </summary>
    private static FakeChatClient Classifier(FakeChatClient chat, List<string> asked) => chat.Respond((messages, _) =>
    {
        var question = messages[0].Text ?? "";
        asked.Add(question);
        var page = question.Contains(PageActionsRule, StringComparison.Ordinal) && question.Contains("- open_tickets_row", StringComparison.Ordinal);
        var answer = page && messages[^1].Text == PageRequest ? "OTHER" : "LOOKUP";
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, answer));
    });

    private static void Page(MentorHostFixture host, List<string> opened)
        => host.Get<IMentorPageContext>().SetPageName("Tickets")
            .RegisterUIAction<int>("open_tickets_row", "The tickets: opens the row whose id is the key", id => opened.Add(id.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .SignalReady();

    private static IReadOnlyList<FakeChatClient.RecordedCall> CoordinatorCalls(MentorHostFixture host)
        => host.Chat!.Calls.Where(c => c.ToolNames.Count > 0).ToList();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_request_for_what_a_page_action_does_is_not_forced_onto_the_reads(bool filtering)
    {
        await using var host = Host(filtering);
        var asked = new List<string>();
        var opened = new List<string>();
        Page(host, opened);
        Classifier(host.Chat!, asked).CallTool("open_tickets_row", new { parameter = 5 }).Say("La segnalazione 5 è aperta.");

        await host.RunTurnAsync(PageRequest);

        asked.Should().ContainSingle().Which.Should().Contain(PageActionsRule)
            .And.Contain("- open_tickets_row: The tickets: opens the row whose id is the key");
        var first = CoordinatorCalls(host)[0];
        first.ToolMode.Should().NotBe(ChatToolMode.RequireAny, "the page's action serves the request: nothing is forced");
        first.ToolNames.Should().Contain("open_tickets_row");
        opened.Should().Equal("5");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_question_about_a_value_is_still_forced_onto_the_reads_on_a_page_with_actions(bool filtering)
    {
        await using var host = Host(filtering);
        var asked = new List<string>();
        Page(host, []);
        Classifier(host.Chat!, asked).CallTool("count_open_orders").Say("Sono 3.");

        await host.RunTurnAsync(ValueQuestion);

        asked.Should().ContainSingle().Which.Should().Contain(PageActionsRule);
        var first = CoordinatorCalls(host)[0];
        first.ToolMode.Should().Be(ChatToolMode.RequireAny);
        first.ToolNames.Should().Contain(Reads).And.NotContain("open_tickets_row", "a forced read is satisfied by a read only");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Without_page_actions_the_classifier_is_asked_the_question_measured_for_reads(bool filtering)
    {
        await using var host = Host(filtering);
        var asked = new List<string>();
        Classifier(host.Chat!, asked).CallTool("count_open_orders").Say("Sono 3.");

        await host.RunTurnAsync(ValueQuestion);

        asked.Should().ContainSingle().Which.Should().NotContain(PageActionsRule).And.NotContain("open_tickets_row");
        CoordinatorCalls(host)[0].ToolMode.Should().Be(ChatToolMode.RequireAny);
    }

    // ── The question ───────────────────────────────────────────────────────────

    [Fact]
    public void The_page_actions_are_listed_after_the_record_question_and_the_rule_gives_no_example_of_a_request()
    {
        var reads = new RequestShape(Lookup: true, [], Navigation: false);
        var withPage = reads with { PageActions = ["- open_tickets_row: The tickets: opens a row", "- sort_tickets: The tickets: sorts the rows"] };

        var question = RequestClassifier.Question(["OrderAgent"], withPage);
        var plain = RequestClassifier.Question(["OrderAgent"], reads);

        question.Should().Contain(PageActionsRule + ", which the assistant carries out on that page:\n- open_tickets_row: The tickets: opens a row\n- sort_tickets: The tickets: sorts the rows\n")
            .And.Contain("is OTHER, not LOOKUP").And.Contain("A question about a value the records hold is still LOOKUP");
        question.Replace(question[question.IndexOf(PageActionsRule, StringComparison.Ordinal)..(question.IndexOf("even when the open page shows those records. ", StringComparison.Ordinal) + "even when the open page shows those records. ".Length)], "")
            .Should().Be(plain, "the rest is the question measured for reads, word for word");
        question.Should().NotContain("\"", "no request is quoted as an example: it would pull its look-alikes into the label");

        // BUG-251: with the actions offered, the rule is there too, after them, and says what is offered.
        RequestClassifier.Question(["OrderAgent"], new RequestShape(false, ["- set_stock: Sets the stock. Inputs: none."], false)
            { PageActions = ["- open_tickets_row: opens a row"] }).Should().Contain(PageActionsRule).And.Contain("is OTHER, not ACTION. ")
            .And.NotContain("not LOOKUP");
        RequestClassifier.Question(["OrderAgent"], new RequestShape(false, [], true) { PageActions = ["- open_tickets_row: opens a row"] })
            .Should().NotContain(PageActionsRule, "navigation alone has its own question");
    }
}
