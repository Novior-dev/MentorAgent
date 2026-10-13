using FluentAssertions;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Middleware;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.AI;
using Xunit;

namespace Novior.MentorAgent.Tests.Pages;

/// <summary>
/// Streaming granularity through <see cref="UIActionsMiddleware"/> (BUG-052).
///
/// <para>
/// <c>UIActionToolTests</c> drives the same middleware 24 times and asserts on the text it
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

        internal UIActionsMiddleware Middleware => new(
            Inner, Page, State,
            new TypedLogger<UIActionsMiddleware>(new TestLoggerProvider(Logs).CreateLogger("ui")));

        /// <summary>Streams one call and returns every update's text, in order.</summary>
        internal async Task<List<string>> StreamAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var updates = new List<string>();

            await foreach (var update in Middleware.GetStreamingResponseAsync(
                               [new ChatMessage(ChatRole.User, "do the thing")], null, deadline.Token))
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
            "with no UI actions the middleware is a pass-through and the provider's deltas reach the caller");
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
        // The loop's own path: the model calls the action, the middleware executes it internally,
        // and the answer that follows must stream like any other.
        var invoked = 0;
        var rig = new Rig();
        rig.Page.RegisterUIAction("highlight_order", "Evidenzia un ordine", _ => invoked++);
        rig.Inner.CallTool("highlight_order", new { parameter = 42 });
        rig.Inner.Say(Reply);

        var updates = await rig.StreamAsync();

        invoked.Should().Be(1, "the middleware executes the action itself rather than offering it outward");
        updates.Count.Should().BeGreaterThan(5, "the reply after the action is an ordinary streamed reply");
        string.Concat(updates).Should().Contain("catalogo");
    }

    [Fact]
    public async Task No_UI_action_call_ever_reaches_the_caller()
    {
        // The guarantee the buffering existed to provide, kept. The outer pipeline has never heard
        // of these tools, so a function call for one leaking out would be unresolvable — and on the
        // server transports it would reach the browser as a tool call nobody can answer.
        var rig = new Rig();
        rig.Page.RegisterUIAction("highlight_order", "Evidenzia un ordine", _ => { });
        rig.Inner.CallTool("highlight_order", new { parameter = 42 });
        rig.Inner.Say(Reply);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var leaked = new List<string>();

        await foreach (var update in rig.Middleware.GetStreamingResponseAsync(
                           [new ChatMessage(ChatRole.User, "do the thing")], null, deadline.Token))
            leaked.AddRange(update.Contents.OfType<FunctionCallContent>().Select(f => f.Name));

        leaked.Should().BeEmpty("UI-action calls are the middleware's own business");
    }
}
