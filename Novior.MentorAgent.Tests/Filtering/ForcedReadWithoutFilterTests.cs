using FluentAssertions;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Novior.MentorAgent.Tests.Filtering;

/// <summary>
/// <c>RequireLookupForRecordQuestions</c> without semantic tool filtering (BUG-222). The forced read ran inside the
/// tool filter only, and the filter needs an embedding model: without one the option warned that it had no effect,
/// and the demo — no filter, Chat Completions, Standard — answered "Which expenses are still waiting for approval?"
/// in Italian with a list it made up, no read called, about one time in three. The read is now required there too:
/// the first call of a record question must call one of the declared reads the user may run — all of them, as no
/// ranking chose among them — or hand the request to a specialist or a team.
/// </summary>
public sealed class ForcedReadWithoutFilterTests
{
    private const string Question = "Quanti ordini sono aperti?";

    private static readonly string[] Reads = ["count_open_orders", "get_active_orders", "show_order_card"];
    private static readonly string[] WaysOut = ["route_to_specialist", "business_analysis_team"];

    /// <summary>A host with the option on and no tool filtering; its user holds every role of the test application.</summary>
    private static MentorHostFixture Host(
        Action<MentorOptions>? configure = null,
        Action<IServiceCollection>? services = null,
        bool withEmbeddings = false)
    {
        var host = MentorHostFixture.Create(o =>
        {
            o.RequireLookupForRecordQuestions = true;
            o.IsReadOnlyAction = name => Reads.Contains(name);
            o.MemoryAutoCapture = false;
            configure?.Invoke(o);
        }, services, withEmbeddings: withEmbeddings);
        host.Auth.SignIn("ada", "Manager", "Admin");
        return host;
    }

    private static IReadOnlyList<FakeChatClient.RecordedCall> CoordinatorCalls(MentorHostFixture host)
        => host.Chat!.Calls.Where(c => c.ToolNames.Count > 0).ToList();

    private static int ClassifierCalls(MentorHostFixture host) => host.Chat!.Calls.Count(c => c.ToolNames.Count == 0);

    // ── The read is required ────────────────────────────────────────────────────

    [Fact]
    public async Task Without_tool_filtering_a_record_question_must_first_call_a_declared_read()
    {
        await using var host = Host();
        host.Chat!.Say("LOOKUP").CallTool("count_open_orders").Say("Sono 3.");

        await host.RunTurnAsync(Question);

        var first = CoordinatorCalls(host)[0];
        first.ToolMode.Should().Be(ChatToolMode.RequireAny);
        first.ToolNames.Should().Contain(Reads, "no ranking chose among the reads, so every one the user may run is offered");
        first.ToolNames.Should().BeSubsetOf([.. Reads, .. WaysOut],
            "nothing that writes, navigates, loads a skill or remembers can satisfy the requirement — a specialist or a team is the way out");
        first.ToolNames.Should().Contain("route_to_specialist");
    }

    [Fact]
    public async Task With_tool_filtering_on_but_no_embedding_model_the_read_is_required_too()
    {
        // The filter warns and sends every tool: it is not installed, and the read no longer depends on it.
        await using var host = Host(o => o.EnableToolFiltering = true);
        host.Chat!.Say("LOOKUP").CallTool("count_open_orders").Say("Sono 3.");

        await host.RunTurnAsync(Question);

        host.Logs.HasWarning("Tool filtering is enabled but no EmbeddingGenerator is configured").Should().BeTrue();
        var first = CoordinatorCalls(host)[0];
        first.ToolMode.Should().Be(ChatToolMode.RequireAny);
        first.ToolNames.Should().BeSubsetOf([.. Reads, .. WaysOut]);
    }

    [Fact]
    public async Task After_the_read_the_turn_continues_with_every_tool()
    {
        await using var host = Host();
        host.Chat!.Say("LOOKUP").CallTool("count_open_orders").Say("Sono 3.");

        await host.RunTurnAsync(Question);

        var calls = CoordinatorCalls(host);
        calls.Should().HaveCount(2);
        calls[1].ToolMode.Should().NotBe(ChatToolMode.RequireAny, "only the first call is forced");
        calls[1].ToolNames.Should().Contain(["count_open_orders", "navigate_to", "cancel_order", "issue_refund"],
            "without filtering the answer is written with the whole surface, as without the option");
    }

