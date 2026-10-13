using FluentAssertions;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.AI;
using Xunit;

namespace Novior.MentorAgent.Tests.Filtering;

/// <summary>
/// Two answers to what the rc.14 live acceptance left open on turns where no action clears the threshold.
///
/// <para>
/// <b>The forced read</b> (<c>RequireLookupForRecordQuestions</c>, opt-in): with the read actions on the turn
/// the model still answered "I can't see the price, shall I look it up?" about one time in three (BUG-106's
/// residue), and stronger wording made it invent figures. When a classifier says the message asks for a value
/// of the records, the first call must call a declared read — or hand over to a specialist.
/// </para>
///
/// <para>
/// <b>The carried selection</b> (OSS-01): the answer to the assistant's own question — "Sì, procedi" scores
/// 0.29 against <c>cancel_order</c> — matched nothing and arrived with none of the actions the question was
/// about. The previous message's selection now travels again on such a message, one turn back only.
/// </para>
/// </summary>
public sealed class ForcedReadAndCarryTests
{
    private const int Relevant = 0;
    private const int Unrelated = 1;

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

    private static readonly string[] Reads = ["count_open_orders", "get_active_orders", "show_order_card"];

    private static MentorHostFixture Host(Action<Novior.MentorAgent.Options.MentorOptions>? configure = null)
        => MentorHostFixture.Create(o =>
        {
            o.EnableToolFiltering = true;
            o.ToolFilterMinScore = 0.35f;
            o.MemoryAutoCapture = false;
            configure?.Invoke(o);
        }, withEmbeddings: true);

    private static MentorHostFixture Forcing(Action<Novior.MentorAgent.Options.MentorOptions>? configure = null)
        => Host(o =>
        {
            o.RequireLookupForRecordQuestions = true;
            o.IsReadOnlyAction = name => Reads.Contains(name);
            configure?.Invoke(o);
        });

    /// <summary>The Dell shape: nothing clears 0.35, a gated action ranks first, three reads behind it.</summary>
    private static void SeedNothingMatches(MentorHostFixture host, params string[] queries)
    {
        var emb = host.Embeddings!;
        foreach (var q in queries) emb.OnAxis(q, Relevant);
        foreach (var t in ToolTexts) emb.OnAxis(t, Unrelated);
        emb.AtSimilarity("cancel_order: Cancels an existing order", Relevant, 0.319f)
           .AtSimilarity("count_open_orders: Counts open orders", Relevant, 0.209f)
           .AtSimilarity("get_active_orders: Returns the list of active orders", Relevant, 0.198f)
           .AtSimilarity("show_order_card: Shows an order as a card", Relevant, 0.174f);
    }

    private static IReadOnlyList<Novior.MentorAgent.Tests.Fakes.FakeChatClient.RecordedCall> CoordinatorCalls(MentorHostFixture host)
        => host.Chat!.Calls.Where(c => c.ToolNames.Count > 0).ToList();

    // ── The forced read ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_record_question_that_matched_nothing_must_first_call_a_declared_read()
    {
        const string query = "Quanto costa il Dell XPS 15?";
        await using var host = Forcing();
        SeedNothingMatches(host, query);
        host.Chat!.Say("LOOKUP").CallTool("count_open_orders").Say("Sono 3.");

        await host.RunTurnAsync(query);

        var calls = CoordinatorCalls(host);
        calls[0].ToolMode.Should().Be(ChatToolMode.RequireAny);
        calls[0].ToolNames.Should().BeSubsetOf([.. Reads, "route_to_specialist", "business_analysis_team"],
            "nothing that writes, navigates, loads a skill or remembers can satisfy the requirement — a specialist or a team is the way out");
        calls[0].ToolNames.Should().Contain(["count_open_orders", "get_active_orders", "show_order_card"]);
    }

    [Fact]
    public async Task A_read_that_cleared_the_line_on_its_own_is_the_one_the_first_call_must_make()
    {
        // Live on the candidate: get_product cleared 0.35 alone for the Dell question, and the model wrote
        // "price not available" without calling it. Forced among this message's own selection only: with a
        // lone right match, the reads ranked below it are not offered (rc.12: company cost Anna Ferrari's answer).
        const string query = "How many orders are open?";
        await using var host = Forcing();
        var emb = host.Embeddings!;
        emb.OnAxis(query, Relevant);
        foreach (var t in ToolTexts) emb.OnAxis(t, Unrelated);
        emb.AtSimilarity("count_open_orders: Counts open orders", Relevant, 0.68f)
           .AtSimilarity("get_active_orders: Returns the list of active orders", Relevant, 0.30f);
        host.Chat!.Say("LOOKUP").CallTool("count_open_orders").Say("Sono 3.");

        await host.RunTurnAsync(query);

        var first = CoordinatorCalls(host)[0];
        first.ToolMode.Should().Be(ChatToolMode.RequireAny);
        first.ToolNames.Should().Contain("count_open_orders");
        first.ToolNames.Should().NotContain("get_active_orders", "it did not clear the line, and this message selected one read");
    }

