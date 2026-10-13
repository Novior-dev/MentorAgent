using FluentAssertions;
using Novior.MentorAgent.Tests.Harness;
using Xunit;

namespace Novior.MentorAgent.Tests.Scenarios;

/// <summary>
/// Part C2.5 — the turn as a state machine, walked at each phase.
///
/// <para>
/// Ordering defects are invisible to every value-combination approach: the pairwise sweep boots a
/// configuration and drives one clean turn, which is exactly the case that works. What breaks is a
/// second thing arriving while the first is still in flight — Stop mid-stream, a reset mid-turn, a
/// second message before the first finished, an approval raised while the model is streaming.
/// </para>
///
/// <para>
/// What each phase is <b>told</b> matters as much as what it does. A turn that ends without
/// producing a reply must clear the busy flag, or the widget's spinner runs forever and Stop is the
/// only way out — and Stop on a turn that already ended is its own edge.
/// </para>
/// </summary>
public sealed class TurnLifecycleTests
{
    /// <summary>Every message the user was shown, in order.</summary>
    private static IReadOnlyList<string> ErrorsShown(MentorHostFixture host)
        => [.. host.Recorder!.Log
            .Where(e => e.Event == "NotifyError")
            .Select(e => e.Args.FirstOrDefault()?.ToString() ?? "")];

    private static IReadOnlyList<bool> BusyStates(MentorHostFixture host)
        => [.. host.Recorder!.Log
            .Where(e => e.Event == "NotifyBusyChanged")
            .Select(e => e.Args.FirstOrDefault() is true)];

    // ── A turn that cannot start ────────────────────────────────────────────────

