using System.Text.Json;
using FluentAssertions;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Middleware;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Novior.MentorAgent.Tests.Pages;

/// <summary>
/// <see cref="UIActionsMiddleware"/> — the piece that turns the current page's registered actions
/// into named tools and runs their function-calling loop itself.
///
/// <para>
/// It has to run that loop internally because the Agent Framework resolves function calls against a
/// static registry built at startup, and a page's actions change on every navigation. So this
/// middleware injects tools the outer pipeline has never heard of, executes any calls to them, and
/// hands back a response that looks as though they were never offered. Every one of those steps is a
/// place where a call can be executed twice, executed against the wrong page, or silently dropped.
/// </para>
///
/// <para>
/// It is driven directly here rather than through a turn: the loop is the subject, and a full turn
/// would put the Agent Framework's own function-calling machinery between the assertion and the
/// behaviour.
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

        internal UIActionsMiddleware Middleware => new(
            Inner, Page, State,
            new TypedLogger<UIActionsMiddleware>(new TestLoggerProvider(Logs).CreateLogger("ui")));

        /// <summary>Runs one non-streaming call through the middleware.</summary>
        internal async Task<ChatResponse> SendAsync(ChatOptions? options = null)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            return await Middleware.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "do the thing")], options, deadline.Token);
        }

        /// <summary>Runs one streamed call through the middleware and returns the text.</summary>
        internal async Task<string> StreamAsync(ChatOptions? options = null)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var text = new System.Text.StringBuilder();

            await foreach (var update in Middleware.GetStreamingResponseAsync(
                               [new ChatMessage(ChatRole.User, "do the thing")], options, deadline.Token))
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
        // The pass-through branch: with no actions registered the middleware is not in the way at
        // all. Kept as the control for the streaming assertions in UIActionStreamingTests.
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
        // an action. BUG-052 fixed that; this now pins the fix from the inner client's side.
        var rig = new Rig();
        rig.Page.RegisterUIAction("highlight_row", "Highlights a row", _ => { });
        rig.Inner.Say("hello there");

        await rig.StreamAsync();

        rig.Inner.Calls[0].Streaming.Should().BeTrue(
            "the UI-action loop runs over the streamed response now, not over a buffered one");
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
        // expecting the business tool. The business tool wins because it is the one the outer
        // pipeline can actually resolve.
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
        // The options are cloned to merge the tools in. A hand-written copy dropped ConversationId,
        // and under service-managed history that loses previous_response_id — the provider then
        // rejects the tool result with "HTTP 400 — No tool call found for function call output".
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
    public async Task A_call_to_a_business_tool_is_left_for_the_outer_pipeline()
    {
        // The middleware must only intercept names it injected. Swallowing a business tool call
        // would strand the outer function-calling loop waiting for a result it never gets.
        var rig = new Rig();
        rig.Page.RegisterUIAction("highlight_row", "d", _ => { });
        rig.Inner.CallTool("cancel_order", new { orderId = 7 });

        var response = await rig.SendAsync();

        response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>()
                .Select(c => c.Name).Should().ContainSingle().Which.Should().Be("cancel_order");
        rig.Inner.CallCount.Should().Be(1, "the middleware returned immediately, it did not loop");
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
        // Ten iterations, then out. Without the cap a model stuck in a loop would drive UI actions
        // against the page forever, one paid call at a time.
        var rig = new Rig();
        var runs = 0;
        rig.Page.RegisterUIAction("highlight_row", "d", _ => runs++);
        rig.Inner.Respond((_, _) => new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent(Guid.NewGuid().ToString("N"), "highlight_row",
                                     new Dictionary<string, object?>())])));

        await rig.SendAsync();

        runs.Should().Be(10, "MaxUIActionIterations is the documented ceiling");
        rig.Logs.HasWarning("max iterations").Should().BeTrue(rig.Logs.WarningText);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static AIFunction Existing(string name)
        => AIFunctionFactory.Create(() => "", name: name, description: "An existing business tool");

    /// <summary>The injected tool's description, exactly as the model was shown it.</summary>
    private static string? DescriptionOf(Rig rig, string toolName)
        => rig.Inner.Calls[0].Tool(toolName)?.Description;

    /// <summary>Every tool result the middleware fed back into the conversation, concatenated.</summary>
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
