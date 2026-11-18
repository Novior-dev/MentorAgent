using System.Text.Json;
using FluentAssertions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Middleware;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Novior.MentorAgent.Tests.Pages;

/// <summary>
/// <see cref="UIActionTools"/> — the piece that turns the current page's registered actions into named
/// tools of the run.
///
/// <para>
/// Up to 1.3.0 <see cref="UIActionsMiddleware"/> added them below the agent and ran their calls in a loop
/// of its own. From story 1288 they are tools of the run, called by the agent's function-invoking client
/// through the gate, and brought up to the page after every call. Every step is still a place where a
/// call can be executed twice, executed against the wrong page, or silently dropped.
/// </para>
///
/// <para>
/// Driven here through a <see cref="FunctionInvokingChatClient"/> over the middleware, with the page's
/// actions among the request's tools and refreshed after each call — what the agent's run does, without
/// the gate between the assertion and the behaviour. The gate itself is <c>UIActionGateTests</c>.
/// </para>
/// </summary>
public sealed class UIActionToolTests
{
    private sealed class Rig
    {
        internal MentorPageContext Page { get; } = new();
        internal FakeChatClient Inner { get; } = new();
        internal RecordingStateService State { get; } = new();
        internal TestLogCollector Logs { get; } = new();

        private ILogger<UIActionsMiddleware> Logger
            => new TypedLogger<UIActionsMiddleware>(new TestLoggerProvider(Logs).CreateLogger("ui"));

        /// <summary>
        /// The run as the agent makes it: a function-invoking client over the middleware, the page's actions
        /// refreshed after every call, as the gate does.
        /// </summary>
        private (IChatClient Client, ChatOptions Options) Run(ChatOptions? options)
        {
            var tools = new UIActionTools(Page, State, Logger);
            var client = new FunctionInvokingChatClient(new UIActionsMiddleware(Inner, Page, State, Logger))
            {
                FunctionInvoker = async (context, ct) =>
                {
                    try { return await context.Function.InvokeAsync(context.Arguments, ct); }
                    finally { tools.Refresh(context.Options?.Tools); }
                },
            };

            var run = options?.Clone() ?? new ChatOptions();
            var page = tools.Current(run.Tools);
            if (page.Count > 0) run.Tools = [.. run.Tools ?? [], .. page];
            return (client, run);
        }

