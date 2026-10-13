using FluentAssertions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.App;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.AI;
using Xunit;

namespace Novior.MentorAgent.Tests.Filtering;

/// <summary>
/// BUG-118 — <c>RequireActionForChangeRequests</c>, opt-in. The assistant answered "Stock aggiornato" with no
/// tool called, about one time in five on the ApiServer sample, and "Ecco la pagina Prodotti" without navigating.
/// When a classifier says the message asks for one of the confirmation-gated actions it selected and gives every
/// input that action needs, the first call must be one of them — the banner comes before any sentence; when it
/// asks for a page, the first call must be <c>navigate_to</c>.
///
/// <para>
/// What it must never do is force a call the request does not support: an incomplete request, an action that
/// would run without asking anybody, an action the message only carried from the previous turn, a page shown
/// to another agent. Each has a test here, beside the forcing itself.
/// </para>
/// </summary>
public sealed class ForcedActionTests
{
    private const int Relevant = 0;
    private const int Unrelated = 1;

    private const string Refund = "Rimborsa l'ordine 1001";
    private const string Page = "Portami alla pagina dei report";

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

    private static MentorHostFixture Host(Action<MentorOptions>? configure = null)
        => MentorHostFixture.Create(o =>
        {
            o.EnableToolFiltering = true;
            o.ToolFilterMinScore = 0.35f;
            o.MemoryAutoCapture = false;
            o.RequireActionForChangeRequests = true;
            configure?.Invoke(o);
        }, withEmbeddings: true);

    /// <summary>Only <paramref name="tool"/> clears 0.35 for the queries; every other action is unrelated.</summary>
    private static void SeedMatch(MentorHostFixture host, string tool, params string[] queries)
    {
        var emb = host.Embeddings!;
        foreach (var q in queries) emb.OnAxis(q, Relevant);
        foreach (var t in ToolTexts) emb.OnAxis(t, Unrelated);
        if (tool.Length > 0) emb.AtSimilarity(tool, Relevant, 0.70f);
    }

    private static IReadOnlyList<FakeChatClient.RecordedCall> CoordinatorCalls(MentorHostFixture host)
        => host.Chat!.Calls.Where(c => c.ToolNames.Count > 0).ToList();

    private static IReadOnlyList<FakeChatClient.RecordedCall> ClassifierCalls(MentorHostFixture host)
        => host.Chat!.Calls
            .Where(c => c.ToolNames.Count == 0 && c.SystemText.StartsWith("You classify one message"))
            .ToList();

    /// <summary>Answers every banner as it is raised, and counts them.</summary>
    private static Func<int> AnswerBanners(MentorHostFixture host, bool approve)
    {
        var raised = 0;
        var state = host.Get<IMentorStateService>();
        state.OnConfirmationRequired += req =>
        {
            Interlocked.Increment(ref raised);
            if (approve) _ = state.ConfirmAsync(req.ActionId);
            else state.Cancel(req.ActionId);
        };
        return () => Volatile.Read(ref raised);
    }

    // ── The action ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_complete_request_for_a_confirmation_gated_action_must_first_call_it()
    {
        await using var host = Host();
        SeedMatch(host, "issue_refund: Issues a refund", Refund);
        host.Chat!.Say("ACTION").CallTool("issue_refund", new { orderId = 1001 }).Say("Il rimborso non è stato emesso.");
        var banners = AnswerBanners(host, approve: false);

        await host.RunTurnAsync(Refund);

        var first = CoordinatorCalls(host)[0];
        first.ToolMode.Should().Be(ChatToolMode.RequireAny);
        first.ToolNames.Should().Contain("issue_refund");
        first.ToolNames.Should().BeSubsetOf(["issue_refund", "route_to_specialist", "business_analysis_team"],
            "nothing that reads, navigates or only talks can satisfy the requirement — a specialist or a team is the way out");
        banners().Should().Be(1, "the banner is what the user sees first, before any sentence");
        host.Get<TestOrderService>().Invoked.Should().NotContain(nameof(TestOrderService.IssueRefundAsync), "it was declined");
    }

    [Fact]
    public async Task The_classifier_sees_what_the_action_needs_and_is_asked_deterministically()
    {
        await using var host = Host();
        SeedMatch(host, "issue_refund: Issues a refund", Refund);
        host.Chat!.Say("OTHER").Say("Quale ordine?");

        await host.RunTurnAsync(Refund);

        var classifier = ClassifierCalls(host).Should().ContainSingle().Subject;
        classifier.SystemText.Should().Contain("- issue_refund: Issues a refund. Inputs: orderId (integer).");
        classifier.SystemText.Should().EndWith("Answer with exactly one word: ACTION, NAVIGATE or OTHER.");
        classifier.LastUserText.Should().Be(Refund);
        classifier.Temperature.Should().Be(0f, "the wording was measured at temperature 0");
        classifier.MaxOutputTokens.Should().Be(16);
    }

