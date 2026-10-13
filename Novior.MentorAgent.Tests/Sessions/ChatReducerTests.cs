using FluentAssertions;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.AI;
using Xunit;

namespace Novior.MentorAgent.Tests.Sessions;

/// <summary>
/// The default history reducer (<see cref="MentorChatReducer"/>) and the <c>ChatReducer</c> option.
///
/// <para>
/// Up to rc.14 the built-in history was reduced by the framework's <c>MessageCountingChatReducer</c>,
/// which drops every message carrying a function call or a function result. From the next turn on the
/// model no longer saw the data a tool had read: measured on rc.12, 50 turns out of 84 lost it, 85% of
/// turns answered right against 95% with a reducer that keeps calls and results. The rules pinned
/// here: a result stays visible on the next turn, a result never reaches the model without its call,
/// turns are cut whole, and the old reducer can still be chosen.
/// </para>
/// </summary>
public sealed class ChatReducerTests
{
    // ── Building blocks ─────────────────────────────────────────────────────────

    private static ChatMessage User(string text) => new(ChatRole.User, text);
    private static ChatMessage Answer(string text) => new(ChatRole.Assistant, text);
    private static ChatMessage Call(params string[] ids)
        => new(ChatRole.Assistant, [.. ids.Select(id => (AIContent)new FunctionCallContent(id, "get_orders"))]);
    private static ChatMessage Result(params string[] ids)
        => new(ChatRole.Tool, [.. ids.Select(id => (AIContent)new FunctionResultContent(id, $"data-{id}"))]);

    private static List<ChatMessage> TextTurn(int n) => [User($"q{n}"), Answer($"a{n}")];
    private static List<ChatMessage> ToolTurn(int n) => [User($"q{n}"), Call($"c{n}"), Result($"c{n}"), Answer($"a{n}")];

    private static async Task<List<ChatMessage>> ReduceAsync(int max, IEnumerable<ChatMessage> history)
        => [.. await new MentorChatReducer(max).ReduceAsync(history)];

    private static IEnumerable<string> CallIds(IEnumerable<ChatMessage> messages)
        => messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Select(c => c.CallId);

    private static IEnumerable<string> ResultIds(IEnumerable<ChatMessage> messages)
        => messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(r => r.CallId);

    // ── The reducer on its own ──────────────────────────────────────────────────

    [Fact]
    public async Task Under_the_budget_the_history_is_returned_as_it_is()
    {
        List<ChatMessage> history = [.. TextTurn(1), .. ToolTurn(2)];

        var reduced = await ReduceAsync(50, history);

        reduced.Should().Equal(history, (a, b) => ReferenceEquals(a, b));
    }

    [Fact]
    public async Task The_previous_turns_tool_result_stays_visible()
    {
        List<ChatMessage> history = [.. TextTurn(1), .. TextTurn(2), .. ToolTurn(3)];

        var reduced = await ReduceAsync(4, history);

        reduced.Should().Equal(ToolTurn(3), (a, b) => a.Role == b.Role && a.Text == b.Text
                                                       && a.Contents.Count == b.Contents.Count);
        ResultIds(reduced).Should().Equal("c3");
        CallIds(reduced).Should().Equal("c3");
    }

    [Fact]
    public async Task The_rc14_reducer_drops_the_same_result()
    {
        // The control for the test above: the behaviour this story replaces, on the same history.
        List<ChatMessage> history = [.. TextTurn(1), .. TextTurn(2), .. ToolTurn(3)];

#pragma warning disable MEAI001 // MessageCountingChatReducer is experimental
        var reduced = (await new MessageCountingChatReducer(4).ReduceAsync(history, CancellationToken.None)).ToList();
#pragma warning restore MEAI001

        ResultIds(reduced).Should().BeEmpty();
        CallIds(reduced).Should().BeEmpty();
    }

    [Fact]
    public async Task Turns_are_cut_whole_oldest_first()
    {
        // Turn 2 (four messages) does not fit next to turn 3 in five: it goes entirely, though three
        // of its messages would have fitted.
        List<ChatMessage> history = [.. TextTurn(1), .. ToolTurn(2), .. TextTurn(3)];

        var reduced = await ReduceAsync(5, history);

        reduced.Select(m => m.Text).Should().Equal("q3", "a3");
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(10)] [InlineData(11)] [InlineData(12)]
    [InlineData(13)] [InlineData(14)] [InlineData(15)] [InlineData(16)] [InlineData(17)] [InlineData(18)]
    public async Task Never_a_result_without_its_call(int max)
    {
        // A mixed history: two calls in one message answered in two messages, a second round in the
        // same turn, a text turn, and a newest turn with two rounds.
        List<ChatMessage> history =
        [
            User("q1"), Call("a", "b"), Result("a"), Result("b"), Call("c"), Result("c"), Answer("a1"),
            .. TextTurn(2),
            User("q3"), Call("d"), Result("d"), Call("e", "f"), Result("e", "f"), Answer("a3"),
        ];

        var reduced = await ReduceAsync(max, history);

        reduced.Should().HaveCountLessThanOrEqualTo(max);
        for (var i = 0; i < reduced.Count; i++)
            foreach (var id in ResultIds([reduced[i]]))
                CallIds(reduced.Take(i)).Should().Contain(id, $"the result {id} needs its call before it (max {max})");
        ResultIds(reduced).Should().BeEquivalentTo(CallIds(reduced),
            "a kept call keeps its result too: a call left unanswered is rejected by the provider");
        reduced.Should().NotBeEmpty();
        reduced[^1].Text.Should().Be("a3", "the newest answer is always kept");
        if (max >= 2)
            reduced[0].Text.Should().BeOneOf(["q1", "q2", "q3"], "a kept turn starts with its question");
    }

