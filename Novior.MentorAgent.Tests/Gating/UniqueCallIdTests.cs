using FluentAssertions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.App;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Novior.MentorAgent.Tests.Gating;

/// <summary>
/// BUG-225 — a model that gave two calls the same id in one response broke the conversation in Native mode: the
/// function-invoking client keys its approval requests by the call id ("ficc_&lt;id&gt;") and threw "An item with the
/// same key has already been added" when the two were answered. The stored conversation ended on both requests, the
/// next turn closed them with two answers of one id, failed the same way, and kept them for the turn after: every
/// turn failed until the conversation was reset. In Blocking mode both calls ran and both results went back to the
/// model under one id. A call whose id the response or the conversation already holds is now given the next free
/// <c>&lt;id&gt;_n</c> before the function-invoking client sees it. With service-managed history the service holds the
/// ids it generated, so nothing is renamed there and a warning says so; on path B the host's agent runs the calls
/// before MentorAgent's clients see them.
/// </summary>
public sealed class UniqueCallIdTests
{
    private static ChatResponse Calls(FakeChatClient chat, params FunctionCallContent[] calls)
        => new(new ChatMessage(ChatRole.Assistant, [.. calls])) { ModelId = chat.ModelId };

    private static ChatResponse Text(FakeChatClient chat, string text)
        => new(new ChatMessage(ChatRole.Assistant, text)) { ModelId = chat.ModelId };

    private static FunctionCallContent Refund(string id) => new(id, "issue_refund", new Dictionary<string, object?> { ["orderId"] = 1001 });

    private static FunctionCallContent Weather(string id) => new(id, "get_weather", new Dictionary<string, object?> { ["city"] = "Rome" });

    private static bool Answered(IReadOnlyList<ChatMessage> messages)
        => messages.LastOrDefault()?.Contents.OfType<FunctionResultContent>().Any() == true
           || messages.LastOrDefault()?.Contents.OfType<ToolApprovalResponseContent>().Any() == true;

    /// <summary>The ids of the results in a request, in order.</summary>
    private static List<string> ResultIds(FakeChatClient.RecordedCall call)
        => [.. call.Messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(r => r.CallId)];

    private static List<string> CallIds(FakeChatClient.RecordedCall call)
        => [.. call.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Select(c => c.CallId)];

    private static int Refunds(MentorHostFixture host)
        => host.Get<TestOrderService>().Invoked.Count(i => i == nameof(TestOrderService.IssueRefundAsync));

    private static void ConfirmEveryBanner(MentorHostFixture host)
    {
        var state = host.Get<IMentorStateService>();
        state.OnConfirmationRequired += req => _ = state.ConfirmAsync(req.ActionId);
    }

    // ── Native ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task In_native_mode_two_calls_with_one_id_no_longer_break_the_conversation(bool rules)
    {
        await using var host = MentorHostFixture.Create(o =>
        {
            o.HitlMode = MentorHitlMode.Native;
            o.MemoryAutoCapture = false;
            if (rules) o.AutoApprovalRules = [_ => ValueTask.FromResult(false)];
        });
        var chat = host.Chat!;
        chat.Respond((messages, _) => messages.Any(m => m.Text.Contains("open orders")) || Answered(messages)
            ? Text(chat, "done")
            : Calls(chat, Refund("same-id"), Refund("same-id")));
        ConfirmEveryBanner(host);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await host.RunTurnAsync("refund order 1001 twice", deadline.Token);
        var afterFirst = chat.CallCount;
        var error = await host.RunTurnAsync("how many open orders?", deadline.Token);

        host.Logs.ExceptionText.Should().NotContain("same key", "rules: {0}", rules);
        host.Logs.Records.Should().NotContain(r => r.Message.Contains("Error in Coordinator"));
        Refunds(host).Should().Be(2, "two calls, each confirmed");
        chat.Calls.Take(afterFirst).SelectMany(ResultIds).Should().BeEquivalentTo(["same-id", "same-id_2"]);
        error.Should().BeNull();
        chat.CallCount.Should().BeGreaterThan(afterFirst, "the turn after reaches the model");
        chat.Calls[^1].Messages.Should().Contain(m => m.Text.Contains("how many open orders?"));
    }

    // ── Blocking ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task In_blocking_mode_the_two_results_go_back_under_two_ids()
    {
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false);
        var chat = host.Chat!;
        chat.Respond((messages, _) => Answered(messages) ? Text(chat, "sunny") : Calls(chat, Weather("w"), Weather("w")));

        (await host.RunTurnAsync("weather in Rome?")).Should().BeNull();