    [Fact]
    public async Task A_request_the_classifier_calls_incomplete_is_not_forced()
    {
        // Whether a request gives every input is the classifier's judgement — measured on the model, its rule
        // pinned in RequestClassifierTests. Here: when it says OTHER, the turn runs as it would without the
        // option, the action still declared and the model free to ask for what is missing.
        const string incomplete = "Rimborsa un ordine";
        await using var host = Host();
        SeedMatch(host, "issue_refund: Issues a refund", incomplete);
        host.Chat!.Say("OTHER").Say("Quale ordine vuoi rimborsare?");

        await host.RunTurnAsync(incomplete);

        var first = CoordinatorCalls(host)[0];
        first.ToolMode.Should().NotBe(ChatToolMode.RequireAny);
        first.ToolNames.Should().Contain(["issue_refund", "navigate_to"], "the turn runs as it would without the option");
    }

    [Fact]
    public async Task After_the_banner_the_turn_continues_unforced_with_its_whole_selection()
    {
        await using var host = Host();
        SeedMatch(host, "issue_refund: Issues a refund", Refund);
        host.Chat!.Say("ACTION").CallTool("issue_refund", new { orderId = 1001 }).Say("Rimborso emesso.");
        AnswerBanners(host, approve: true);

        await host.RunTurnAsync(Refund);

        host.Get<TestOrderService>().Invoked.Should().Contain(nameof(TestOrderService.IssueRefundAsync));
        var calls = CoordinatorCalls(host);
        calls.Should().HaveCountGreaterThan(1);
        calls[1].ToolMode.Should().NotBe(ChatToolMode.RequireAny, "only the first call is forced");
        calls[1].ToolNames.Should().Contain(["issue_refund", "navigate_to"],
            "the action that ran stays declared (call_id), and the core tools are back for the answer");
    }

    [Fact]
    public async Task With_native_approval_the_approval_round_is_not_forced_again()
    {
        // The answer to the banner travels on a run of its own. It continues the turn: no second classifier
        // call, no second forced request — a forced call there would raise the banner again.
        await using var host = Host(o => o.HitlMode = MentorHitlMode.Native);
        SeedMatch(host, "issue_refund: Issues a refund", Refund);
        host.Chat!.Say("ACTION").CallTool("issue_refund", new { orderId = 1001 }).Say("Il rimborso non è stato emesso.");
        var banners = AnswerBanners(host, approve: false);

        await host.RunTurnAsync(Refund);

        banners().Should().Be(1);
        ClassifierCalls(host).Should().ContainSingle();
        var calls = CoordinatorCalls(host);
        calls[0].ToolMode.Should().Be(ChatToolMode.RequireAny);
        calls.Skip(1).Should().NotBeEmpty().And.OnlyContain(c => c.ToolMode != ChatToolMode.RequireAny);
    }

    [Fact]
    public async Task An_action_that_would_run_unasked_is_never_required()
    {
        // With RequireConfirmation off no banner would come: a forced call on a misread request would just run.
        await using var host = Host(o => o.RequireConfirmation = false);
        SeedMatch(host, "issue_refund: Issues a refund", Refund);
        host.Chat!.Say("ACTION").Say("ok");

        await host.RunTurnAsync(Refund);

        host.Logs.HasWarning("RequireActionForChangeRequests is on, but options.RequireConfirmation is off").Should().BeTrue();
        host.Logs.HasWarning("only page requests are covered").Should().BeTrue();
        ClassifierCalls(host).Single().SystemText.Should().NotContain("Answer ACTION", "no action is on offer");
        CoordinatorCalls(host)[0].ToolMode.Should().NotBe(ChatToolMode.RequireAny);
    }

    [Fact]
    public async Task An_action_without_confirmation_is_never_required()
    {
        // mark_order_reviewed changes data but asks nobody: it is not on offer, whatever the classifier says.
        // It declares nothing, so since rc.15 it asks by default; the host here turned that default off.
        const string review = "Segna come revisionato l'ordine 1001";
        await using var host = Host(o => o.RequireConfirmationForWrites = false);
        SeedMatch(host, "mark_order_reviewed: Marks an order as reviewed", review);
        host.Chat!.Say("ACTION").CallTool("mark_order_reviewed", new { orderId = 1001 }).Say("Fatto.");

        await host.RunTurnAsync(review);

        ClassifierCalls(host).Single().SystemText.Should().NotContain("mark_order_reviewed");
        CoordinatorCalls(host)[0].ToolMode.Should().NotBe(ChatToolMode.RequireAny);
    }