    [Fact]
    public async Task A_coordinator_that_cannot_be_built_still_tells_the_user_something()
    {
        // Worth pinning precisely, because BUG-011 and BUG-028 were both written up as producing
        // *silence*. They do not: the failure path calls NotifyError, so the widget shows a message.
        // The defect in both cases is that the message names neither the cause nor anything the
        // reader can act on — not that it is absent. A test is the only way to keep that distinction
        // honest, since the fixture's RunTurnAsync only surfaces thrown exceptions and would report
        // "no error" either way.
        await using var host = MentorHostFixture.Create(withRecorder: true,
            configure: o => o.MaxSessionMessages = -1);   // guarded now; kept as the canary

        host.Chat!.Say("ok");
        await host.RunTurnAsync("hello");

        // With the BUG-028 guard in place this configuration works, so the user sees no error at
        // all — which is the point of the fix.
        ErrorsShown(host).Should().BeEmpty();
        host.Chat!.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task A_message_past_the_length_limit_is_refused_and_the_spinner_stops()
    {
        // Every early return in SendMessageAsync has to clear busy on the way out. One that forgets
        // leaves the widget spinning with no request in flight and no way to recover but Stop.
        await using var host = MentorHostFixture.Create(withRecorder: true,
            configure: o => o.MaxMessageLength = 10);

        host.Chat!.Say("ok");
        await host.RunTurnAsync(new string('a', 50));

        ErrorsShown(host).Should().ContainSingle().Which.Should().NotBeEmpty();
        BusyStates(host).Should().NotBeEmpty();
        BusyStates(host)[^1].Should().BeFalse("the turn ended, so the widget must stop spinning");
    }

    [Fact]
    public async Task A_rate_limited_message_is_refused_and_the_spinner_stops()
    {
        await using var host = MentorHostFixture.Create(withRecorder: true, configure: o =>
        {
            o.RateLimitPerUser = 1;
            o.RateLimitWindowSecs = 60;
        });

        host.Chat!.Say("ok");
        await host.RunTurnAsync("first");
        host.Recorder!.Reset();

        await host.RunTurnAsync("second");

        ErrorsShown(host).Should().ContainSingle();
        BusyStates(host)[^1].Should().BeFalse();
    }

    [Fact]
    public async Task A_blocked_message_is_refused_and_the_spinner_stops()
    {
        await using var host = MentorHostFixture.Create(withRecorder: true,
            configure: o => o.InputGuardrail = (_, _) => Task.FromResult(false));

        host.Chat!.Say("ok");
        await host.RunTurnAsync("something forbidden");

        ErrorsShown(host).Should().ContainSingle();
        BusyStates(host)[^1].Should().BeFalse();
        host.Chat!.CallCount.Should().Be(0);
    }

    // ── Stop, at each phase ─────────────────────────────────────────────────────

    [Fact]
    public async Task Stop_before_anything_is_running_is_a_no_op()
    {
        // The button is enabled whenever the widget thinks it is busy, and "thinks" can be stale
        // after a reconnect. Throwing here would take down a click handler.
        await using var host = MentorHostFixture.Create(withRecorder: true);

        var act = () => host.Orchestrator.CancelCurrentRequest();

        act.Should().NotThrow();
        host.Recorder!.Log.Should().BeEmpty("there was nothing to cancel");
    }

    [Fact]
    public async Task Stop_during_a_turn_ends_it_and_clears_busy()
    {
        // The documented Stop semantics: the partial reply stays on screen and the turn ends.
        await using var host = MentorHostFixture.Create(withRecorder: true);

        host.Chat!.BeforeRespond = () =>
        {
            host.Orchestrator.CancelCurrentRequest();
            return Task.CompletedTask;
        };
        host.Chat.Say("this should never finish");

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await host.RunTurnAsync("start something long", deadline.Token);

        BusyStates(host).Should().NotBeEmpty();
        BusyStates(host)[^1].Should().BeFalse("Stop must leave the widget idle, not spinning");
    }

    [Fact]
    public async Task A_turn_after_a_stopped_one_works_normally()
    {
        // The state machine has to return to idle. A cancellation token left in place would make
        // every later turn cancel immediately — and look exactly like a broken assistant.
        await using var host = MentorHostFixture.Create(withRecorder: true);

        host.Chat!.BeforeRespond = () =>
        {
            host.Orchestrator.CancelCurrentRequest();
            host.Chat!.BeforeRespond = null;   // only the first turn is interrupted
            return Task.CompletedTask;
        };
        host.Chat.Say("interrupted").Say("second answer");

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await host.RunTurnAsync("first", deadline.Token);
        await host.RunTurnAsync("second", deadline.Token);

        host.Chat!.CallCount.Should().BeGreaterThan(1, "the second turn must reach the model");
    }

    // ── Reset, at each phase ────────────────────────────────────────────────────

    [Fact]
    public async Task A_reset_between_turns_starts_a_new_conversation_and_the_next_turn_works()
    {
        await using var host = MentorHostFixture.Create(withRecorder: true);
        host.Chat!.Say("first").Say("second");

        await host.RunTurnAsync("hello");
        await host.Orchestrator.ResetSessionAsync();
        var error = await host.RunTurnAsync("hello again");

        error.Should().BeNull();
        host.Chat!.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task A_reset_before_the_first_message_is_harmless()
    {
        // The reset button is enabled from the moment the widget opens.
        await using var host = MentorHostFixture.Create(withRecorder: true);
        host.Chat!.Say("ok");

        var act = async () => await host.Orchestrator.ResetSessionAsync();
        await act.Should().NotThrowAsync();

        (await host.RunTurnAsync("hello")).Should().BeNull();
    }

    [Fact]
    public async Task A_reset_is_logged_once()
    {
        // A reset used to leave no trace in the host's log, on any transport — so "the assistant forgot
        // what I told it" could not be told apart from a user who had pressed New conversation.
        await using var host = MentorHostFixture.Create();
        host.Chat!.SayRepeatedly("ok", 2);
        await host.RunTurnAsync("hello");

        await host.Orchestrator.ResetSessionAsync();

        host.Logs.Records
            .Where(r => r.Level == Microsoft.Extensions.Logging.LogLevel.Information
                        && r.Message.Contains("New conversation", StringComparison.Ordinal))
            .Should().ContainSingle("one line per reset, with no ids and no content");
    }

    // ── Two messages in flight ──────────────────────────────────────────────────

    [Fact]
    public async Task A_second_message_sent_while_the_first_is_running_does_not_corrupt_either()
    {
        // The widget disables the composer while busy, but a custom client, the hub, or a fast
        // double-tap can still get two in. Neither may hang, and the session must survive both.
        await using var host = MentorHostFixture.Create(withRecorder: true);
        host.Chat!.Say("a").Say("b");

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var first = host.Orchestrator.SendMessageAsync("one", deadline.Token);
        var second = host.Orchestrator.SendMessageAsync("two", deadline.Token);

        var act = async () => await Task.WhenAll(first, second);

        await act.Should().NotThrowAsync();
        BusyStates(host)[^1].Should().BeFalse("both turns finished, so the widget must be idle");
    }

    // ── The busy flag is the widget's whole notion of state ─────────────────────

    [Fact]
    public async Task Every_turn_raises_busy_and_lowers_it_again()
    {
        // The invariant behind every test above, stated once: whatever happens in between, a turn
        // starts busy and ends idle. The widget has no other way to know.
        await using var host = MentorHostFixture.Create(withRecorder: true);
        host.Chat!.Say("ok");

        await host.RunTurnAsync("hello");

        var busy = BusyStates(host);
        busy.Should().NotBeEmpty();
        busy[0].Should().BeTrue("the turn starts by saying it is working");
        busy[^1].Should().BeFalse("and ends by saying it is not");
    }

    [Fact]
    public async Task The_composer_is_handed_back_before_the_post_turn_fact_extraction()
    {
        // Measured live on rc.4: the answer is complete on screen and the turn stays busy for the
        // 544–767 ms of a second model call that extracts durable facts. That call is bookkeeping —
        // best-effort by design, and nothing the user is waiting for. Every client that gates its
        // composer on BusyChanged (the widget, and the React/Angular/Vue/WASM clients that receive
        // the event over the wire) was made to wait for it.
        //
        // Asserted on the order of events rather than on a clock: when the extraction call reaches
        // the model, the widget has already been told the turn is over.
        var busyWhenCalled = new List<bool?>();

        await using var host = MentorHostFixture.Create(
            o => { o.UseMemoryContext = true; o.MemoryAutoCapture = true; },
            withRecorder: true);

        host.Chat!.BeforeRespond = () =>
        {
            var states = BusyStates(host);
            busyWhenCalled.Add(states.Count == 0 ? null : states[^1]);
            return Task.CompletedTask;
        };
        host.Chat.Say("Ecco la risposta.").Say("NONE");

        await host.RunTurnAsync("il mio corriere preferito è BRT");

        busyWhenCalled.Should().HaveCountGreaterThan(1,
            "the turn is one coordinator call plus the extraction call");
        busyWhenCalled[0].Should().Be(true, "the coordinator runs while the turn is live");
        busyWhenCalled[^1].Should().Be(false,
            "by the extraction call the user can already type the next message");
        BusyStates(host)[^1].Should().BeFalse("and the turn still ends idle");
    }

    [Fact]
    public async Task The_facts_are_still_extracted_after_the_release()
    {
        // The control for the test above: handing the composer back early must not turn the
        // extraction into dead code.
        await using var host = MentorHostFixture.Create(
            o => { o.UseMemoryContext = true; o.MemoryAutoCapture = true; });

        host.Chat!.Say("Ecco la risposta.")
                  .Respond((_, _) => new Microsoft.Extensions.AI.ChatResponse(
                      new Microsoft.Extensions.AI.ChatMessage(
                          Microsoft.Extensions.AI.ChatRole.Assistant,
                          """{"facts":[{"key":"preferred_courier","value":"BRT"}]}""")));

        await host.RunTurnAsync("il mio corriere preferito è BRT");

        var store = host.Get<Novior.MentorAgent.Memory.IMentorMemoryStore>();
        var me = await host.Get<Novior.MentorAgent.Memory.MentorMemoryService>().GetUserIdAsync();
        var saved = await store.GetRecentAsync(me, 10);
        string.Join(";", saved.Select(s => $"{s.Key}={s.Value}"))
            .Should().Contain("BRT", "the extraction still runs, it just no longer blocks the composer");
    }

    // ── A provider that fails inside the stream (BUG-117) ──────────────────────────

    /// <summary>
    /// What Azure OpenAI's Responses API sent on a 429 in the rc.14 batteries: no exception, one
    /// <see cref="Microsoft.Extensions.AI.ErrorContent"/> in the stream, no text.
    /// </summary>
    private static Microsoft.Extensions.AI.ChatResponse RateLimitedInTheStream()
        => new(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant,
            [new Microsoft.Extensions.AI.ErrorContent(
                "Your requests to gpt-4.1 for gpt-4.1 in eastus have exceeded token rate limit.")
            { ErrorCode = "rate_limit_exceeded" }]));

    private static readonly Novior.MentorAgent.Abstractions.Core.MentorLocalizer En =
        new(Novior.MentorAgent.Abstractions.Models.MentorLanguage.English);

    [Fact]
    public async Task A_provider_error_inside_the_stream_is_shown_to_the_user()
    {
        // Found live on rc.14: the turn ended with nothing on screen — no reply, no error — because the
        // failure never became an exception. It must reach the user the way a thrown 429 does.
        await using var host = MentorHostFixture.Create(withRecorder: true);
        host.Chat!.Respond((_, _) => RateLimitedInTheStream());

        await host.RunTurnAsync("how many open orders are there?");

        ErrorsShown(host).Should().ContainSingle()
            .Which.Should().Be(En.Get("error.providerBusy"),
                "a throttled provider is one the user can act on — by trying again in a moment");
        BusyStates(host)[^1].Should().BeFalse("the turn ended, so the widget must stop spinning");
    }

    [Fact]
    public async Task A_provider_error_on_the_call_after_a_tool_is_shown_to_the_user()
    {
        // The shape measured live: the read ran, the call that was to write the reply was throttled.
        await using var host = MentorHostFixture.Create(withRecorder: true);
        host.Chat!.CallTool("count_open_orders").Respond((_, _) => RateLimitedInTheStream());

        await host.RunTurnAsync("how many open orders are there?");

        host.Get<Novior.MentorAgent.Tests.App.TestOrderService>().Invoked.Should().Contain(nameof(Novior.MentorAgent.Tests.App.TestOrderService.CountOpenOrders), "the read did run");
        ErrorsShown(host).Should().ContainSingle().Which.Should().Be(En.Get("error.providerBusy"));
    }

    [Fact]
    public async Task A_sentence_written_before_a_tool_does_not_hide_the_error_on_the_call_after_it()
    {
        // "I'll check the open orders." then the read, then a throttled call: the sentence is not the answer,
        // and the user must not be left with it alone (rc.14 review of BUG-117).
        await using var host = MentorHostFixture.Create(withRecorder: true);
        host.Chat!.Respond((_, _) => new Microsoft.Extensions.AI.ChatResponse(
                new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant,
                [
                    new Microsoft.Extensions.AI.TextContent("I'll check the open orders."),
                    new Microsoft.Extensions.AI.FunctionCallContent("call_1", "count_open_orders"),
                ])))
            .Respond((_, _) => RateLimitedInTheStream());

        await host.RunTurnAsync("how many open orders are there?");

        ErrorsShown(host).Should().ContainSingle().Which.Should().Be(En.Get("error.providerBusy"));
    }