    [Fact]
    public async Task An_approval_answer_does_not_start_a_turn()
    {
        // With Native approvals the user's answer is a User message. Read as a turn start, a budget of
        // seven would keep the five messages after it: the answer and the call, without the request.
        var request = new ToolApprovalRequestContent("r1", new FunctionCallContent("c1", "update_order"));
        List<ChatMessage> history =
        [
            User("q1"),
            new(ChatRole.Assistant, [request]),
            new(ChatRole.User, [new ToolApprovalResponseContent("r1", true, request.ToolCall)]),
            Call("c1"), Result("c1"), Answer("a1"),
            .. TextTurn(2),
        ];

        var reduced = await ReduceAsync(7, history);

        reduced.Select(m => m.Text).Should().Equal("q2", "a2");
    }

    [Fact]
    public async Task An_approval_answer_whose_request_was_cut_goes_with_it()
    {
        // A newest turn over budget keeps its question and its latest messages: the approval request
        // falls out, so its answer must too.
        var request = new ToolApprovalRequestContent("r1", new FunctionCallContent("c1", "update_order"));
        List<ChatMessage> history =
        [
            User("q1"),
            new(ChatRole.Assistant, [request]),
            new(ChatRole.User, [new ToolApprovalResponseContent("r1", true, request.ToolCall)]),
            Answer("a1"),
        ];

        var reduced = await ReduceAsync(3, history);

        reduced.SelectMany(m => m.Contents).Should().NotContain(c => c is ToolApprovalResponseContent);
        reduced.Select(m => m.Text).Should().Equal("q1", "a1");
    }

    [Fact]
    public async Task When_the_newest_turn_alone_is_over_budget_its_question_and_latest_messages_stay()
    {
        List<ChatMessage> history =
            [User("q1"), Call("c1"), Result("c1"), Call("c2"), Result("c2"), Answer("a1")];

        (await ReduceAsync(4, history)).Should().HaveCount(4)
            .And.Subject.Should().SatisfyRespectively(
                m => m.Text.Should().Be("q1"),
                m => CallIds([m]).Should().Equal("c2"),
                m => ResultIds([m]).Should().Equal("c2"),
                m => m.Text.Should().Be("a1"));

        // Three: the question and the last two messages, whose result lost its call — so it goes.
        (await ReduceAsync(3, history)).Select(m => m.Text).Should().Equal("q1", "a1");

        // One: the answer alone, as MessageCountingChatReducer keeps it.
        (await ReduceAsync(1, history)).Select(m => m.Text).Should().Equal("a1");
    }

    [Fact]
    public async Task The_first_system_message_is_kept_and_not_counted()
    {
        List<ChatMessage> history = [new(ChatRole.System, "rules"), .. TextTurn(1), .. TextTurn(2)];

        var reduced = await ReduceAsync(2, history);

        reduced.Select(m => m.Text).Should().Equal("rules", "q2", "a2");
    }

    [Fact]
    public async Task A_result_that_was_already_unmatched_is_left_alone()
    {
        // Only what the reduction cut is considered: a store that already held a lone result gets it back.
        List<ChatMessage> history = [.. TextTurn(1), User("q2"), Result("x"), Answer("a2")];

        var reduced = await ReduceAsync(3, history);

        ResultIds(reduced).Should().Equal("x");
    }