    [Fact]
    public async Task The_classifier_is_asked_once_per_turn()
    {
        await using var host = Host();
        host.Chat!.Say("LOOKUP").CallTool("count_open_orders").CallTool("get_active_orders").Say("Sono 3.")
            .Say("OTHER").Say("Prego.");

        await host.RunTurnAsync(Question);

        ClassifierCalls(host).Should().Be(1, "the follow-ups of the tool loop carry a tool result and are never decided again");
        CoordinatorCalls(host).Skip(1).Should().OnlyContain(c => c.ToolMode != ChatToolMode.RequireAny);

        await host.RunTurnAsync("Grazie");

        ClassifierCalls(host).Should().Be(2, "a new message is a new turn");
        CoordinatorCalls(host)[^1].ToolMode.Should().NotBe(ChatToolMode.RequireAny);
    }

    [Fact]
    public async Task With_semantic_filtering_the_filter_alone_decides_once()
    {
        // The two never run together: the forced read without filtering is not installed when the filter is.
        await using var host = Host(o => o.EnableToolFiltering = true, withEmbeddings: true);
        var emb = host.Embeddings!;
        emb.OnAxis(Question, 0);
        foreach (var t in ForcedReadAndCarryToolTexts) emb.OnAxis(t, 1);
        emb.AtSimilarity("count_open_orders: Counts open orders", 0, 0.68f);
        host.Chat!.Say("LOOKUP").CallTool("count_open_orders").Say("Sono 3.");

        await host.RunTurnAsync(Question);

        ClassifierCalls(host).Should().Be(1, "one decision per turn");
        var first = CoordinatorCalls(host)[0];
        first.ToolMode.Should().Be(ChatToolMode.RequireAny);
        first.ToolNames.Should().Contain("count_open_orders");
        first.ToolNames.Should().NotContain("get_active_orders", "with the filter only this message's selection is offered, as before");
        host.Logs.Records.Should().NotContain(r => r.Message.Contains("among every declared read"));
    }

    /// <summary>The application's actions as the filter matches them, every one put off the question's axis.</summary>
    private static readonly string[] ForcedReadAndCarryToolTexts =
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

    // ── When it is not required ─────────────────────────────────────────────────

    [Fact]
    public async Task A_message_the_classifier_calls_OTHER_is_left_as_it_was()
    {
        await using var host = Host();
        host.Chat!.Say("OTHER").Say("Ciao!");

        await host.RunTurnAsync("Ciao");

        ClassifierCalls(host).Should().Be(1);
        var first = CoordinatorCalls(host)[0];
        first.ToolMode.Should().NotBe(ChatToolMode.RequireAny);
        first.ToolNames.Should().Contain(["navigate_to", "cancel_order", "count_open_orders"], "every tool, as without the option");
    }

    [Theory]
    [InlineData("unreadable", "Request classifier → unreadable")]
    [InlineData("failing", "Request classifier failed")]
    [InlineData("slow", "Request classifier timed out")]
    public async Task A_classifier_that_cannot_tell_leaves_the_turn_as_without_the_option(string how, string logged)
    {
        var classifier = new FakeChatClient();
        switch (how)
        {
            case "unreadable": classifier.Say("Sure, here is the answer"); break;
            case "failing": classifier.ThrowOnNextCall = new InvalidOperationException("the classifier is down"); break;
            default: classifier.BeforeRespond = () => Task.Delay(TimeSpan.FromSeconds(1)); classifier.Say("LOOKUP"); break;
        }
        await using var host = Host(o =>
        {
            o.ClassifierChatClient = classifier;
            o.SafetyCheckTimeout = TimeSpan.FromMilliseconds(100);
        });
        host.Chat!.Say("Ecco.");

        var error = await host.RunTurnAsync(Question);

        error.Should().BeNull("the turn runs, and is answered");
        classifier.Calls.Should().HaveCount(1);
        host.Chat.Calls.Should().ContainSingle();
        host.Chat.Calls[0].ToolMode.Should().NotBe(ChatToolMode.RequireAny, "never forced on an answer nobody gave");
        host.Chat.Calls[0].ToolNames.Should().Contain(["navigate_to", "cancel_order"]);
        host.Logs.Records.Should().Contain(r => r.Message.Contains(logged));
    }

    [Fact]
    public async Task Only_declared_reads_that_need_no_confirmation_and_no_role_are_ever_required()
    {
        // BUG-099: a write answering a read. A read-only claim on an action that asks for confirmation or needs a
        // role is void; and with RequireConfirmationForWrites off an undeclared action asks nobody, but may write.
        await using var host = Host(o =>
        {
            o.RequireConfirmationForWrites = false;
            o.IsReadOnlyAction = name => name is "count_open_orders" or "issue_refund" or "export_ledger";
        });
        host.Chat!.Say("LOOKUP").CallTool("count_open_orders").Say("Sono 3.");

        await host.RunTurnAsync(Question);

        var first = CoordinatorCalls(host)[0];
        first.ToolMode.Should().Be(ChatToolMode.RequireAny);
        first.ToolNames.Should().Contain("count_open_orders");
        first.ToolNames.Should().NotContain(
            ["issue_refund", "export_ledger", "mark_order_reviewed", "cancel_order", "show_order_card", "get_active_orders"],
            "confirmation and roles make a read-only claim void, and an action not declared a read may write");
    }

