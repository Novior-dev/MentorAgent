using FluentAssertions;
using Novior.MentorAgent.Tests.Harness;
using Xunit;

namespace Novior.MentorAgent.Tests.Middleware;

/// <summary>
/// The middleware hooks a host can install around a turn: input and output guardrails, the
/// tool-result transform, and the exception mapper.
///
/// <para>
/// The output guardrail carries the sharpest requirement in the library. Moderating a reply is
/// pointless if the reply has already been streamed to the screen — so when moderation is on, the
/// turn is buffered and revealed all at once. A regression that let one chunk through early would
/// still pass a naive "was it blocked" assertion while defeating the entire feature, which is why
/// the tests below assert on what the user <em>saw</em>, not on the final state.
/// </para>
/// </summary>
public sealed class GuardrailTests
{
    // ── Input guardrail ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_custom_input_guardrail_can_block_a_message()
    {
        await using var host = MentorHostFixture.Create(o =>
            o.InputGuardrail = (_, _) => Task.FromResult(false));
        host.Chat!.Say("this should never be produced");

        await host.RunTurnAsync("anything");

        host.Chat.CallCount.Should().Be(0, "a blocked message must never reach the model");
    }

    [Fact]
    public async Task A_blocked_message_tells_the_user_and_clears_the_busy_flag()
    {
        // A block that leaves the widget spinning reads as a hang, which is worse than a refusal.
        await using var host = MentorHostFixture.Create(
            withRecorder: true,
            configure: o => o.InputGuardrail = (_, _) => Task.FromResult(false));

        await host.RunTurnAsync("anything");

        var events = host.Recorder!.Log.Select(e => e.Event).ToList();
        events.Should().Contain("NotifyError");
        events.Should().Contain("NotifyBusyChanged");
    }