    [Fact]
    public async Task An_action_the_message_only_carried_is_never_required()
    {
        // "Sì, procedi" carries the previous message's selection (OSS-01), but asked for nothing itself: the
        // action travels, and is not on offer to the classifier.
        const string yes = "Sì, procedi";
        await using var host = Host();
        SeedMatch(host, "issue_refund: Issues a refund", Refund);
        host.Embeddings!.OnAxis(yes, Unrelated + 2);
        host.Chat!.Say("OTHER").Say("Confermi il rimborso dell'ordine 1001?")
            .Say("ACTION").Say("ok");

        await host.RunTurnAsync(Refund);
        await host.RunTurnAsync(yes);

        ClassifierCalls(host)[^1].SystemText.Should().NotContain("issue_refund");
        var last = CoordinatorCalls(host)[^1];
        last.ToolMode.Should().NotBe(ChatToolMode.RequireAny);
        last.ToolNames.Should().Contain("issue_refund", "it still travels, as carried");
    }

    [Fact]
    public async Task One_classifier_call_serves_both_options_and_ACTION_forces_the_action_not_the_reads()
    {
        await using var host = Host(o =>
        {
            o.RequireLookupForRecordQuestions = true;
            o.IsReadOnlyAction = name => name is "count_open_orders" or "get_active_orders" or "show_order_card";
        });
        SeedMatch(host, "issue_refund: Issues a refund", Refund);
        host.Embeddings!.AtSimilarity("count_open_orders: Counts open orders", Relevant, 0.30f);
        host.Chat!.Say("ACTION").CallTool("issue_refund", new { orderId = 1001 }).Say("ok");
        AnswerBanners(host, approve: false);

        await host.RunTurnAsync(Refund);

        ClassifierCalls(host).Should().ContainSingle().Which.SystemText
            .Should().EndWith("Answer with exactly one word: LOOKUP, ACTION, NAVIGATE or OTHER.");
        var first = CoordinatorCalls(host)[0];
        first.ToolMode.Should().Be(ChatToolMode.RequireAny);
        first.ToolNames.Should().Contain("issue_refund").And.NotContain("count_open_orders");
    }

    [Fact]
    public async Task With_native_auto_approval_a_required_action_is_still_shown_to_a_person()
    {
        // The review of the first candidate: with HitlMode.Native a rule approves a call after it is made, with
        // nobody asked — a forced refund ran with no banner. No rule is consulted for a required call now; a call
        // the model makes on its own is the rule's to decide, as the host configured it.
        await using var host = Host(o =>
        {
            o.HitlMode = MentorHitlMode.Native;
            o.AutoApprovalRules = [ctx => ValueTask.FromResult(ctx.FunctionCallContent.Name == "issue_refund")];
        });
        SeedMatch(host, "issue_refund: Issues a refund", Refund);
        host.Chat!.Say("ACTION").CallTool("issue_refund", new { orderId = 1001 }).Say("Il rimborso non è stato emesso.")
            .Say("OTHER").CallTool("issue_refund", new { orderId = 1001 }).Say("Rimborso emesso.");
        var banners = AnswerBanners(host, approve: false);

        await host.RunTurnAsync(Refund);

        CoordinatorCalls(host)[0].ToolMode.Should().Be(ChatToolMode.RequireAny);
        banners().Should().Be(1, "the rule would have approved it; a required call is always shown");
        host.Get<TestOrderService>().Invoked.Should().NotContain(nameof(TestOrderService.IssueRefundAsync), "it was declined");

        await host.RunTurnAsync(Refund);

        banners().Should().Be(1, "not required this time: the host's rule approved it, as configured");
        host.Get<TestOrderService>().Invoked.Should().Contain(nameof(TestOrderService.IssueRefundAsync));
    }

    [Fact]
    public async Task A_required_action_the_user_may_not_run_is_refused_before_any_banner()
    {
        // cancel_order needs the Manager role, which the anonymous user lacks. Forcing the call grants nothing:
        // the role is checked before anyone is asked.
        const string cancel = "Annulla l'ordine 1001: è un duplicato";
        await using var host = Host();
        SeedMatch(host, "cancel_order: Cancels an existing order", cancel);
        host.Chat!.Say("ACTION").CallTool("cancel_order", new { orderId = 1001, reason = "duplicato" })
            .Say("Non hai i permessi per annullarlo.");
        var banners = AnswerBanners(host, approve: true);

        await host.RunTurnAsync(cancel);

        ClassifierCalls(host).Single().SystemText.Should().Contain(
            "- cancel_order: Cancels an existing order. Inputs: orderId (integer), reason (string).");
        CoordinatorCalls(host)[0].ToolMode.Should().Be(ChatToolMode.RequireAny);
        banners().Should().Be(0);
        host.Get<TestOrderService>().Invoked.Should().NotContain(i => i.StartsWith(nameof(TestOrderService.CancelOrderAsync)));
    }