    [Fact]
    public async Task After_the_read_the_turn_continues_with_its_whole_selection()
    {
        const string query = "Quanto costa il Dell XPS 15?";
        await using var host = Forcing();
        SeedNothingMatches(host, query);
        host.Chat!.Say("LOOKUP").CallTool("count_open_orders").Say("Sono 3.");

        await host.RunTurnAsync(query);

        var calls = CoordinatorCalls(host);
        calls.Should().HaveCountGreaterThan(1);
        calls[1].ToolMode.Should().NotBe(ChatToolMode.RequireAny, "only the first call is forced");
        calls[1].ToolNames.Should().Contain("count_open_orders", "the read that ran stays declared (call_id)");
        calls[1].ToolNames.Should().Contain("navigate_to", "the core tools are back for the answer");
    }

    [Fact]
    public async Task A_message_the_classifier_calls_OTHER_is_not_forced()
    {
        const string query = "Ciao";
        await using var host = Forcing();
        SeedNothingMatches(host, query);
        host.Chat!.Say("OTHER").Say("Ciao!");

        await host.RunTurnAsync(query);

        CoordinatorCalls(host)[0].ToolMode.Should().NotBe(ChatToolMode.RequireAny);
    }

    [Fact]
    public async Task An_unreadable_classifier_answer_leaves_the_turn_as_without_the_option()
    {
        const string query = "Quanto costa il Dell XPS 15?";
        await using var host = Forcing();
        SeedNothingMatches(host, query);
        host.Chat!.Say("Sure, here is the answer").Say("ok");

        await host.RunTurnAsync(query);

        CoordinatorCalls(host)[0].ToolMode.Should().NotBe(ChatToolMode.RequireAny);
        host.Logs.Records.Should().Contain(r => r.Message.Contains("Request classifier → unreadable"));
    }

    [Fact]
    public async Task A_read_only_claim_on_a_confirmation_gated_action_is_ignored()
    {
        // Confirmation is the mark of an action that changes data: it wins over a read-only claim. Asked of the
        // declaration itself — the zero-match eligibility would exclude the action from forcing anyway, and
        // the MCP surface withholds it, so only this answer pins the rule.
        const string query = "Quanto costa il Dell XPS 15?";
        await using var host = Forcing(o => o.IsReadOnlyAction = name => name is "issue_refund" or "count_open_orders");
        SeedNothingMatches(host, query);
        host.Chat!.Say("LOOKUP").CallTool("count_open_orders").Say("ok");

        await host.RunTurnAsync(query);

        var declarations = host.Get<Novior.MentorAgent.Core.MentorConfirmationBuilder>();
        declarations.IsDeclaredReadOnly("issue_refund").Should().BeFalse("it needs confirmation");
        declarations.IsDeclaredReadOnly("count_open_orders").Should().BeTrue();
        CoordinatorCalls(host)[0].ToolMode.Should().Be(ChatToolMode.RequireAny);
        CoordinatorCalls(host)[0].ToolNames.Should().NotContain("issue_refund");
    }

    [Fact]
    public async Task A_forced_read_adds_no_ui_action_and_its_marker_never_reaches_the_provider()
    {
        const string query = "Quanto costa il Dell XPS 15?";
        await using var host = Forcing();
        host.Get<Novior.MentorAgent.Abstractions.Interfaces.IMentorPageContext>()
            .RegisterUIAction("highlight_row", "Highlights a row", _ => { });
        SeedNothingMatches(host, query);
        host.Chat!.Say("LOOKUP").CallTool("count_open_orders").Say("Sono 3.");

        await host.RunTurnAsync(query);

        var first = CoordinatorCalls(host)[0];
        first.ToolMode.Should().Be(ChatToolMode.RequireAny);
        first.ToolNames.Should().NotContain("highlight_row", "a UI action must not be able to satisfy the requirement");
        host.Chat.Calls.Should().OnlyContain(c => !c.AdditionalPropertyKeys.Contains(Novior.MentorAgent.Middleware.ToolFilterChatClient.ForcedCallKey),
            "the marker is MentorAgent's own, and some provider clients send every additional property");
        CoordinatorCalls(host)[^1].ToolNames.Should().Contain("highlight_row", "after the read the UI actions are back");
    }

