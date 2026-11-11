using FluentAssertions;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Evaluation;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.App;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Xunit;

namespace Novior.MentorAgent.Tests.Evaluation;

/// <summary>
/// Story 171 — <see cref="MentorEvaluator.RunConversationsAsync"/>: whole conversations through the real orchestrator,
/// with tools, history and streaming, evaluated turn by turn.
///
/// <para>
/// What a CI gate needs from it is that a turn which did the wrong thing fails, and says what it did: the tools are
/// read from the gate, the tokens from the session's own counter, the first token from the stream. Each of those is
/// pinned here from the failing side as well, because a harness that passes when it should fail turns an unreviewed
/// prompt change into a green build.
/// </para>
/// </summary>
public sealed class ConversationEvalTests
{
    private const string JudgePromptMarker = "Rate from 1 to 5";

    private static async Task<MentorEvalReport> RunAsync(
        MentorHostFixture host, MentorEvalOptions? options, params EvalConversation[] conversations)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        return await host.Get<MentorEvaluator>().RunConversationsAsync(conversations, options, deadline.Token);
    }

    private static EvalConversation Conversation(string name, params EvalTurn[] turns) => new(name, turns);

    private static string LastUserText(IReadOnlyList<ChatMessage> messages)
        => messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";

    private static ChatResponse Reply(string text, long input, long output)
        => new(new ChatMessage(ChatRole.Assistant, text))
        {
            Usage = new UsageDetails { InputTokenCount = input, OutputTokenCount = output },
        };

    // ── The criterion: "at turn 2 it must call X" ────────────────────────────────

    [Fact]
    public async Task At_turn_two_the_tool_it_must_call_is_called_and_the_report_says_so()
    {
        await using var host = MentorHostFixture.Create();
        host.Chat!.Say("Hello, how can I help?")
            .CallTool("count_open_orders").Say("There are 2 open orders.");

        var report = await RunAsync(host, null, Conversation("orders",
            new EvalTurn("Hi"),
            new EvalTurn("How many open orders are there?") { ExpectTools = ["count_open_orders"] }));

        report.Passed.Should().BeTrue(report.FailureReason);
        report.Results.Should().HaveCount(2);

        var first = report.Results[0];
        first.Conversation.Should().Be("orders");
        first.Turn.Should().Be(1);
        first.ToolsCalled.Should().BeEmpty();
        first.Output.Should().Be("Hello, how can I help?");

        var second = report.Results[1];
        second.Turn.Should().Be(2);
        second.ToolsCalled.Should().Equal("count_open_orders");
        second.Output.Should().Be("There are 2 open orders.");
        second.TimeToFirstToken.Should().NotBeNull();
        second.TurnDuration.Should().NotBeNull();
        second.TurnDuration!.Value.Should().BeGreaterThanOrEqualTo(second.TimeToFirstToken!.Value);

        // The tokens of the turn: one call for the first, the tool call and the answer for the second.
        (first.InputTokens, first.OutputTokens).Should().Be((10L, 5L));
        (second.InputTokens, second.OutputTokens).Should().Be((20L, 10L));
    }

    [Fact]
    public async Task A_turn_that_does_not_call_the_expected_tool_fails_and_says_what_it_called()
    {
        await using var host = MentorHostFixture.Create();
        host.Chat!.Say("Hello.").Say("I could not say.");

        var report = await RunAsync(host, null, Conversation("orders",
            new EvalTurn("Hi"),
            new EvalTurn("How many open orders are there?") { ExpectTools = ["count_open_orders"] }));

        report.Passed.Should().BeFalse();
        report.Results[0].Passed.Should().BeTrue("the first turn had nothing to call");
        report.Results[1].Passed.Should().BeFalse();
        report.Results[1].Notes.Should().Be("expected count_open_orders, called none");
        report.FailureReason.Should().Be(
            "1 of 2 turn(s) failed — first: 'orders' turn 2: expected count_open_orders, called none");
        report.Invoking(r => r.ThrowIfFailed()).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task A_forbidden_tool_that_was_called_fails_the_turn()
    {
        await using var host = MentorHostFixture.Create();
        host.Chat!.CallTool("count_open_orders").Say("There are 2 open orders.");

        var report = await RunAsync(host, null, Conversation("orders",
            new EvalTurn("Tell me about orders") { ForbidTools = ["count_open_orders", "issue_refund"] }));

        report.Results.Single().Passed.Should().BeFalse();
        report.Results.Single().Notes.Should().Be("called forbidden count_open_orders");
    }

    [Fact]
    public async Task A_call_the_gate_refuses_is_not_counted_as_called_and_the_notes_say_so()
    {
        // A policy stops count_open_orders when it is called: the model asked for it, the gate refused it. It did not
        // run, so it is not in ToolsCalled and forbidding it does not fail the turn; the notes say what happened.
        await using var host = MentorHostFixture.Create(configureServices: s => s.AddMentorActionPolicy(new TestActionPolicy
        {
            Verdict = r => r.ToolName == "count_open_orders" ? MentorActionVerdict.Stop("orders are closed") : MentorActionVerdict.Proceed,
        }));
        host.Chat!.CallTool("count_open_orders").Say("I cannot count them now.");

        var report = await RunAsync(host, null, Conversation("orders",
            new EvalTurn("How many open orders are there?") { ForbidTools = ["count_open_orders"] }));

        var turn = report.Results.Single();
        turn.Passed.Should().BeTrue(turn.Notes);
        turn.ToolsCalled.Should().BeEmpty();
        turn.Notes.Should().Be($"not run: count_open_orders ({MentorActionReasons.Policy})");
    }

    // ── One session per conversation, with its history ───────────────────────────

    [Fact]
    public async Task Each_turn_is_sent_with_the_history_of_the_turns_before_it()
    {
        await using var host = MentorHostFixture.Create();
        host.Chat!.Say("Your order is 1001.").Say("It ships tomorrow.");

        await RunAsync(host, null, Conversation("history",
            new EvalTurn("Which is my order?"), new EvalTurn("When does it ship?")));

        var second = host.Chat.Calls.Last();
        second.LastUserText.Should().Contain("When does it ship?");
        second.Messages.Select(m => m.Text).Should().Contain(t => t.Contains("Which is my order?"))
            .And.Contain(t => t.Contains("Your order is 1001."));
    }

    [Fact]
    public async Task Each_conversation_has_a_session_of_its_own()
    {
        await using var host = MentorHostFixture.Create();
        host.Chat!.Say("First answer.").Say("Second answer.");

        var report = await RunAsync(host, null,
            Conversation("one", new EvalTurn("The first conversation's question")),
            Conversation("two", new EvalTurn("The second conversation's question")));

        report.Passed.Should().BeTrue(report.FailureReason);
        var second = host.Chat.Calls.Last();
        second.Messages.Select(m => m.Text).Should().NotContain(t => t.Contains("first conversation"))
            .And.NotContain(t => t.Contains("First answer."));

        // Nor is the application's own session touched.
        (await host.StoredHistoryAsync()).Should().BeEmpty();
    }

    // ── The time to the first token ──────────────────────────────────────────────

    [Fact]
    public async Task The_time_to_the_first_token_is_measured_and_a_slow_turn_fails_its_limit()
    {
        await using var host = MentorHostFixture.Create();
        host.Chat!.Say("Quick answer.");

        var fast = await RunAsync(host, null, Conversation("fast",
            new EvalTurn("Hi") { MaxTimeToFirstToken = TimeSpan.FromSeconds(10) }));
        fast.Passed.Should().BeTrue(fast.FailureReason);

        // 400 ms before the model answers, then three words 200 ms apart: the first token is the first word's.
        host.Chat.Say("Slow but steady.");
        host.Chat.BeforeRespond = () => Task.Delay(400);
        host.Chat.ChunkDelay = TimeSpan.FromMilliseconds(200);
        var slow = await RunAsync(host, null, Conversation("slow",
            new EvalTurn("Hi") { MaxTimeToFirstToken = TimeSpan.FromMilliseconds(100) }));

        var turn = slow.Results.Single();
        turn.Output.Should().Be("Slow but steady.");
        turn.TimeToFirstToken.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(580));
        (turn.TurnDuration!.Value - turn.TimeToFirstToken!.Value).Should()
            .BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(350), "two more words came after the first");
        turn.Passed.Should().BeFalse();
        turn.Notes.Should().StartWith("first token after ").And.EndWith(" ms > 100 ms");
    }

    // ── Errors and cancellation ──────────────────────────────────────────────────

    [Fact]
    public async Task A_turn_that_shows_an_error_fails_and_the_conversation_goes_on()
    {
        await using var host = MentorHostFixture.Create();
        host.Chat!.ThrowOnNextCall = new InvalidOperationException("the provider is down");
        host.Chat.Say("Back again.");

        var report = await RunAsync(host, null, Conversation("errors", new EvalTurn("First"), new EvalTurn("Second")));

        report.Results[0].Passed.Should().BeFalse();
        report.Results[0].Notes.Should().StartWith("error: ");
        report.Results[1].Passed.Should().BeTrue(report.Results[1].Notes);
        report.Results[1].Output.Should().Be("Back again.");
    }

    [Fact]
    public async Task A_turn_that_streams_no_text_fails()
    {
        await using var host = MentorHostFixture.Create();
        host.Chat!.Say("");

        var report = await RunAsync(host, null, Conversation("silent", new EvalTurn("Hi")));

        report.Results.Single().Passed.Should().BeFalse();
        report.Results.Single().Notes.Should().Be("empty response");
    }

    [Fact]
    public async Task A_cancelled_run_throws_and_runs_no_further_turn()
    {
        await using var host = MentorHostFixture.Create();
        host.Chat!.Say("Never seen.");
        using var cts = new CancellationTokenSource();
        host.Chat.BeforeRespond = () => { cts.Cancel(); return Task.CompletedTask; };

        await host.Get<MentorEvaluator>()
            .Invoking(e => e.RunConversationsAsync([Conversation("c", new EvalTurn("First"), new EvalTurn("Second"))],
                ct: cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();

        host.Chat.Calls.Should().ContainSingle("the second turn never ran");
    }

    // ── Tokens: the turn's own ───────────────────────────────────────────────────

    [Fact]
    public async Task A_turns_tokens_are_its_own_while_another_session_talks_on_the_same_model()
    {
        await using var host = MentorHostFixture.Create();
        host.Chat!.Respond((messages, _) => LastUserText(messages).Contains("other session")
            ? Reply("Other answer.", 7, 3)
            : Reply("Evaluated answer.", 100, 20));

        // While the evaluated turn waits for the model, another session — the application's own — has a whole turn.
        var interleaved = false;
        host.Chat.BeforeRespond = async () =>
        {
            if (interleaved) return;
            interleaved = true;
            (await host.RunTurnAsync("a question from the other session")).Should().BeNull();
        };

        var process = host.Get<MentorTokenUsage>();
        var before = process.TotalTokens;

        var report = await RunAsync(host, null, Conversation("tokens", new EvalTurn("The evaluated question")));

        interleaved.Should().BeTrue();
        var turn = report.Results.Single();
        (turn.InputTokens, turn.OutputTokens).Should().Be((100L, 20L), "the other session's 7 + 3 are not this turn's");
        (process.TotalTokens - before).Should().Be(130, "the process counted both sessions");
    }

    [Fact]
    public async Task A_turn_over_its_token_budget_fails_and_the_run_budget_counts_every_turn()
    {
        await using var host = MentorHostFixture.Create();
        host.Chat!.Say("One.").Say("Two.");

        var report = await RunAsync(host, new MentorEvalOptions { MaxTotalTokens = 29 }, Conversation("budget",
            new EvalTurn("First") { MaxTokens = 15 },
            new EvalTurn("Second") { MaxTokens = 14 }));

        report.Results[0].Passed.Should().BeTrue("15 tokens reach the budget of 15 without exceeding it");
        report.Results[1].Passed.Should().BeFalse();
        report.Results[1].Notes.Should().Be("tokens 15 > 14");
        report.TotalTokens.Should().Be(30);
        report.FailureReason.Should().Be("total tokens 30 exceed budget 29");
    }

    // ── Confirmations: no by default ─────────────────────────────────────────────

    [Theory]
    [InlineData(MentorHitlMode.Blocking)]
    [InlineData(MentorHitlMode.Native)]
    public async Task By_default_a_confirmation_is_answered_no_and_the_action_does_not_run(MentorHitlMode mode)
    {
        await using var host = MentorHostFixture.Create(o => o.HitlMode = mode);
        host.Chat!.CallTool("issue_refund", new { orderId = 1001 }).Say("The refund was not issued.");
        TestOrderService? orders = null;

        var report = await RunAsync(host,
            new MentorEvalOptions { PrepareSession = (sp, _) => { orders = sp.GetRequiredService<TestOrderService>(); return Task.CompletedTask; } },
            Conversation("refund", new EvalTurn("Refund order 1001") { ExpectTools = ["issue_refund"] }));

        var turn = report.Results.Single();
        turn.Passed.Should().BeTrue(turn.Notes);
        turn.ToolsCalled.Should().Equal("issue_refund");
        turn.Notes.Should().Be("confirmation asked: issue_refund — denied");
        orders!.Invoked.Should().NotContain(nameof(TestOrderService.IssueRefundAsync));
    }

    [Theory]
    [InlineData(MentorHitlMode.Blocking)]
    [InlineData(MentorHitlMode.Native)]
    public async Task With_ApproveConfirmations_the_confirmed_action_runs(MentorHitlMode mode)
    {
        await using var host = MentorHostFixture.Create(o => o.HitlMode = mode);
        host.Chat!.CallTool("issue_refund", new { orderId = 1001 }).Say("The refund was issued.");
        TestOrderService? orders = null;

        var report = await RunAsync(host,
            new MentorEvalOptions
            {
                ApproveConfirmations = true,
                PrepareSession = (sp, _) => { orders = sp.GetRequiredService<TestOrderService>(); return Task.CompletedTask; },
            },
            Conversation("refund", new EvalTurn("Refund order 1001") { ExpectTools = ["issue_refund"] }));

        var turn = report.Results.Single();
        turn.Passed.Should().BeTrue(turn.Notes);
        turn.Notes.Should().Be("confirmation asked: issue_refund — approved");
        orders!.Invoked.Should().Contain(nameof(TestOrderService.IssueRefundAsync));
    }

    // ── The user: PrepareSession ─────────────────────────────────────────────────

    [Fact]
    public async Task PrepareSession_sets_the_user_of_each_conversation_and_without_it_nobody_is_signed_in()
    {
        // A scoped provider, as a real host has: what PrepareSession sets is that conversation's user only.
        await using var host = MentorHostFixture.Create(configureServices: s =>
            s.AddScoped<AuthenticationStateProvider>(_ => new TestAuthStateProvider()));
        host.Chat!.Say("Done.");

        await RunAsync(host, null, Conversation("anonymous", new EvalTurn("Export the ledger")));
        host.Chat.Calls.Last().ToolNames.Should().NotContain("export_ledger", "export_ledger needs the Admin role");

        await RunAsync(host,
            new MentorEvalOptions
            {
                PrepareSession = (sp, _) =>
                {
                    ((TestAuthStateProvider)sp.GetRequiredService<AuthenticationStateProvider>()).SignIn("ada", "Admin");
                    return Task.CompletedTask;
                },
            },
            Conversation("admin", new EvalTurn("Export the ledger")));
        host.Chat.Calls.Last().ToolNames.Should().Contain("export_ledger");
    }

    // ── The judge, turn by turn ──────────────────────────────────────────────────

    [Fact]
    public async Task The_judge_scores_each_turn_with_an_expectation_and_a_low_score_fails_that_turn()
    {
        await using var host = MentorHostFixture.Create();
        host.Chat!.Respond((messages, _) =>
        {
            var text = string.Join("\n", messages.Select(m => m.Text));
            if (!text.Contains(JudgePromptMarker, StringComparison.Ordinal))
                return Reply(LastUserText(messages).Contains("good") ? "A good answer." : "A poor answer.", 10, 5);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant,
                text.Contains("ASSISTANT OUTPUT: A good answer.", StringComparison.Ordinal) ? "5" : "2"));
        });

        var report = await RunAsync(host, new MentorEvalOptions { Judge = true, MinQuality = 0.5 },
            Conversation("judged",
                new EvalTurn("Give a good answer") { Expectation = "a helpful answer" },
                new EvalTurn("Something else"),
                new EvalTurn("Now a weak one") { Expectation = "a helpful answer" }));

        report.Results.Select(r => r.QualityScore).Should().Equal(1.0, null, 0.4);
        report.Results.Select(r => r.Passed).Should().Equal(true, true, false);
        report.Results[2].Notes.Should().Be("quality 0.40 < 0.50");
        report.AverageQuality.Should().BeApproximately(0.7, 1e-9);
        report.Passed.Should().BeFalse();
    }

    // ── What it refuses ──────────────────────────────────────────────────────────

    [Fact]
    public async Task A_malformed_conversation_is_refused_before_any_turn_runs()
    {
        await using var host = MentorHostFixture.Create();
        var evaluator = host.Get<MentorEvaluator>();

        await evaluator.Invoking(e => e.RunConversationsAsync(
                [Conversation("fine", new EvalTurn("Hi")), Conversation("empty")]))
            .Should().ThrowAsync<ArgumentException>().WithMessage("Conversation 'empty' has no turns.*");
        await evaluator.Invoking(e => e.RunConversationsAsync([Conversation("blank", new EvalTurn(" "))]))
            .Should().ThrowAsync<ArgumentException>().WithMessage("Conversation 'blank' has a turn with no input.*");
        await evaluator.Invoking(e => e.RunConversationsAsync([Conversation("", new EvalTurn("Hi"))]))
            .Should().ThrowAsync<ArgumentException>().WithMessage("Every conversation needs a name.*");

        host.Chat!.Calls.Should().BeEmpty("nothing ran before the malformed case was found");
    }

    [Fact]
    public async Task An_evaluator_built_by_hand_cannot_open_sessions_and_says_how_to_get_one()
    {
        var evaluator = new MentorEvaluator(
            Microsoft.Extensions.Options.Options.Create(new MentorOptions { AppName = "TestApp", ScanAssemblies = [] }),
            NullLogger<MentorEvaluator>.Instance);

        await evaluator.Invoking(e => e.RunConversationsAsync([Conversation("c", new EvalTurn("Hi"))]))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*resolve MentorEvaluator from the container*");
    }

    [Fact]
    public async Task Options_that_apply_to_single_cases_only_are_named_in_a_warning()
    {
        await using var host = MentorHostFixture.Create();
        host.Chat!.Say("Hello.");

        await RunAsync(host,
            new MentorEvalOptions { SystemInstructions = "You are terse.", Checks = [EvalChecks.KeywordCheck("Hello")] },
            Conversation("c", new EvalTurn("Hi")));

        host.Logs.HasWarning("SystemInstructions, Checks apply to RunAsync only").Should().BeTrue(host.Logs.WarningText);
        host.Chat.Calls.Single().SystemText.Should().NotContain("You are terse.");
    }
}