    [Fact]
    public async Task When_the_user_may_run_no_read_the_classifier_is_not_asked()
    {
        await using var host = Host(
            o => o.IsReadOnlyAction = null,
            s => s.AddMentorActionPolicy(new TestActionPolicy { Allowed = r => r.ToolName != "count_open_orders" }));
        host.Chat!.Say("Non posso.");

        await host.RunTurnAsync(Question);

        host.Chat.Calls.Should().OnlyContain(c => c.ToolNames.Count > 0, "no read on the request, nothing to require");
        host.Chat.Calls[0].ToolNames.Should().NotContain("count_open_orders");
        host.Chat.Calls[0].ToolMode.Should().NotBe(ChatToolMode.RequireAny);
    }

    [Fact]
    public async Task A_picture_sent_without_text_is_not_classified()
    {
        // A receipt sent as a picture carries no text: nothing to ask the classifier, nothing to require.
        await using var host = Host(o => o.EnableImageInput = true);
        host.Chat!.Say("Ho letto la ricevuta.");

        await host.Orchestrator.SendMessageAsync(string.Empty,
            [new Novior.MentorAgent.Abstractions.Models.MentorAttachment
            {
                MimeType = "image/png",
                DataBase64 = Convert.ToBase64String([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
            }]);

        var call = host.Chat.Calls.Should().ContainSingle("the classifier was not asked").Which;
        call.ToolNames.Should().Contain("navigate_to", "the one call is the coordinator's");
        call.ToolMode.Should().NotBe(ChatToolMode.RequireAny);
        call.Messages[^1].Contents.Should().ContainItemsAssignableTo<DataContent>();
    }

    [Fact]
    public async Task Off_by_default_no_classifier_is_asked()
    {
        await using var host = MentorHostFixture.Create(o =>
        {
            o.IsReadOnlyAction = name => Reads.Contains(name);
            o.MemoryAutoCapture = false;
        });
        host.Chat!.Say("ok");

        await host.RunTurnAsync(Question);

        host.Chat.Calls.Should().OnlyContain(c => c.ToolNames.Count > 0, "no tool-less classifier call was made");
        host.Chat.Calls[0].ToolMode.Should().NotBe(ChatToolMode.RequireAny);
    }

    [Fact]
    public async Task After_Stop_at_a_banner_the_next_turn_is_not_forced()
    {
        // BUG-116's recovery turn answers the approval a Stop left open, and declares its action for that answer: a
        // required call could be satisfied by calling it again, and the banner the user stopped would come back.
        await using var host = Host(o => o.HitlMode = MentorHitlMode.Native);
        host.Chat!.Say("OTHER").CallTool("issue_refund", new { orderId = 1001 })
            .CallTool("count_open_orders").Say("Sono 3.");
        var stopped = false;
        host.Get<Novior.MentorAgent.Abstractions.Interfaces.IMentorStateService>().OnConfirmationRequired += _ =>
        {
            if (stopped) return;
            stopped = true;
            host.Orchestrator.CancelCurrentRequest();
        };

        await host.RunTurnAsync("Rimborsa l'ordine 1001");
        var before = CoordinatorCalls(host).Count;
        (await host.RunTurnAsync(Question)).Should().BeNull();

        CoordinatorCalls(host)[before].ToolMode.Should().NotBe(ChatToolMode.RequireAny,
            "the turn that answers what a Stop left open is never forced");
        ClassifierCalls(host).Should().Be(1, "the classifier was asked on the first message, and not on the recovery turn");
    }

    // ── What the forced request keeps ───────────────────────────────────────────

    [Fact]
    public async Task A_forced_read_keeps_the_instructions_adds_no_ui_action_and_its_marker_never_reaches_the_provider()
    {
        await using var host = Host();
        host.Get<Novior.MentorAgent.Abstractions.Interfaces.IMentorPageContext>()
            .RegisterUIAction("highlight_row", "Highlights a row", _ => { });
        host.Chat!.Say("LOOKUP").CallTool("count_open_orders").Say("Sono 3.");

        await host.RunTurnAsync(Question);

        var calls = CoordinatorCalls(host);
        calls[0].ToolMode.Should().Be(ChatToolMode.RequireAny);
        calls[0].Instructions.Should().NotBeNullOrWhiteSpace().And.Be(calls[1].Instructions,
            "the request is cloned: the coordinator's instructions travel unchanged");
        calls[0].ToolNames.Should().NotContain("highlight_row", "a UI action must not be able to satisfy the requirement");
        calls[1].ToolNames.Should().Contain("highlight_row", "after the read the UI actions are back");
        host.Chat.Calls.Should().OnlyContain(c => !c.AdditionalPropertyKeys.Contains(Novior.MentorAgent.Middleware.ToolFilterChatClient.ForcedCallKey),
            "the marker is MentorAgent's own, and some provider clients send every additional property");
    }

    [Fact]
    public async Task Under_service_managed_history_the_forced_request_keeps_the_conversation()
    {
        // A hand-copied ChatOptions drops ConversationId, and the provider cannot match the read's result to its call.
        await using var host = Host(o => o.UseServiceManagedHistory = true);
        host.Chat!.Say("OTHER")
            .Respond((_, _) => new ChatResponse(new ChatMessage(ChatRole.Assistant, "Ciao!")) { ConversationId = "resp_1" })
            .Say("LOOKUP").CallTool("count_open_orders").Say("Sono 3.");

        await host.RunTurnAsync("Ciao");
        await host.RunTurnAsync(Question);

        var forced = CoordinatorCalls(host).Single(c => c.ToolMode == ChatToolMode.RequireAny);
        forced.ConversationId.Should().Be("resp_1");
    }

    [Fact]
    public async Task The_client_forces_the_non_streaming_request_too_and_leaves_the_callers_options_alone()
    {
        var inner = new FakeChatClient().Say("Sono 3.");
        var asked = new List<Novior.MentorAgent.Core.RequestShape>();
        using var client = new Novior.MentorAgent.Middleware.ForcedReadChatClient(
            inner,
            name => name == "count_open_orders",
            (_, shape, _) => { asked.Add(shape); return Task.FromResult<Novior.MentorAgent.Core.RequestKind?>(Novior.MentorAgent.Core.RequestKind.Lookup); },
            new HashSet<string>(["route_to_specialist"], StringComparer.OrdinalIgnoreCase),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        var options = new ChatOptions
        {
            Instructions = "the coordinator's instructions",
            ConversationId = "resp_1",
            Tools = [Tool("count_open_orders"), Tool("cancel_order"), Tool("route_to_specialist"), Tool("navigate_to")],
        };

        client.BeginUserTurn();
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, Question)], options);

        var shape = asked.Should().ContainSingle().Which;
        shape.Lookup.Should().BeTrue();
        shape.Actions.Should().BeEmpty("asked about reads only");
        shape.Navigation.Should().BeFalse();
        var sent = inner.Calls.Single();
        sent.ToolMode.Should().Be(ChatToolMode.RequireAny);
        sent.ToolNames.Should().Equal("count_open_orders", "route_to_specialist");
        sent.Instructions.Should().Be("the coordinator's instructions");
        sent.ConversationId.Should().Be("resp_1");
        sent.AdditionalPropertyKeys.Should().Contain(Novior.MentorAgent.Middleware.ToolFilterChatClient.ForcedCallKey,
            "for UIActionsMiddleware, which takes it off");
        options.Tools.Should().HaveCount(4, "the caller's options are cloned, never changed");
        options.ToolMode.Should().BeNull();
    }