    [Fact]
    public async Task A_forced_read_keeps_the_teams_as_a_way_out()
    {
        // The classifier is told the teams' names, but can still miss "ask the analysis team…": the request
        // must then still be able to reach the team, as it can reach a specialist.
        const string query = "Quanto costa il Dell XPS 15?";
        await using var host = Forcing();
        SeedNothingMatches(host, query);
        host.Chat!.Say("LOOKUP").CallTool("count_open_orders").Say("Sono 3.");

        await host.RunTurnAsync(query);

        CoordinatorCalls(host)[0].ToolNames.Should().Contain("business_analysis_team");
    }

    [Fact]
    public async Task After_Stop_at_a_banner_the_next_message_is_ranked_on_its_own_and_not_forced()
    {
        // BUG-116's recovery: the answer to the request left open is appended AFTER the new message, so the
        // request ended on a tool result and the filter took it for the stopped turn's continuation — the
        // refund's selection replayed, the new question neither ranked nor forced (rc.14 review). Since BUG-118's
        // review that turn is not forced at all: the stopped action is declared for its answer, and a required call
        // could be satisfied by calling it again — the banner the user stopped would come back.
        const string refund = "Rimborsa l'ordine 1001";
        const string count = "How many orders are open?";
        await using var host = Forcing(o => o.HitlMode = Novior.MentorAgent.Options.MentorHitlMode.Native);
        // The refund clears the line on axis 0 with count_open_orders as its first companion; the count question
        // (axis 2) selects count_open_orders alone; "Sì" (axis 4) matches nothing.
        var emb = host.Embeddings!;
        emb.OnAxis(refund, 0).OnAxis(count, 2).OnAxis("Sì", 4);
        foreach (var t in ToolTexts) emb.OnAxis(t, Unrelated);
        emb.Exactly("issue_refund: Issues a refund", 0.70f, 0.714f, 0, 0, 0, 0, 0, 0)
           .Exactly("count_open_orders: Counts open orders", 0.20f, 0, 0.68f, 0, 0, 0, 0, 0.7054f);
        host.Chat!.Say("OTHER").CallTool("issue_refund", new { orderId = 1001 })
            .CallTool("count_open_orders").Say("Sono 3.")
            .Say("OTHER").Say("Prego.");
        var stopped = false;
        host.Get<Novior.MentorAgent.Abstractions.Interfaces.IMentorStateService>().OnConfirmationRequired += _ =>
        {
            if (stopped) return;
            stopped = true;
            host.Orchestrator.CancelCurrentRequest();
        };

        await host.RunTurnAsync(refund);
        var before = CoordinatorCalls(host).Count;
        (await host.RunTurnAsync(count)).Should().BeNull();

        var recovery = CoordinatorCalls(host)[before];
        recovery.ToolMode.Should().NotBe(ChatToolMode.RequireAny, "the turn that answers what a Stop left open is never forced");
        host.Chat.Calls.Count(c => c.ToolNames.Count == 0).Should().Be(1,
            "the classifier was asked on the first message, and not on the recovery turn");
        recovery.ToolNames.Should().Contain("count_open_orders", "ranked on the new message");
        recovery.ToolNames.Should().Contain("issue_refund", "the request answered on this call keeps its tool declared");

        var beforeThird = CoordinatorCalls(host).Count;
        await host.RunTurnAsync("Sì");
        CoordinatorCalls(host)[beforeThird].ToolNames.Should().NotContain("issue_refund",
            "the carried selection is the previous message's, never the one before it");
    }

    [Fact]
    public async Task Off_by_default_no_classifier_is_asked()
    {
        const string query = "Quanto costa il Dell XPS 15?";
        await using var host = Host(o => o.IsReadOnlyAction = name => Reads.Contains(name));
        SeedNothingMatches(host, query);
        host.Chat!.Say("ok");

        await host.RunTurnAsync(query);

        host.Chat.Calls.Should().OnlyContain(c => c.ToolNames.Count > 0, "no tool-less classifier call was made");
        host.Chat.Calls[0].ToolMode.Should().NotBe(ChatToolMode.RequireAny);
    }

    [Fact]
    public async Task Without_a_declared_read_only_action_nothing_is_forced_and_a_warning_says_so()
    {
        const string query = "Quanto costa il Dell XPS 15?";
        // The test app's one declared read is gated here: confirmation wins, so nothing is left to force.
        await using var host = Host(o =>
        {
            o.RequireLookupForRecordQuestions = true;
            o.RequiresApproval = name => name == "count_open_orders";
        });
        SeedNothingMatches(host, query);
        host.Chat!.Say("ok");

        await host.RunTurnAsync(query);

        host.Logs.HasWarning("no action is declared read-only").Should().BeTrue();
        CoordinatorCalls(host)[0].ToolMode.Should().NotBe(ChatToolMode.RequireAny);
    }