    // ── The page ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_page_request_must_first_navigate_and_no_ui_action_can_stand_in()
    {
        await using var host = Host();
        host.Get<IMentorPageContext>().RegisterUIAction("highlight_row", "Highlights a row", _ => { });
        SeedMatch(host, "", Page);
        host.Chat!.Say("NAVIGATE").CallTool("navigate_to", new { url = "/reports" }).Say("Eccoti nei report.");

        await host.RunTurnAsync(Page);

        var calls = CoordinatorCalls(host);
        calls[0].ToolMode.Should().Be(ChatToolMode.RequireAny);
        calls[0].ToolNames.Should().Equal("navigate_to");
        calls[1].ToolMode.Should().NotBe(ChatToolMode.RequireAny);
        calls[^1].ToolNames.Should().Contain("highlight_row", "after the navigation the UI actions are back");
        host.Chat.Calls.Should().OnlyContain(c => !c.AdditionalPropertyKeys.Contains(Novior.MentorAgent.Middleware.ToolFilterChatClient.ForcedCallKey),
            "the marker is MentorAgent's own, and some provider clients send every additional property");
    }

    [Fact]
    public async Task A_page_request_from_another_agent_is_never_forced()
    {
        // Another agent has no page in front of it. With no action selected, navigation was all there was to
        // ask about — so not even the classifier is paid for.
        await using var host = Host();
        host.Get<MentorTurnOrigin>().FromAnotherAgent = true;
        SeedMatch(host, "", Page);
        host.Chat!.Say("Le pagine si aprono dall'applicazione.");

        await host.RunTurnAsync(Page);

        ClassifierCalls(host).Should().BeEmpty();
        CoordinatorCalls(host)[0].ToolMode.Should().NotBe(ChatToolMode.RequireAny);
    }

    [Fact]
    public async Task A_label_the_classifier_was_not_offered_requires_nothing()
    {
        // Another agent's request is asked without NAVIGATE. A NAVIGATE answer anyway answers a question nobody
        // asked: no page is required (the review of the first candidate found one forced).
        await using var host = Host();
        host.Get<MentorTurnOrigin>().FromAnotherAgent = true;
        SeedMatch(host, "issue_refund: Issues a refund", Refund);
        host.Chat!.Say("NAVIGATE").Say("Va fatto dall'applicazione.");

        await host.RunTurnAsync(Refund);

        ClassifierCalls(host).Single().SystemText.Should().NotContain("NAVIGATE");
        CoordinatorCalls(host)[0].ToolMode.Should().NotBe(ChatToolMode.RequireAny);
    }

    [Fact]
    public async Task An_unreadable_classifier_answer_leaves_the_turn_as_without_the_option()
    {
        await using var host = Host();
        SeedMatch(host, "", Page);
        host.Chat!.Say("Sure, I will navigate").Say("ok");

        await host.RunTurnAsync(Page);

        CoordinatorCalls(host)[0].ToolMode.Should().NotBe(ChatToolMode.RequireAny);
        host.Logs.Records.Should().Contain(r => r.Message.Contains("Request classifier → unreadable"));
    }

    // ── Configuration ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Off_by_default_no_classifier_is_asked()
    {
        await using var host = Host(o => o.RequireActionForChangeRequests = false);
        SeedMatch(host, "issue_refund: Issues a refund", Refund);
        host.Chat!.Say("ok");

        await host.RunTurnAsync(Refund);

        ClassifierCalls(host).Should().BeEmpty();
        CoordinatorCalls(host)[0].ToolMode.Should().NotBe(ChatToolMode.RequireAny);
    }

    [Fact]
    public async Task Without_tool_filtering_the_option_warns_that_it_has_no_effect()
    {
        await using var host = MentorHostFixture.Create(o =>
        {
            o.RequireActionForChangeRequests = true;
            o.MemoryAutoCapture = false;
        });
        host.Chat!.Say("ok");

        await host.RunTurnAsync(Refund);

        host.Logs.HasWarning("RequireActionForChangeRequests is on but options.EnableToolFiltering is off").Should().BeTrue();
    }

}