    [Fact]
    public async Task A_refusal_in_the_stream_reads_as_a_block_not_as_a_provider_error()
    {
        await using var host = MentorHostFixture.Create(withRecorder: true);
        host.Chat!.Respond((_, _) => new Microsoft.Extensions.AI.ChatResponse(
            new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant,
                [new Microsoft.Extensions.AI.ErrorContent("I'm sorry, I can't help with that.") { ErrorCode = "Refusal" }])));

        await host.RunTurnAsync("something the model refuses");

        ErrorsShown(host).Should().ContainSingle().Which.Should().Be(En.Get("guardrail.blocked"));
        host.Logs.ExceptionText.Should().NotContain("ProviderStreamException");
    }

    [Fact]
    public async Task A_reply_written_before_a_provider_error_is_left_as_it_is()
    {
        // The control: a hosted tool can fail while the model still answers ("I could not search the
        // web, but…"). The answer is on screen; an error bubble under it would contradict it.
        await using var host = MentorHostFixture.Create(withRecorder: true);
        host.Chat!.Respond((_, _) => new Microsoft.Extensions.AI.ChatResponse(
            new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant,
            [
                new Microsoft.Extensions.AI.TextContent("There are 3 open orders."),
                new Microsoft.Extensions.AI.ErrorContent("web_search failed") { ErrorCode = "server_error" },
            ])));

        await host.RunTurnAsync("how many open orders are there?");

        ErrorsShown(host).Should().BeEmpty();
    }
}