    [Fact]
    public async Task A_custom_input_guardrail_that_allows_lets_the_turn_run()
    {
        await using var host = MentorHostFixture.Create(o =>
            o.InputGuardrail = (_, _) => Task.FromResult(true));
        host.Chat!.Say("the reply");

        var error = await host.RunTurnAsync("anything");

        error.Should().BeNull();
        host.Chat.CallCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task The_custom_guardrail_receives_the_user_message()
    {
        string? seen = null;
        await using var host = MentorHostFixture.Create(o => o.InputGuardrail = (message, _) =>
        {
            seen = message;
            return Task.FromResult(true);
        });
        host.Chat!.Say("ok");

        await host.RunTurnAsync("the exact text the user typed");

        seen.Should().Be("the exact text the user typed");
    }

    [Fact]
    public async Task A_custom_guardrail_runs_even_when_the_built_in_check_is_off()
    {
        // Documented: "When set it runs on every message (whether or not EnableSafetyCheck is on)".
        // A host that installed a guardrail and forgot the unrelated flag must still be protected.
        var ran = false;
        await using var host = MentorHostFixture.Create(o =>
        {
            o.EnableSafetyCheck = false;
            o.InputGuardrail = (_, _) => { ran = true; return Task.FromResult(true); };
        });
        host.Chat!.Say("ok");

        await host.RunTurnAsync("hello");

        ran.Should().BeTrue();
    }

    [Fact]
    public async Task A_custom_guardrail_replaces_the_built_in_check_rather_than_adding_to_it()
    {
        // "Replaces", not "runs alongside". If both ran, enabling the built-in check would cost an
        // extra model call per turn that the host neither asked for nor can see.
        await using var host = MentorHostFixture.Create(o =>
        {
            o.EnableSafetyCheck = true;
            o.InputGuardrail = (_, _) => Task.FromResult(true);
        });
        host.Chat!.SayRepeatedly("ok", times: 4);

        await host.RunTurnAsync("hello");

        // One conversation call. A built-in safety check would show up as an extra classification
        // request on the same client.
        host.Chat.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task No_guardrail_and_no_safety_check_means_no_extra_call()
    {
        await using var host = MentorHostFixture.Create(o => o.EnableSafetyCheck = false);
        host.Chat!.SayRepeatedly("ok", times: 4);

        await host.RunTurnAsync("hello");

        host.Chat.CallCount.Should().Be(1, "safety checking is opt-in and costs a call");
    }

    // ── Output guardrail ────────────────────────────────────────────────────────

    [Fact]
    public async Task With_output_moderation_on_nothing_is_streamed_before_the_verdict()
    {
        // The property that makes output moderation worth anything. If chunks arrive while the
        // moderator is still deciding, the blocked text is already on the user's screen.
        var chunksBeforeVerdict = -1;

        // The guardrail runs during the turn, long after Create returns — but C# will not let the
        // lambda name `host` inside its own initializer, so the fixture is handed over explicitly.
        MentorHostFixture? captured = null;
        await using var host = MentorHostFixture.Create(
            withRecorder: true,
            configure: o => o.OutputGuardrail = (_, _) =>
            {
                chunksBeforeVerdict = captured!.Recorder!.Log.Count(e => e.Event == "NotifyStreamingChunk");
                return Task.FromResult(true);
            });
        captured = host;
        host.Chat!.Say("a long reply split into several streaming chunks");

        await host.RunTurnAsync("hello");

        chunksBeforeVerdict.Should().Be(0,
            "the reply must be buffered until moderation has answered");
    }

    [Fact]
    public async Task An_approved_reply_is_revealed_after_moderation()
    {
        await using var host = MentorHostFixture.Create(
            withRecorder: true,
            configure: o => o.OutputGuardrail = (_, _) => Task.FromResult(true));
        host.Chat!.Say("a perfectly fine reply");

        await host.RunTurnAsync("hello");

        var streamed = string.Concat(host.Recorder!.Log
            .Where(e => e.Event == "NotifyStreamingChunk")
            .Select(e => e.Args[0] as string));
        streamed.Should().Contain("a perfectly fine reply");
    }

    [Fact]
    public async Task A_rejected_reply_never_reaches_the_user()
    {
        await using var host = MentorHostFixture.Create(
            withRecorder: true,
            configure: o => o.OutputGuardrail = (_, _) => Task.FromResult(false));
        host.Chat!.Say("something the moderator will refuse");

        await host.RunTurnAsync("hello");

        var streamed = string.Concat(host.Recorder!.Log
            .Where(e => e.Event == "NotifyStreamingChunk")
            .Select(e => e.Args[0] as string));
        streamed.Should().NotContain("something the moderator will refuse");
    }

    [Fact]
    public async Task The_output_guardrail_receives_the_completed_reply_not_a_fragment()
    {
        // Moderating a partial sentence would flag or clear the wrong thing.
        string? moderated = null;
        await using var host = MentorHostFixture.Create(o => o.OutputGuardrail = (text, _) =>
        {
            moderated = text;
            return Task.FromResult(true);
        });
        host.Chat!.Say("one two three four five");

        await host.RunTurnAsync("hello");

        moderated.Should().Contain("one two three four five");
    }

    [Fact]
    public async Task A_failing_output_guardrail_fails_open_and_shows_the_reply()
    {
        // Documented: fails open. A moderation outage that silenced every reply would take the
        // whole assistant down, and the reply was produced by the configured model — not by an
        // attacker — so showing it is the lesser risk.
        await using var host = MentorHostFixture.Create(
            withRecorder: true,
            configure: o => o.OutputGuardrail = (_, _) => throw new InvalidOperationException("moderator down"));
        host.Chat!.Say("the reply");

        var error = await host.RunTurnAsync("hello");

        error.Should().BeNull();
        string.Concat(host.Recorder!.Log.Where(e => e.Event == "NotifyStreamingChunk").Select(e => e.Args[0] as string))
            .Should().Contain("the reply");
    }

    [Fact]
    public async Task Without_output_moderation_the_reply_streams_live()
    {
        // The default. Buffering every reply would cost the streaming experience for everyone.
        await using var host = MentorHostFixture.Create(withRecorder: true);
        host.Chat!.Say("one two three four five six");

        await host.RunTurnAsync("hello");

        host.Recorder!.Log.Count(e => e.Event == "NotifyStreamingChunk").Should().BeGreaterThan(1,
            "the fake splits on words, so live streaming produces several chunks");
    }

    // ── Tool-result transform ───────────────────────────────────────────────────

    [Fact]
    public async Task OnToolResult_sees_every_tool_invocation()
    {
        var seen = new List<string>();
        await using var host = MentorHostFixture.Create(o => o.OnToolResult = (name, result) =>
        {
            seen.Add(name);
            return result;
        });
        host.Chat!.CallTool("count_open_orders").Say("done");

        await host.RunTurnAsync("how many open orders");

        seen.Should().Contain("count_open_orders");
    }

    [Fact]
    public async Task OnToolResult_can_replace_the_value_the_model_receives()
    {
        // The documented use: redaction. What matters is that the replacement is what travels on,
        // not merely that the hook ran.
        await using var host = MentorHostFixture.Create(o =>
            o.OnToolResult = (_, _) => "REDACTED");
        host.Chat!.CallTool("count_open_orders").Say("done");

        await host.RunTurnAsync("how many open orders");

        // A tool result travels as FunctionResultContent, not as message text — reading `.Text`
        // finds only the user's own message and would pass for any transform at all.
        var results = host.Chat.Calls[^1].Messages
            .SelectMany(m => m.Contents)
            .OfType<Microsoft.Extensions.AI.FunctionResultContent>()
            .Select(c => c.Result?.ToString() ?? "");

        string.Join(" ", results).Should().Contain("REDACTED");
    }

    [Fact]
    public async Task Returning_the_result_unchanged_is_a_no_op()
    {
        await using var host = MentorHostFixture.Create(o => o.OnToolResult = (_, result) => result);
        host.Chat!.CallTool("count_open_orders").Say("done");

        var error = await host.RunTurnAsync("how many open orders");

        error.Should().BeNull();
    }

    [Fact]
    public async Task Without_the_hook_a_tool_result_travels_untouched()
    {
        await using var host = MentorHostFixture.Create();
        host.Chat!.CallTool("count_open_orders").Say("done");

        var error = await host.RunTurnAsync("how many open orders");

        error.Should().BeNull();
        host.Chat.Calls.Should().HaveCountGreaterThan(1);
    }

    // ── Exception mapping ───────────────────────────────────────────────────────

    [Fact]
    public async Task OnException_replaces_the_message_shown_to_the_user()
    {
        await using var host = MentorHostFixture.Create(
            withRecorder: true,
            configure: o => o.OnException = _ => "Our order system is having a moment. Try again shortly.");
        host.Chat!.ThrowOnNextCall = new InvalidOperationException("upstream exploded");

        await host.RunTurnAsync("hello");

        var errors = host.Recorder!.Log.Where(e => e.Event == "NotifyError").Select(e => e.Args[0] as string);
        string.Join(" ", errors).Should().Contain("Our order system is having a moment");
    }

    [Fact]
    public async Task An_internal_exception_message_is_never_shown_verbatim()
    {
        // Without a custom mapper the built-in fallback applies. A raw exception message can carry
        // a connection string, a deployment name or a stack frame.
        await using var host = MentorHostFixture.Create(withRecorder: true);
        host.Chat!.ThrowOnNextCall = new InvalidOperationException(
            "Endpoint https://secret.openai.azure.com rejected key sk-abc123");

        await host.RunTurnAsync("hello");

        var errors = string.Join(" ", host.Recorder!.Log.Where(e => e.Event == "NotifyError").Select(e => e.Args[0] as string));
        errors.Should().NotContain("sk-abc123").And.NotContain("secret.openai.azure.com");
    }

    [Fact]
    public async Task Returning_null_from_OnException_falls_back_to_the_built_in_mapping()
    {
        // Documented escape hatch: the hook can decline a case and let the library handle it.
        await using var host = MentorHostFixture.Create(
            withRecorder: true,
            configure: o => o.OnException = _ => null);
        host.Chat!.ThrowOnNextCall = new InvalidOperationException("boom");

        await host.RunTurnAsync("hello");

        var errors = string.Join(" ", host.Recorder!.Log.Where(e => e.Event == "NotifyError").Select(e => e.Args[0] as string));
        errors.Should().NotBeEmpty("declining still has to produce a message");
        errors.Should().NotContain("boom");
    }

    [Fact]
    public async Task A_whitespace_only_mapping_is_treated_as_a_decline()
    {
        // An empty error bubble is the same as no feedback at all.
        await using var host = MentorHostFixture.Create(
            withRecorder: true,
            configure: o => o.OnException = _ => "   ");
        host.Chat!.ThrowOnNextCall = new InvalidOperationException("boom");

        await host.RunTurnAsync("hello");

        var errors = string.Join(" ", host.Recorder!.Log.Where(e => e.Event == "NotifyError").Select(e => e.Args[0] as string));
        errors.Trim().Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_provider_content_filter_maps_to_the_guardrail_message()
    {
        // The one built-in special case: Azure reports its own moderation as an exception carrying
        // "content_filter", and telling the user "an error occurred" would be misleading — nothing
        // is broken, the request was refused.
        await using var host = MentorHostFixture.Create(withRecorder: true);
        host.Chat!.ThrowOnNextCall = new InvalidOperationException(
            "The response was filtered due to content_filter policy");

        await host.RunTurnAsync("hello");

        var errors = string.Join(" ", host.Recorder!.Log.Where(e => e.Event == "NotifyError").Select(e => e.Args[0] as string));
        errors.Should().NotContain("An error occurred while processing");
    }

    [Fact]
    public async Task The_exception_object_itself_reaches_the_mapper()
    {
        Exception? seen = null;
        await using var host = MentorHostFixture.Create(o => o.OnException = ex =>
        {
            seen = ex;
            return "handled";
        });
        host.Chat!.ThrowOnNextCall = new TimeoutException("took too long");

        await host.RunTurnAsync("hello");

        seen.Should().BeOfType<TimeoutException>("the mapper has to be able to switch on the type");
    }
}
