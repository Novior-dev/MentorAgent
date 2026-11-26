using FluentAssertions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Middleware;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.AI;
using Xunit;

namespace Novior.MentorAgent.Tests.Pages;

/// <summary>
/// Streaming granularity with the page's UI actions among the run's tools (BUG-052).
///
/// <para>
/// <c>UIActionToolTests</c> drives the same run 24 times and asserts on the text it
/// produces. That is why registering one UI action could switch token streaming off for the whole
/// page without a single test noticing: the text was right, it simply all arrived at once. The
/// property this file adds is <b>how many updates it arrived in</b>.
/// </para>
///
/// <para>
/// It matters because the two features are documented independently and recommended together.
/// A page registers UI actions so the assistant can drive its controls; the same page streams the
/// reply token by token. Buffering the response to look for UI-action calls turns a 20-second
/// reasoning turn into 20 seconds of an unchanging typing indicator followed by the whole answer —
/// and nothing tells the developer that calling <c>RegisterUIAction</c> did that.
/// </para>
/// </summary>
public sealed class UIActionStreamingTests
{
    private sealed class Rig
    {
        internal MentorPageContext Page { get; } = new();
        internal FakeChatClient Inner { get; } = new();
        internal RecordingStateService State { get; } = new();
        internal TestLogCollector Logs { get; } = new();

        /// <summary>
        /// Story 1288 — the run as the agent makes it: the page's actions are tools of the request, called by a
        /// function-invoking client over the middleware. Up to 1.3.1 the middleware ran them in a loop of its own.
        /// </summary>
        private (IChatClient Client, ChatOptions Options) Run()
        {
            var logger = new TypedLogger<UIActionsMiddleware>(new TestLoggerProvider(Logs).CreateLogger("ui"));
            var tools = new UIActionTools(Page, State, logger);
            return (new FunctionInvokingChatClient(new UIActionsMiddleware(Inner, Page, State, logger)),
                    new ChatOptions { Tools = tools.Current(null) });
        }

        /// <summary>Streams one request of the run and returns every update's text, in order.</summary>
        internal async Task<List<string>> StreamAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var updates = new List<string>();
            var (client, options) = Run();

            await foreach (var update in client.GetStreamingResponseAsync(
                               [new ChatMessage(ChatRole.User, "do the thing")], options, deadline.Token))
                updates.Add(update.Text);

            return updates;
        }
    }

    /// <summary>Long enough that a real stream is unmistakably more than one update.</summary>
    private const string Reply =
        "Ecco l'elenco completo dei prodotti disponibili a catalogo con prezzo e giacenza attuale.";

    [Fact]
    public async Task With_no_UI_actions_the_reply_streams_in_many_updates()
    {
        // The control. If this ever collapses too, the finding is about the fake or the rig rather
        // than about the middleware, and the next test proves nothing.
        var rig = new Rig();
        rig.Inner.Say(Reply);

        var updates = await rig.StreamAsync();

        updates.Count.Should().BeGreaterThan(5,
            "with no UI actions nothing of the page is in the way and the provider's deltas reach the caller");
        string.Concat(updates).Should().Contain("catalogo");
    }

    [Fact]
    public async Task Registering_a_UI_action_does_not_switch_streaming_off()
    {
        // One registered action, and a reply that never calls it — the ordinary case on any page
        // that offers the assistant a control. This is the assertion that failed before the fix:
        // the whole reply arrived as a single update.
        var rig = new Rig();
        rig.Page.RegisterUIAction("highlight_order", "Evidenzia un ordine", _ => { });
        rig.Inner.Say(Reply);

        var updates = await rig.StreamAsync();

        updates.Count.Should().BeGreaterThan(5,
            "a page that registers a UI action still streams: the user must not sit in front of an " +
            "unchanging typing indicator for the whole turn and then receive the answer at once");
        string.Concat(updates).Should().Contain("catalogo");
    }

    [Fact]
    public async Task The_answer_after_a_UI_action_call_streams_too()
    {
        // The model calls the action, the run executes it, and the answer that follows must stream
        // like any other.
        var invoked = 0;
        var rig = new Rig();
        rig.Page.RegisterUIAction("highlight_order", "Evidenzia un ordine", _ => invoked++);
        rig.Inner.CallTool("highlight_order", new { parameter = 42 });
        rig.Inner.Say(Reply);

        var updates = await rig.StreamAsync();

        invoked.Should().Be(1, "the run's function-invoking client executes the action, once");
        updates.Count.Should().BeGreaterThan(5, "the reply after the action is an ordinary streamed reply");
        string.Concat(updates).Should().Contain("catalogo");
    }

    [Fact]
    public async Task No_UI_action_call_ever_reaches_the_transports()
    {
        // The guarantee the buffering existed to provide, kept where it matters. On the server
        // transports a call reaching the browser would be a tool call nobody can answer. From story
        // 1288 the call is a call of the run, like every other action, so the guarantee is the
        // orchestrator's: the hub and SSE forward what reaches the state service — text, the action's
        // started and finished, cards — and never a call.
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false, withRecorder: true);
        host.Get<IMentorPageContext>().RegisterUIAction("highlight_order", "Evidenzia un ordine", _ => { });
        host.Chat!.CallTool("highlight_order", new { parameter = 42 }).Say(Reply);

        (await host.RunTurnAsync("do the thing")).Should().BeNull();

        host.Recorder!.Log.SelectMany(e => e.Args).OfType<FunctionCallContent>().Should().BeEmpty(
            "no UI-action call reaches the state service, which is all the transports forward");
        host.Recorder.StreamedText.Should().Contain("catalogo").And.NotContain("highlight_order");
        host.Recorder.Sequence.Should().Contain(nameof(RecordingStateService.NotifyUIActionCompleted));
    }
}
