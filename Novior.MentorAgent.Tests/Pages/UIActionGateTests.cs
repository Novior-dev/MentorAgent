using FluentAssertions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Novior.MentorAgent.Tests.Pages;

/// <summary>
/// Story 1288 — a page's UI actions are tools of the turn and go through the gate like every other action: the
/// observers see their decisions, and the action policies and the before-action hook are asked about them. Before,
/// <c>UIActionsMiddleware</c> ran them in a loop of its own, below the agent, where the gate never saw them.
/// </summary>
[Trait("Rule", "page.gated")]
public sealed class UIActionGateTests
{
    private static MentorHostFixture Host(RecordingActionObserver observer)
        => MentorHostFixture.Create(o => o.MemoryAutoCapture = false, s => s.AddMentorActionObserver(observer));

    private static Task FlushAsync(MentorHostFixture host) => host.Get<MentorActionDecisions>().FlushAsync();

    private static MentorHostFixture Host(RecordingActionObserver observer, Action<MentorOptions> configure,
        TestActionPolicy? policy = null, bool withRecorder = false)
        => MentorHostFixture.Create(o =>
        {
            o.MemoryAutoCapture = false;
            configure(o);
        }, s =>
        {
            s.AddMentorActionObserver(observer);
            if (policy is not null) s.AddMentorActionPolicy(policy);
        }, withRecorder: withRecorder);

    [Theory]
    [InlineData(MentorHitlMode.Blocking)]
    [InlineData(MentorHitlMode.Native)]
    public async Task A_UI_action_asks_no_confirmation_and_no_role_of_its_own(MentorHitlMode mode)
    {
        // Only the way changes: an action that ran without a question and for anyone still does, with writes confirmed
        // by default and nobody signed in.
        var observer = new RecordingActionObserver();
        await using var host = Host(observer, o => { o.HitlMode = mode; o.RequireConfirmationForWrites = true; });
        var banners = 0;
        host.Get<IMentorStateService>().OnConfirmationRequired += _ => banners++;
        var invoked = 0;
        host.Get<IMentorPageContext>().RegisterUIAction("delete_filter", "Removes the grid's filter", _ => invoked++);
        host.Chat!.CallTool("delete_filter").Say("Removed.");

        (await host.RunTurnAsync("remove the filter")).Should().BeNull();
        await FlushAsync(host);

        invoked.Should().Be(1);
        banners.Should().Be(0, "mode {0}: a UI action asks for no confirmation of its own", mode);
        observer.Steps("delete_filter").Should().Equal(["Allowed/allowed", "Executed/completed"], "mode {0}", mode);
        observer.Seen.Single(d => d.ToolName == "delete_filter" && d.Outcome == MentorActionOutcome.Executed)
            .DisplayName.Should().Be("Removes the grid's filter", "the description is the text shown");
    }

    [Fact]
    public async Task A_UI_action_reports_its_own_start_and_end_and_not_the_gates()
    {
        // The widget shows a UI action as before: its own started and finished, not a second line of the gate's.
        var observer = new RecordingActionObserver();
        await using var host = Host(observer, _ => { }, withRecorder: true);
        host.Get<IMentorPageContext>().RegisterUIAction("highlight_order", "Highlights an order", _ => { });
        host.Chat!.CallTool("highlight_order", new { parameter = 42 }).Say("Highlighted.");

        (await host.RunTurnAsync("highlight order 42")).Should().BeNull();

        host.Recorder!.Sequence.Where(e => e.Contains("Action", StringComparison.Ordinal)).Should().Equal(
            nameof(RecordingStateService.NotifyUIActionExecuting), nameof(RecordingStateService.NotifyUIActionCompleted));
    }