    [Fact]
    public async Task Without_tool_filtering_the_option_warns_that_it_has_no_effect()
    {
        await using var host = MentorHostFixture.Create(o =>
        {
            o.RequireLookupForRecordQuestions = true;
            o.MemoryAutoCapture = false;
        });
        host.Chat!.Say("ok");

        await host.RunTurnAsync("Quanto costa il Dell XPS 15?");

        host.Logs.HasWarning("RequireLookupForRecordQuestions is on but options.EnableToolFiltering is off").Should().BeTrue();
    }

    [Fact]
    public async Task A_read_only_action_is_declared_by_attribute_too()
    {
        var host = MentorHostFixture.Create();
        await using var _ = host;
        var confirmations = host.Get<Novior.MentorAgent.Core.MentorConfirmationBuilder>();

        confirmations.IsDeclaredReadOnly("count_open_orders").Should().BeTrue(
            "the test app marks it [MentorAction(ReadOnly = true)]");
        confirmations.IsDeclaredReadOnly("get_active_orders").Should().BeFalse("not declared, and no predicate");
        confirmations.IsDeclaredReadOnly("issue_refund").Should().BeFalse("needs confirmation");
    }

    // ── The carried selection ───────────────────────────────────────────────────

    [Fact]
    public async Task A_gated_action_the_user_asked_for_travels_on_the_answer_to_the_assistants_question()
    {
        const string ask = "cancel order 1001";
        const string yes = "Sì, procedi";
        await using var host = Host();
        var emb = host.Embeddings!;
        emb.OnAxis(ask, Relevant);
        emb.OnAxis(yes, Unrelated + 2);
        foreach (var t in ToolTexts) emb.OnAxis(t, Unrelated);
        emb.AtSimilarity("cancel_order: Cancels an existing order", Relevant, 0.68f);
        host.Chat!.Say("Qual è il motivo?").Say("ok");

        await host.RunTurnAsync(ask);
        await host.RunTurnAsync(yes);

        var second = CoordinatorCalls(host)[^1];
        second.ToolNames.Should().Contain("cancel_order", "the answer to the assistant's question carries what it was about");
        second.ToolNames.Should().Contain(n => n != "cancel_order" && ToolTexts.Any(t => t.StartsWith(n + ":")),
            "a confirmation-gated action never travels alone (BUG-099)");
    }

    [Fact]
    public async Task A_carried_action_travels_one_turn_only()
    {
        const string ask = "cancel order 1001";
        await using var host = Host();
        var emb = host.Embeddings!;
        emb.OnAxis(ask, Relevant);
        emb.OnAxis("Sì", Unrelated + 2);
        emb.OnAxis("ok grazie", Unrelated + 2);
        foreach (var t in ToolTexts) emb.OnAxis(t, Unrelated);
        emb.AtSimilarity("cancel_order: Cancels an existing order", Relevant, 0.68f);
        host.Chat!.Say("Qual è il motivo?").Say("ok").Say("prego");

        await host.RunTurnAsync(ask);
        await host.RunTurnAsync("Sì");
        await host.RunTurnAsync("ok grazie");

        CoordinatorCalls(host)[^1].ToolNames.Should().NotContain("cancel_order",
            "what a message carried is not carried again");
    }

    [Fact]
    public async Task A_message_that_matches_an_action_carries_nothing()
    {
        const string first = "cancel order 1001";
        const string second = "what is the weather in Rome?";
        await using var host = Host();
        var emb = host.Embeddings!;
        emb.OnAxis(first, Relevant);
        emb.OnAxis(second, Relevant + 3);
        foreach (var t in ToolTexts) emb.OnAxis(t, Unrelated);
        emb.AtSimilarity("cancel_order: Cancels an existing order", Relevant, 0.68f);
        emb.AtSimilarity("get_weather: Returns the weather for a city", Relevant + 3, 0.70f);
        host.Chat!.Say("Qual è il motivo?").Say("Sole.");

        await host.RunTurnAsync(first);
        await host.RunTurnAsync(second);

        CoordinatorCalls(host)[^1].ToolNames.Should().NotContain("cancel_order");
    }

    [Fact]
    public async Task A_new_conversation_carries_nothing()
    {
        const string ask = "cancel order 1001";
        const string yes = "Sì, procedi";
        await using var host = Host();
        var emb = host.Embeddings!;
        emb.OnAxis(ask, Relevant);
        emb.OnAxis(yes, Unrelated + 2);
        foreach (var t in ToolTexts) emb.OnAxis(t, Unrelated);
        emb.AtSimilarity("cancel_order: Cancels an existing order", Relevant, 0.68f);
        host.Chat!.Say("Qual è il motivo?").Say("ok");

        await host.RunTurnAsync(ask);
        await host.Orchestrator.ResetSessionAsync();
        await host.RunTurnAsync(yes);

        CoordinatorCalls(host)[^1].ToolNames.Should().NotContain("cancel_order");
    }
}