    private static AIFunction Tool(string name) => AIFunctionFactory.Create(() => "ok", name);

    // ── At startup ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Without_tool_filtering_the_option_says_what_it_requires_and_no_longer_warns()
    {
        await using var host = Host();
        host.Chat!.Say("OTHER").Say("Ciao!");

        await host.RunTurnAsync("Ciao");

        host.Logs.HasWarning("RequireLookupForRecordQuestions is on but").Should().BeFalse("it has an effect now");
        host.Logs.Records.Should().Contain(r => r.Level == Microsoft.Extensions.Logging.LogLevel.Information
                                                && r.Message.Contains("among every declared read"));
    }

    [Fact]
    public async Task With_change_requests_required_too_only_the_read_is_asked_about_without_filtering()
    {
        await using var host = Host(o => o.RequireActionForChangeRequests = true);
        host.Chat!.Say("OTHER").Say("ok");

        await host.RunTurnAsync("Annulla l'ordine 1004 perché è un doppione");

        host.Logs.HasWarning("RequireActionForChangeRequests is on but options.EnableToolFiltering is off").Should().BeTrue(
            "the change requests still need the filter");
        var classifier = host.Chat.Calls.Single(c => c.ToolNames.Count == 0);
        classifier.SystemText.Should().EndWith("Answer with exactly one word: LOOKUP or OTHER.");
        classifier.Temperature.Should().Be(0f);
    }
}