        var followUp = chat.Calls[^1];
        CallIds(followUp).Should().Equal("w", "w_2");
        ResultIds(followUp).Should().Equal("w", "w_2");
    }

    [Fact]
    public async Task An_id_the_conversation_already_holds_is_taken_by_the_next_free_suffix()
    {
        // Turn 1 uses w_2 and h; turn 2 repeats h, and gives w three times: w stays, then w_3 and w_4.
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false);
        var chat = host.Chat!;
        var turn = 0;
        chat.Respond((messages, _) =>
        {
            if (Answered(messages)) return Text(chat, "ok");
            return ++turn == 1
                ? Calls(chat, Weather("w_2"), Weather("h"))
                : Calls(chat, Weather("h"), Weather("w"), Weather("w"), Weather("w"));
        });

        (await host.RunTurnAsync("weather in Rome?")).Should().BeNull();
        (await host.RunTurnAsync("and again?")).Should().BeNull();

        var followUp = chat.Calls[^1];
        CallIds(followUp).Should().Equal("w_2", "h", "h_2", "w", "w_3", "w_4");
        ResultIds(followUp).Should().Equal("w_2", "h", "h_2", "w", "w_3", "w_4");
    }

    [Fact]
    public async Task A_renamed_call_carries_no_raw_representation_and_the_same_name_and_arguments()
    {
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false);
        var chat = host.Chat!;
        var raw = new object();
        chat.Respond((messages, _) => Answered(messages)
            ? Text(chat, "sunny")
            : Calls(chat, Weather("w"), new FunctionCallContent("w", "get_weather",
                new Dictionary<string, object?> { ["city"] = "Milan" }) { RawRepresentation = raw }));

        (await host.RunTurnAsync("weather in Rome and Milan?")).Should().BeNull();

        var renamed = chat.Calls[^1].Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Single(c => c.CallId == "w_2");
        renamed.Name.Should().Be("get_weather");
        renamed.Arguments!["city"].Should().Be("Milan");
        renamed.RawRepresentation.Should().BeNull("a provider adapter would send the raw item, with the old id");
    }

    // ── What does not change ────────────────────────────────────────────────────

    [Fact]
    public async Task With_unique_ids_the_model_gets_back_the_very_call_it_made()
    {
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false);
        var chat = host.Chat!;
        var first = Weather("w");
        var second = Weather("v");
        chat.Respond((messages, _) => Answered(messages) ? Text(chat, "sunny") : Calls(chat, first, second));

        (await host.RunTurnAsync("weather in Rome?")).Should().BeNull();

        var calls = chat.Calls[^1].Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToList();
        calls.Should().HaveCount(2);
        ReferenceEquals(calls[0], first).Should().BeTrue("nothing is copied when no id repeats");
        ReferenceEquals(calls[1], second).Should().BeTrue();
        host.Logs.Records.Should().NotContain(r => r.Message.Contains("same id"));
    }

    [Fact]
    public async Task With_service_managed_history_nothing_is_renamed_and_a_warning_says_so_once()
    {
        await using var host = MentorHostFixture.Create(o =>
        {
            o.UseServiceManagedHistory = true;
            o.MemoryAutoCapture = false;
        });
        var chat = host.Chat!;
        chat.Respond((messages, _) => Answered(messages)
            ? new ChatResponse(new ChatMessage(ChatRole.Assistant, "sunny")) { ModelId = chat.ModelId, ConversationId = "resp_2" }
            : new ChatResponse(new ChatMessage(ChatRole.Assistant, [Weather("w"), Weather("w")])) { ModelId = chat.ModelId, ConversationId = "resp_1" });

        await host.RunTurnAsync("weather in Rome?");
        await host.RunTurnAsync("and in Milan?");

        chat.Calls.SelectMany(ResultIds).Should().NotContain("w_2", "the service holds the ids it generated");
        host.Logs.Records.Count(r => r.Level == LogLevel.Warning && r.Message.Contains("same id")).Should().Be(1);
    }

    // ── A conversation that cannot go on says so ────────────────────────────────

    [Fact]
    public async Task Two_turns_that_fail_the_same_way_on_the_requests_left_open_are_logged_as_an_error_once()
    {
        await using var host = MentorHostFixture.Create(o => { o.HitlMode = MentorHitlMode.Native; o.MemoryAutoCapture = false; });
        var chat = host.Chat!;
        chat.CallTool("issue_refund", new { orderId = 1001 }).SayRepeatedly("ok", 5);
        var first = true;
        host.Get<IMentorStateService>().OnConfirmationRequired += _ =>
        {
            if (!first) return;
            first = false;
            host.Orchestrator.CancelCurrentRequest();
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await host.RunTurnAsync("refund order 1001", deadline.Token);            // left open by the Stop
        for (var i = 0; i < 3; i++)
        {
            chat.ThrowOnNextCall = new InvalidOperationException("the same failure");
            await host.RunTurnAsync("how many open orders?", deadline.Token);
        }
        var error = await host.RunTurnAsync("and the delivered ones?", deadline.Token);

        host.Logs.Records.Count(r => r.Level == LogLevel.Error && r.Message.Contains("start a new conversation"))
            .Should().Be(1, "the second failure says it, the third does not repeat it");
        error.Should().BeNull("a turn that does not fail still closes the request");
    }

    [Fact]
    public async Task A_failure_of_another_kind_each_time_is_not_logged_as_a_conversation_that_cannot_go_on()
    {
        await using var host = MentorHostFixture.Create(o => { o.HitlMode = MentorHitlMode.Native; o.MemoryAutoCapture = false; });
        var chat = host.Chat!;
        chat.CallTool("issue_refund", new { orderId = 1001 }).SayRepeatedly("ok", 5);
        var first = true;
        host.Get<IMentorStateService>().OnConfirmationRequired += _ =>
        {
            if (!first) return;
            first = false;
            host.Orchestrator.CancelCurrentRequest();
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await host.RunTurnAsync("refund order 1001", deadline.Token);
        chat.ThrowOnNextCall = new InvalidOperationException("one failure");
        await host.RunTurnAsync("how many open orders?", deadline.Token);
        chat.ThrowOnNextCall = new InvalidOperationException("another failure");
        await host.RunTurnAsync("how many open orders?", deadline.Token);

        host.Logs.Records.Should().NotContain(r => r.Message.Contains("start a new conversation"));
    }

    [Fact]
    public async Task Stops_in_a_row_are_not_logged_as_a_conversation_that_cannot_go_on()
    {
        await using var host = MentorHostFixture.Create(o => { o.HitlMode = MentorHitlMode.Native; o.MemoryAutoCapture = false; });
        var chat = host.Chat!;
        chat.CallTool("issue_refund", new { orderId = 1001 }).SayRepeatedly("ok", 5);
        var first = true;
        host.Get<IMentorStateService>().OnConfirmationRequired += _ =>
        {
            if (!first) return;
            first = false;
            host.Orchestrator.CancelCurrentRequest();
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await host.RunTurnAsync("refund order 1001", deadline.Token);
        chat.BeforeRespond = () => { host.Orchestrator.CancelCurrentRequest(); return Task.CompletedTask; };
        await host.RunTurnAsync("how many open orders?", deadline.Token);
        await host.RunTurnAsync("how many open orders?", deadline.Token);
        chat.BeforeRespond = null;

        host.Logs.Records.Should().NotContain(r => r.Message.Contains("start a new conversation"), "a Stop is the user's");
    }

    // ── The agents under the coordinator, and path B ────────────────────────────

    [Fact]
    public async Task A_specialists_two_calls_with_one_id_go_back_under_two_ids()
    {
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false);
        var chat = host.Chat!;
        FunctionCallContent Track(string id) => new(id, "get_tracking_status", new Dictionary<string, object?> { ["orderId"] = 7 });
        var delegating = ActionDecisionTests.Delegating(host, "where is order 7?", "shipping", "get_tracking_status",
            new() { ["orderId"] = 7 });
        chat.Respond((messages, options) =>
        {
            var tools = options?.Tools?.Select(t => t.Name).ToList() ?? [];
            if (tools.Contains("get_tracking_status") && !tools.Contains("route_to_specialist") && !Answered(messages))
                return Calls(chat, Track("t"), Track("t"));
            return delegating(messages, options);
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        (await host.RunTurnAsync("where is order 7?", deadline.Token)).Should().BeNull();

        var specialistFollowUp = chat.Calls.Last(c => c.ToolNames.Contains("get_tracking_status")
                                                      && c.Messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Any());
        ResultIds(specialistFollowUp).Should().Contain(["t", "t_2"], "warnings: {0}", host.Logs.WarningText);
    }

    [Fact]
    public async Task On_path_B_the_ids_are_the_hosts_agents()
    {
        // A limit, pinned: the client factory of a run decorates the host agent's client outside its function-invoking
        // client, so a call reaches it already run. The host's agent and its client are the host's.
        var inner = new FakeChatClient();
        await using var host = MentorHostFixture.Create(
            o =>
            {
                o.ChatClient = null;
                o.Agent = inner.AsAIAgent(instructions: "You are a test agent.");
                o.MemoryAutoCapture = false;
            },
            withChatClient: false);
        inner.Respond((messages, _) => Answered(messages) ? Text(inner, "sunny") : Calls(inner, Weather("w"), Weather("w")));

        (await host.RunTurnAsync("weather in Rome?")).Should().BeNull();

        ResultIds(inner.Calls[^1]).Should().Equal(["w", "w"], "path B is outside what MentorAgent can rename");
    }
}