        /// <summary>Runs one non-streaming request of the run.</summary>
        internal async Task<ChatResponse> SendAsync(ChatOptions? options = null)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var (client, run) = Run(options);
            return await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "do the thing")], run, deadline.Token);
        }

        /// <summary>Runs one streamed request of the run and returns the text.</summary>
        internal async Task<string> StreamAsync(ChatOptions? options = null)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var (client, run) = Run(options);
            var text = new System.Text.StringBuilder();

            await foreach (var update in client.GetStreamingResponseAsync(
                               [new ChatMessage(ChatRole.User, "do the thing")], run, deadline.Token))
                text.Append(update.Text);

            return text.ToString();
        }

        /// <summary>The tool names offered to the model on the first call.</summary>
        internal IReadOnlyList<string> FirstToolsSent => Inner.Calls[0].ToolNames;
    }

    /// <summary>Queues a call to <paramref name="name"/>, then a plain reply.</summary>
    private static void CallThenReply(FakeChatClient chat, string name, object? args = null,
                                      string reply = "done")
    {
        chat.CallTool(name, args).Say(reply);
    }

    // ── Nothing registered ──────────────────────────────────────────────────────

    [Fact]
    public async Task With_no_actions_registered_nothing_is_added_to_the_tool_list()
    {
        var rig = new Rig();
        rig.Inner.Say("hello");

        await rig.SendAsync(new ChatOptions { Tools = [Existing("get_orders")] });

        string.Join(",", rig.FirstToolsSent).Should().Be("get_orders");
    }

    [Fact]
    public async Task With_no_actions_registered_streaming_really_streams()
    {
        // With no actions registered nothing of the page is in the way at all. Kept as the control
        // for the streaming assertions in UIActionStreamingTests.
        var rig = new Rig();
        rig.Inner.Say("hello there");

        await rig.StreamAsync();

        rig.Inner.Calls[0].Streaming.Should().BeTrue();
    }

    [Fact]
    public async Task With_actions_registered_the_loop_still_streams()
    {
        // This test used to assert the opposite, and called it "the documented trade-off": with a
        // UI action registered the middleware ran its loop through GetResponseAsync and replayed the
        // finished reply as updates. The trade-off was real but its cost had never been felt,
        // because no live client with UI actions had ever been driven — a page that registers one
        // action lost token streaming for every turn, including the ordinary ones that never call
        // an action. BUG-052 fixed that; this now pins the fix from the inner client's side. From
        // story 1288 there is no loop of the page's own: the run streams like any other.
        var rig = new Rig();
        rig.Page.RegisterUIAction("highlight_row", "Highlights a row", _ => { });
        rig.Inner.Say("hello there");

        await rig.StreamAsync();

        rig.Inner.Calls[0].Streaming.Should().BeTrue(
            "the request with the page's actions is streamed, not buffered");
    }

    [Fact]
    public async Task The_reply_still_reaches_the_caller_in_full()
    {
        var rig = new Rig();
        rig.Page.RegisterUIAction("highlight_row", "Highlights a row", _ => { });
        rig.Inner.Say("all done");

        (await rig.StreamAsync()).Should().Be("all done");
    }

    // ── Tool injection ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Each_registered_action_becomes_its_own_named_tool()
    {
        // Not a generic dispatcher. The model calls highlight_row(5), which it cannot hallucinate
        // the name of — where invoke_ui_action("hilite_row", 5) is a plausible-looking mistake.
        var rig = new Rig();
        rig.Page.RegisterUIAction("highlight_row", "Highlights a row", _ => { })
                .RegisterUIAction("clear_filters", "Clears every filter", _ => { });
        rig.Inner.Say("ok");

        await rig.SendAsync();

        string.Join(",", rig.FirstToolsSent).Should().Contain("highlight_row").And.Contain("clear_filters");
    }

    [Fact]
    public async Task The_existing_tools_survive_the_merge()
    {
        var rig = new Rig();
        rig.Page.RegisterUIAction("highlight_row", "Highlights a row", _ => { });
        rig.Inner.Say("ok");

        await rig.SendAsync(new ChatOptions { Tools = [Existing("get_orders")] });

        string.Join(",", rig.FirstToolsSent).Should().Contain("get_orders").And.Contain("highlight_row");
    }

    [Fact]
    public async Task An_action_that_collides_with_a_real_tool_is_skipped_and_reported()
    {
        // Two tools with one name is ambiguous at best; at worst the model calls the page action
        // expecting the business tool. The business tool wins: it is the application's action, and
        // the page cannot shadow it.
        var rig = new Rig();
        rig.Page.RegisterUIAction("get_orders", "A page action with an unlucky name", _ => { });
        rig.Inner.Say("ok");

        await rig.SendAsync(new ChatOptions { Tools = [Existing("get_orders")] });

        rig.FirstToolsSent.Count(n => n == "get_orders").Should().Be(1);
        rig.Logs.HasWarning("conflicts with an existing tool").Should().BeTrue(rig.Logs.WarningText);
    }

    [Fact]
    public async Task The_parameter_hint_reaches_the_model_in_the_tool_description()
    {
        // The hint is generated from the handler's type and is the only description of the argument
        // the model gets. Dropping it here would make every generated hint dead code.
        var rig = new Rig();
        rig.Page.RegisterUIAction<int>("highlight_row", "Highlights a row", _ => { });
        rig.Inner.Say("ok");

        await rig.SendAsync();

        DescriptionOf(rig, "highlight_row").Should().Be("Highlights a row (parameter: integer)");
    }

    [Fact]
    public async Task An_action_with_no_hint_keeps_its_plain_description()
    {
        var rig = new Rig();
        rig.Page.RegisterUIAction("refresh", "Refreshes the grid", _ => { });
        rig.Inner.Say("ok");

        await rig.SendAsync();

        DescriptionOf(rig, "refresh").Should().Be("Refreshes the grid");
    }

    [Fact]
    public async Task The_conversation_id_survives_the_options_clone()
    {
        // The page's actions travel in the run's options. A hand-written copy of options dropped
        // ConversationId, and under service-managed history that loses previous_response_id — the
        // provider then rejects the tool result with "HTTP 400 — No tool call found for function call
        // output".
        var rig = new Rig();
        rig.Page.RegisterUIAction("highlight_row", "Highlights a row", _ => { });
        rig.Inner.Say("ok");

        await rig.SendAsync(new ChatOptions { ConversationId = "conv-123" });

        rig.Inner.Calls[0].ConversationId.Should().Be("conv-123");
    }

    // ── Execution ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Calling_an_action_runs_its_handler()
    {
        var rig = new Rig();
        var ran = false;
        rig.Page.RegisterUIAction("highlight_row", "Highlights a row", _ => ran = true);
        CallThenReply(rig.Inner, "highlight_row");

        await rig.SendAsync();

        ran.Should().BeTrue();
    }

    [Fact]
    public async Task The_user_is_told_the_action_started_and_finished()
    {
        var rig = new Rig();
        rig.Page.RegisterUIAction("highlight_row", "Highlights a row", _ => { });
        CallThenReply(rig.Inner, "highlight_row");

        await rig.SendAsync();

        string.Join(",", rig.State.Sequence)
              .Should().Be("NotifyUIActionExecuting,NotifyUIActionCompleted");
    }

    [Fact]
    public async Task A_handler_that_throws_reports_an_error_and_the_turn_carries_on()
    {
        // The handler is the host's own page code, running on a background thread that reaches into
        // a live component. It will throw eventually — a stale reference, a disposed component —
        // and taking the turn down with it would lose the user's answer.
        var rig = new Rig();
        rig.Page.RegisterUIAction("highlight_row", "Highlights a row",
            _ => throw new InvalidOperationException("the grid is gone"));
        CallThenReply(rig.Inner, "highlight_row", reply: "I could not highlight it.");

        var response = await rig.SendAsync();

        response.Text.Should().Be("I could not highlight it.");
        rig.Logs.HasWarning("threw an exception").Should().BeTrue(rig.Logs.WarningText);
        ToolResultsOf(rig).Should().Contain("error");
        rig.State.Sequence.Should().NotContain(nameof(RecordingStateService.NotifyUIActionCompleted),
            "an action that threw did not complete");
    }

    [Fact]
    public async Task The_model_is_told_the_action_succeeded()
    {
        // Without a tool result the follow-up request is malformed, and the model has no way to
        // know whether to describe the action as done.
        var rig = new Rig();
        rig.Page.RegisterUIAction("highlight_row", "Highlights a row", _ => { });
        CallThenReply(rig.Inner, "highlight_row");

        await rig.SendAsync();

        ToolResultsOf(rig).Should().Contain("highlight_row");
    }

    [Fact]
    public async Task Several_actions_in_one_response_all_run()
    {
        var rig = new Rig();
        var ran = new List<string>();
        rig.Page.RegisterUIAction("a", "d", _ => ran.Add("a")).RegisterUIAction("b", "d", _ => ran.Add("b"));

        rig.Inner.Respond((_, _) => new ChatResponse(new ChatMessage(ChatRole.Assistant,
        [
            new FunctionCallContent("1", "a", new Dictionary<string, object?>()),
            new FunctionCallContent("2", "b", new Dictionary<string, object?>()),
        ]))).Say("done");

        await rig.SendAsync();

        string.Join(",", ran).Should().Be("a,b");
    }

    // ── How the argument is found ───────────────────────────────────────────────

    [Fact]
    public async Task The_canonical_parameter_argument_is_used()
    {
        var rig = new Rig();
        object? got = null;
        rig.Page.RegisterUIAction("highlight_row", "d", p => got = p);
        CallThenReply(rig.Inner, "highlight_row", new Dictionary<string, object?> { ["parameter"] = 5 });

        await rig.SendAsync();

        got.Should().Be(5);
    }

    [Fact]
    public async Task A_single_argument_under_any_name_is_used()
    {
        // Models rename the argument to something domain-shaped — highlight_row(rowId: 5). Refusing
        // it would make the action fail for a call that is entirely reasonable.
        var rig = new Rig();
        object? got = null;
        rig.Page.RegisterUIAction("highlight_row", "d", p => got = p);
        CallThenReply(rig.Inner, "highlight_row", new Dictionary<string, object?> { ["rowId"] = 5 });

        await rig.SendAsync();

        got.Should().Be(5);
    }

    [Fact]
    public async Task Several_flat_arguments_are_rewrapped_into_the_dto()
    {
        // The model unpacked the DTO into top-level fields instead of nesting it. Re-serialising
        // them as one object is what lets the typed handler still deserialise it.
        var rig = new Rig();
        OrderFilter? got = null;
        rig.Page.RegisterUIAction<OrderFilter>("apply_filter", "d", f => got = f);
        CallThenReply(rig.Inner, "apply_filter",
            new Dictionary<string, object?> { ["customerId"] = 3, ["onlyOpen"] = true });

        await rig.SendAsync();

        got.Should().NotBeNull();
        got!.CustomerId.Should().Be(3);
        got.OnlyOpen.Should().BeTrue();
    }

    [Fact]
    public async Task An_action_called_with_no_arguments_gets_null()
    {
        var rig = new Rig();
        object? got = "not-null";
        rig.Page.RegisterUIAction("refresh", "d", p => got = p);
        CallThenReply(rig.Inner, "refresh");

        await rig.SendAsync();

        got.Should().BeNull();
    }

    // ── What is not ours ────────────────────────────────────────────────────────

    [Fact]
    public async Task A_call_to_a_business_tool_runs_the_business_tool_and_no_page_action()
    {
        // Story 1288: one client runs both now, so the page's actions must not catch a call that is
        // not theirs. Up to 1.3.0 the middleware handed such a call back to the outer loop untouched.
        var rig = new Rig();
        var highlighted = false;
        var cancelled = 0;
        rig.Page.RegisterUIAction("highlight_row", "d", _ => highlighted = true);
        CallThenReply(rig.Inner, "cancel_order", new { orderId = 7 });

        await rig.SendAsync(new ChatOptions
        {
            Tools = [AIFunctionFactory.Create((int orderId) => { cancelled++; return "cancelled"; }, name: "cancel_order")],
        });

        cancelled.Should().Be(1, "the business tool ran, once");
        highlighted.Should().BeFalse("no page action was run for a call to another tool");
    }

    [Fact]
    public async Task An_action_the_page_no_longer_offers_is_not_executed()
    {
        // BUG-019. The page was disposed between the request and the response — a user navigating
        // while the model thinks, which takes seconds. The registration was matched against the
        // snapshot taken *before* the call, so the middleware ran a handler closing over a
        // component that no longer exists. Card buttons of kind UIAction already resolve at click
        // time for exactly this reason; the model's own path did not.
        var rig = new Rig();
        var ran = false;
        rig.Page.RegisterUIAction("highlight_row", "d", _ => ran = true);

        // Clear on the UI thread while the model is thinking — exactly what Dispose() does.
        rig.Inner.BeforeRespond = () => { rig.Page.Clear(); return Task.CompletedTask; };
        CallThenReply(rig.Inner, "highlight_row", reply: "That page is closed now.");

        await rig.SendAsync();

        ran.Should().BeFalse("the action belonged to a page that is gone");
        rig.Logs.HasWarning("went away").Should().BeTrue(rig.Logs.WarningText);
    }

    [Fact]
    public async Task A_skipped_stale_action_still_answers_the_model()
    {
        // Skipping must not mean silence: the model was legitimately offered the tool, so it needs
        // a result for the call it made. Leaving the call unanswered strands the conversation with
        // a function call that has no output.
        var rig = new Rig();
        rig.Page.RegisterUIAction("highlight_row", "d", _ => { });
        rig.Inner.BeforeRespond = () => { rig.Page.Clear(); return Task.CompletedTask; };
        CallThenReply(rig.Inner, "highlight_row", reply: "That page is closed now.");

        var response = await rig.SendAsync();

        ToolResultsOf(rig).Should().Contain("no longer open");
        response.Text.Should().Be("That page is closed now.");
    }

    [Fact]
    public async Task A_page_that_re_registers_an_action_gets_its_new_handler()
    {
        // The same rule in miniature. A Blazor re-render re-registers every action, replacing the
        // delegate; the snapshot still holds the previous one, which closes over the previous
        // render's state.
        var rig = new Rig();
        var ran = "";
        rig.Page.RegisterUIAction("highlight_row", "d", _ => ran = "old");
        rig.Inner.BeforeRespond = () =>
        {
            rig.Page.RegisterUIAction("highlight_row", "d", _ => ran = "new");
            return Task.CompletedTask;
        };
        CallThenReply(rig.Inner, "highlight_row");

        await rig.SendAsync();

        ran.Should().Be("new", "the live registration wins over the one the tools were built from");
    }

    // ── The loop guard ──────────────────────────────────────────────────────────

    [Fact]
    public async Task A_model_that_keeps_calling_the_same_action_is_stopped()
    {
        // Without a cap a model stuck in a loop would drive UI actions against the page forever, one
        // paid call at a time. Up to 1.3.0 the page's own loop stopped at ten. From story 1288 the
        // actions are tools of the run, and the cap is the run's, the same as for every other action:
        // MaximumIterationsPerRequest of the agent's function-invoking client, which MentorAgent does
        // not set — Microsoft.Extensions.AI's default of 40, then a last request with no tool.
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false);
        var runs = 0;
        host.Get<IMentorPageContext>().RegisterUIAction("highlight_row", "d", _ => runs++);
        host.Chat!.Respond((_, _) => new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent(Guid.NewGuid().ToString("N"), "highlight_row",
                                     new Dictionary<string, object?>())])));

        await host.RunTurnAsync("highlight the row");

        runs.Should().Be(MaxRunsPerTurn, "the run's cap is the ceiling, as for the application's own actions");
        host.Chat.Calls.Last().ToolNames.Should().NotContain("highlight_row", "the last request offers no tool");
    }

    /// <summary>
    /// The runs of one action a turn allows: <c>FunctionInvokingChatClient.MaximumIterationsPerRequest</c>, 40 by
    /// default, and then one more request that offers no tool, so the model has to answer.
    /// </summary>
    private const int MaxRunsPerTurn = 40;

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static AIFunction Existing(string name)
        => AIFunctionFactory.Create(() => "", name: name, description: "An existing business tool");

    /// <summary>The injected tool's description, exactly as the model was shown it.</summary>
    private static string? DescriptionOf(Rig rig, string toolName)
        => rig.Inner.Calls[0].Tool(toolName)?.Description;

    /// <summary>Every tool result the run fed back into the conversation, concatenated.</summary>
    private static string ToolResultsOf(Rig rig)
        => string.Join("\n", rig.Inner.Calls
            .SelectMany(c => c.Messages)
            .Where(m => m.Role == ChatRole.Tool)
            .SelectMany(m => m.Contents)
            .OfType<FunctionResultContent>()
            .Select(r => r.Result?.ToString() ?? ""));

    private sealed class OrderFilter
    {
        public int CustomerId { get; set; }
        public bool OnlyOpen { get; set; }
    }
}