    [Fact]
    public async Task The_stored_messages_are_not_changed()
    {
        // One message carries two results; the call of the first was cut. The model gets a copy with the
        // second only, and the stored message keeps both.
        var results = Result("c1", "c2");
        List<ChatMessage> history = [User("q1"), Call("c1"), Call("c2"), results, Answer("a1")];

        var reduced = await ReduceAsync(4, history);

        ResultIds(reduced).Should().Equal("c2");
        results.Contents.Should().HaveCount(2);
        reduced.Should().NotContain(results);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_budget_below_one_is_refused(int max)
    {
        var act = () => new MentorChatReducer(max);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ── Through the orchestrator ────────────────────────────────────────────────

    private const string ToolQuestion = "Quanti ordini aperti?";

    private static MentorHostFixture ToolTurnHost(Action<Novior.MentorAgent.Options.MentorOptions>? configure = null)
    {
        var host = MentorHostFixture.Create(o =>
        {
            o.MemoryAutoCapture = false;
            configure?.Invoke(o);
        });
        host.Chat!.CallTool("count_open_orders").Say("Due ordini aperti.").Say("Il più vecchio è del 2 ottobre.");
        return host;
    }

    private static async Task RunToolTurnThenFollowUpAsync(MentorHostFixture host)
    {
        (await host.RunTurnAsync(ToolQuestion)).Should().BeNull();
        (await host.RunTurnAsync("E il più vecchio?")).Should().BeNull();
    }

    [Fact]
    public async Task By_default_the_next_turn_sees_the_tool_call_and_its_result()
    {
        await using var host = ToolTurnHost();

        await RunToolTurnThenFollowUpAsync(host);

        var followUp = host.Chat!.Calls[2].Messages;
        var call = followUp.SelectMany(m => m.Contents).OfType<FunctionCallContent>()
            .Should().ContainSingle(c => c.Name == "count_open_orders").Subject;
        followUp.SelectMany(m => m.Contents).OfType<FunctionResultContent>()
            .Should().ContainSingle(r => r.CallId == call.CallId, "the data the tool read is still in front of the model");
    }

    [Fact]
    public async Task A_tight_cap_cuts_the_older_turn_and_keeps_the_tool_turn()
    {
        // Cap 4: the tool turn (question, call, result, answer) fits on its own; the greeting before it
        // does not fit next to it and goes whole.
        await using var host = MentorHostFixture.Create(o =>
        {
            o.MemoryAutoCapture = false;
            o.MaxSessionMessages = 4;
        });
        host.Chat!.Say("Ciao!").CallTool("count_open_orders").Say("Due ordini aperti.").Say("Il più vecchio è del 2 ottobre.");

        (await host.RunTurnAsync("Ciao")).Should().BeNull();
        (await host.RunTurnAsync(ToolQuestion)).Should().BeNull();
        (await host.RunTurnAsync("E il più vecchio?")).Should().BeNull();

        var lastTurn = host.Chat.Calls[^1].Messages.Where(m => m.Role != ChatRole.System).ToList();
        lastTurn.Should().NotContain(m => m.Role == ChatRole.User && m.Text == "Ciao", "the oldest turn is cut whole");
        lastTurn.Should().Contain(m => m.Role == ChatRole.User && m.Text == ToolQuestion);
        ResultIds(lastTurn).Should().ContainSingle();
        CallIds(lastTurn).Should().Equal(ResultIds(lastTurn));
    }

    [Fact]
    public async Task The_rc14_reducer_is_still_selectable()
    {
        await using var host = ToolTurnHost(o =>
        {
#pragma warning disable MEAI001 // MessageCountingChatReducer is experimental
            o.ChatReducer = new MessageCountingChatReducer(50);
#pragma warning restore MEAI001
        });

        await RunToolTurnThenFollowUpAsync(host);

        var followUp = host.Chat!.Calls[2].Messages;
        followUp.SelectMany(m => m.Contents).Should().NotContain(c => c is FunctionCallContent || c is FunctionResultContent,
            "the framework's reducer drops calls and results — the behaviour up to rc.14, on request");
        followUp.Should().Contain(m => m.Role == ChatRole.User && m.Text == ToolQuestion);
    }

    [Fact]
    public async Task A_supplied_reducer_replaces_the_default_and_the_cap()
    {
        var reducer = new RecordingReducer();
        await using var host = ToolTurnHost(o =>
        {
            o.ChatReducer = reducer;
            o.MaxSessionMessages = 0;   // not read when a reducer is supplied: no "will not be trimmed" warning
        });

        await RunToolTurnThenFollowUpAsync(host);

        reducer.Calls.Should().BeGreaterThan(0);
        host.Logs.HasWarning("history will not be trimmed").Should().BeFalse();
    }

    [Fact]
    public async Task A_reducer_next_to_a_supplied_provider_is_reported()
    {
        await using var host = ToolTurnHost(o =>
        {
            o.ChatReducer = new MentorChatReducer(10);
            o.ChatHistoryProviderFactory = _ => new Microsoft.Agents.AI.InMemoryChatHistoryProvider();
        });

        await host.RunTurnAsync(ToolQuestion);

        host.Logs.HasWarning("ChatReducer applies only to the built-in history").Should().BeTrue();
    }

    [Fact]
    public async Task A_reducer_with_service_managed_history_is_reported()
    {
        await using var host = MentorHostFixture.Create(o =>
        {
            o.MemoryAutoCapture = false;
            o.UseServiceManagedHistory = true;
            o.ChatReducer = new MentorChatReducer(10);
        });
        host.Chat!.Say("ok");

        await host.RunTurnAsync("hello");

        host.Logs.HasWarning("ChatReducer has no effect with UseServiceManagedHistory").Should().BeTrue();
    }

    private sealed class RecordingReducer : IChatReducer
    {
        private int _calls;
        internal int Calls => _calls;

        public Task<IEnumerable<ChatMessage>> ReduceAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(messages);
        }
    }
}