    [Theory]
    [InlineData(MentorHitlMode.Blocking)]
    [InlineData(MentorHitlMode.Native)]
    public async Task A_policy_can_ask_the_user_to_confirm_a_UI_action(MentorHitlMode mode)
    {
        var observer = new RecordingActionObserver();
        var policy = new TestActionPolicy
        {
            Verdict = r => r.ToolName == "highlight_order" ? MentorActionVerdict.Confirm("ask first") : MentorActionVerdict.Proceed,
        };
        await using var host = Host(observer, o => o.HitlMode = mode, policy);
        var state = host.Get<IMentorStateService>();
        state.OnConfirmationRequired += req => _ = state.ConfirmAsync(req.ActionId);
        var invoked = 0;
        host.Get<IMentorPageContext>().RegisterUIAction("highlight_order", "Highlights an order", _ => invoked++);
        host.Chat!.CallTool("highlight_order", new { parameter = 42 }).Say("Highlighted.");

        (await host.RunTurnAsync("highlight order 42")).Should().BeNull();
        await FlushAsync(host);

        invoked.Should().Be(1, "mode {0}", mode);
        observer.Steps("highlight_order").Should().Equal(
            ["Allowed/allowed", "ConfirmationRequested/policy", "Confirmed/operator", "Executed/completed"], "mode {0}", mode);
    }

    [Fact]
    public async Task A_UI_action_a_policy_stops_is_not_run()
    {
        var observer = new RecordingActionObserver();
        var policy = new TestActionPolicy
        {
            Verdict = r => r.ToolName == "highlight_order" ? MentorActionVerdict.Stop("not on this page") : MentorActionVerdict.Proceed,
        };
        await using var host = Host(observer, _ => { }, policy, withRecorder: true);
        var invoked = 0;
        host.Get<IMentorPageContext>().RegisterUIAction("highlight_order", "Highlights an order", _ => invoked++);
        host.Chat!.CallTool("highlight_order", new { parameter = 42 }).Say("I could not.");

        (await host.RunTurnAsync("highlight order 42")).Should().BeNull();
        await FlushAsync(host);

        invoked.Should().Be(0, "the handler never ran");
        host.Recorder!.Sequence.Should().NotContain(nameof(RecordingStateService.NotifyUIActionExecuting));
        observer.Steps("highlight_order").Should().ContainSingle().Which.Should().EndWith("/policy",
            "the policy's refusal is the only decision: nothing was executed");
    }

    [Fact]
    public async Task An_action_of_the_page_goes_through_the_gate()
    {
        var observer = new RecordingActionObserver();
        await using var host = Host(observer);
        var invoked = 0;
        host.Get<IMentorPageContext>().SetPageName("Orders").RegisterUIAction("highlight_order", "Highlights an order", _ => invoked++);
        host.Chat!.CallTool("highlight_order", new { parameter = 42 }).Say("Highlighted.");

        (await host.RunTurnAsync("highlight order 42")).Should().BeNull();
        await FlushAsync(host);

        invoked.Should().Be(1);
        observer.Steps("highlight_order").Should().Equal(["Allowed/allowed", "Executed/completed"],
            "a UI action is an action of the coordinator, decided by the gate like the others");
    }

    [Fact]
    public async Task An_action_of_the_page_navigate_to_opened_goes_through_the_gate_in_the_same_turn()
    {
        // The case the tools of the run have to follow: the new page registers its actions while navigate_to waits for
        // its SignalReady, and the model calls one of them in the next round trip of the same turn.
        var observer = new RecordingActionObserver();
        await using var host = Host(observer);
        var page = host.Get<IMentorPageContext>();
        var pages = host.Get<MentorDiscoveryService>().Discover().Pages;
        var orders = pages.FindIndex(p => p.Url == "/orders");
        pages[orders] = pages[orders] with { ReadyTimeout = 30_000 };

        page.SetPageName("Home").RegisterUIAction("show_tips", "Shows the tips", _ => { });
        var invoked = 0;
        host.Get<IMentorStateService>().OnNavigationRequested += url => _ = Task.Run(async () =>
        {
            // As Blazor does it: the new page initializes, then the one it replaces is disposed.
            await Task.Delay(100);
            page.SetPageName("Orders").RegisterUIAction("highlight_order", "Highlights an order", _ => invoked++);
            page.SignalReady();
            page.Clear("Home");
        });
        host.Chat!.CallTool("navigate_to", new { url = "/orders" })
            .CallTool("highlight_order", new { parameter = 7 })
            .Say("Here is order 7.");

        (await host.RunTurnAsync("show me order 7 on the orders page")).Should().BeNull();
        await FlushAsync(host);

        invoked.Should().Be(1, "the new page's action was offered and run after the navigation");
        host.Chat!.Calls[1].ToolNames.Should().Contain("highlight_order").And.NotContain("show_tips",
            "the request after the navigation offers the actions of the page now shown");
        observer.Steps("highlight_order").Should().Equal(["Allowed/allowed", "Executed/completed"],
            "an action added during the run is gated like one the run started with");
    }

    // ── BUG-241: a handler that throws ───────────────────────────────────────────

    /// <summary>The kind of text an exception carries: a user's e-mail address.</summary>
    private const string Secret = "the grid of mario.rossi@example.com is gone";

    /// <summary>The results of the tools the model was given, in every request it received.</summary>
    private static string ToolResults(FakeChatClient chat) => string.Join("\n", chat.Calls
        .SelectMany(c => c.Messages).SelectMany(m => m.Contents).OfType<FunctionResultContent>()
        .Select(r => r.Result?.ToString() ?? ""));

    [Fact]
    public async Task A_UI_action_whose_handler_throws_failed_with_the_type_of_its_exception()
    {
        // Up to 1.4.0 the observers were told the action was executed: its tool answered the error as a result (BUG-241).
        var observer = new RecordingActionObserver();
        await using var host = Host(observer, _ => { }, withRecorder: true);
        host.Get<IMentorPageContext>().RegisterUIActionAsync("highlight_order", "Highlights an order",
            _ => throw new InvalidOperationException(Secret));
        host.Chat!.CallTool("highlight_order", new { parameter = 42 }).Say("I could not highlight it.");

        (await host.RunTurnAsync("highlight order 42")).Should().BeNull();
        await FlushAsync(host);

        observer.Steps("highlight_order").Should().Equal(["Allowed/allowed", "Failed/exception"],
            "the handler threw: the action was not executed");
        var failed = observer.Seen.Single(d => d.ToolName == "highlight_order" && d.Outcome == MentorActionOutcome.Failed);
        failed.ExceptionType.Should().Be("System.InvalidOperationException", "the type of the handler's own exception");
        ToolResults(host.Chat!).Should().Contain(Secret, "without OnException the model gets the handler's message, as before");
        host.Recorder!.Sequence.Where(e => e.Contains("Action", StringComparison.Ordinal)).Should().Equal(
            [nameof(RecordingStateService.NotifyUIActionExecuting)],
            "the widget shows the action's start and no end, as before, and no failed line of the gate's");
    }

    [Fact]
    public async Task With_OnException_set_a_UI_action_that_throws_gives_the_model_the_apps_message()
    {
        var observer = new RecordingActionObserver();
        await using var host = Host(observer, o => o.OnException = _ => "The grid is not available.");
        host.Get<IMentorPageContext>().RegisterUIAction("highlight_order", "Highlights an order",
            _ => throw new InvalidOperationException(Secret));
        host.Chat!.CallTool("highlight_order", new { parameter = 42 }).Say("I could not highlight it.");

        (await host.RunTurnAsync("highlight order 42")).Should().BeNull();
        await FlushAsync(host);

        observer.Steps("highlight_order").Should().Equal(["Allowed/allowed", "Failed/exception"]);
        ToolResults(host.Chat!).Should().Contain("The grid is not available.").And.NotContain(Secret,
            "the hook says what of an exception reaches the model, for a UI action as for any other");
    }

    [Fact]
    public async Task Under_a_pre_built_agent_invoke_ui_action_failed_when_the_handler_threw()
    {
        // Path B: the page's actions are reached through the invoke_ui_action dispatcher, which answered the error too.
        var observer = new RecordingActionObserver();
        var inner = new FakeChatClient();
        await using var host = MentorHostFixture.Create(o =>
        {
            o.MemoryAutoCapture = false;
            o.ChatClient = null;
            o.Agent = inner.AsAIAgent(instructions: "You are a test agent.");
        }, s => s.AddMentorActionObserver(observer), withChatClient: false);
        host.Get<IMentorPageContext>().RegisterUIAction("highlight_row", "Highlights a row",
            _ => throw new InvalidOperationException(Secret));
        inner.CallTool("invoke_ui_action", new { actionName = "highlight_row" }).Say("I could not highlight it.");

        await host.RunTurnAsync("highlight row 5");
        await FlushAsync(host);

        observer.Steps("invoke_ui_action").Should().Equal(["Allowed/allowed", "Failed/exception"]);
        observer.Seen.Single(d => d.Outcome == MentorActionOutcome.Failed).ExceptionType
            .Should().Be("System.InvalidOperationException");
        ToolResults(inner).Should().Contain(Secret);
    }
}
