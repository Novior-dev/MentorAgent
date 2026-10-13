using FluentAssertions;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;
using Xunit.Abstractions;

namespace Novior.MentorAgent.Tests.Filtering;

/// <summary>
/// History compaction — the wiring and the option semantics.
///
/// <para>
/// The compaction <i>algorithm</i> belongs to the Agent Framework: MentorAgent assembles a
/// <c>PipelineCompactionStrategy</c> from three of AF's strategies and hands it over. Testing how a
/// sliding window slides would be testing someone else's code. What MentorAgent owns, and what these
/// tests cover, is the part a developer configures: whether it is on, what the thresholds are, the
/// order the strategies run in, and — the one that actually bites — that it is silently inert under
/// service-managed history.
/// </para>
/// </summary>
public sealed class CompactionTests(ITestOutputHelper output)
{
    private static MentorHostFixture WithCompaction(Action<Novior.MentorAgent.Options.MentorOptions>? configure = null)
        => MentorHostFixture.Create(o =>
        {
            o.EnableCompaction = true;
            configure?.Invoke(o);
        });

    // ── On and off ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Compaction_is_off_by_default()
    {
        // Opt-in on purpose: it changes what the model sees, and a library that silently drops
        // conversation history would be indistinguishable from a model with a short memory.
        await using var host = MentorHostFixture.Create();
        host.Chat!.SayRepeatedly("ok", times: 2);

        await host.RunTurnAsync("hello");

        host.Options.EnableCompaction.Should().BeFalse();
        host.Logs.Records.Select(r => r.ToString()).Should().NotContain(r => r.Contains("Compaction enabled"));
    }

    [Fact]
    public async Task Enabling_it_is_confirmed_with_the_values_actually_in_force()
    {
        // The numbers are in the message because the defaults (4000 tokens / 8 turns) are the ones
        // people forget they never changed.
        await using var host = WithCompaction(o =>
        {
            o.CompactionTokenThreshold = 1234;
            o.CompactionMaxTurns = 5;
        });
        host.Chat!.SayRepeatedly("ok", times: 2);

        await host.RunTurnAsync("hello");

        var text = string.Join("\n", host.Logs.Records.Select(r => r.ToString()));
        output.WriteLine(text);
        text.Should().Contain("Compaction enabled").And.Contain("1234").And.Contain("5");
    }

    [Fact]
    public void The_documented_defaults_are_what_the_options_carry()
    {
        var options = new Novior.MentorAgent.Options.MentorOptions
        {
            AppName = "x",
            ScanAssemblies = [typeof(Novior.MentorAgent.Tests.App.TestOrderService).Assembly],
        };

        options.EnableCompaction.Should().BeFalse();
        options.CompactionTokenThreshold.Should().Be(4000);
        options.CompactionMaxTurns.Should().Be(8);
    }

    // ── The interaction that makes it inert ─────────────────────────────────────

    [Fact]
    public async Task With_service_managed_history_compaction_warns_that_it_does_nothing()
    {
        // There is no local history to compact — the service owns the conversation. Enabling both
        // is a configuration that looks like it is controlling context and is not, which is exactly
        // the shape of misconfiguration this library warns about everywhere else.
        await using var host = WithCompaction(o => o.UseServiceManagedHistory = true);
        host.Chat!.SayRepeatedly("ok", times: 2);

        await host.RunTurnAsync("hello");

        host.Logs.HasWarning("Compaction has no effect with UseServiceManagedHistory").Should().BeTrue(
            "warnings seen:{0}{1}", Environment.NewLine, host.Logs.WarningText);
    }

    [Fact]
    public async Task The_warning_names_what_to_do_instead()
    {
        await using var host = WithCompaction(o => o.UseServiceManagedHistory = true);
        host.Chat!.SayRepeatedly("ok", times: 2);

        await host.RunTurnAsync("hello");

        host.Logs.WarningText.Should().Contain("Manage context on the service side",
            "a warning that only says 'this does nothing' leaves the reader stuck");
    }

    [Fact]
    public async Task Without_service_managed_history_there_is_no_such_warning()
    {
        await using var host = WithCompaction();
        host.Chat!.SayRepeatedly("ok", times: 2);

        await host.RunTurnAsync("hello");

        host.Logs.HasWarning("Compaction has no effect").Should().BeFalse();
    }

    [Fact]
    public async Task Service_managed_history_also_reports_that_a_history_provider_is_ignored()
    {
        // The sibling case, warned about in the same block: two ways to configure context, one of
        // which silently loses.
        await using var host = MentorHostFixture.Create(o =>
        {
            o.UseServiceManagedHistory = true;
            o.ChatHistoryProvider = new NoOpHistoryProvider();
        });
        host.Chat!.SayRepeatedly("ok", times: 2);

        await host.RunTurnAsync("hello");

        var reported = host.Logs.WarningText + host.Logs.ExceptionText;
        reported.Should().NotBeEmpty();
    }

    // ── Turns survive the configuration ─────────────────────────────────────────

    [Fact]
    public async Task A_turn_completes_normally_with_compaction_on()
    {
        // The pipeline gains a provider; the simplest thing that could go wrong is it not building.
        await using var host = WithCompaction();
        host.Chat!.Say("the answer");

        var error = await host.RunTurnAsync("a question");

        error.Should().BeNull();
        host.Chat.CallCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Several_turns_in_a_row_survive_compaction()
    {
        // Compaction only has anything to do once there is history, so a single turn proves very
        // little. This walks far enough past CompactionMaxTurns for the sliding window to engage.
        await using var host = WithCompaction(o => o.CompactionMaxTurns = 2);
        host.Chat!.SayRepeatedly("ok", times: 12);

        for (var i = 0; i < 6; i++)
        {
            var error = await host.RunTurnAsync($"message number {i}");
            error.Should().BeNull("turn {0} failed", i);
        }

        host.Chat.CallCount.Should().BeGreaterThanOrEqualTo(6);
    }

    [Fact]
    public async Task The_system_prompt_is_still_sent_after_several_compacted_turns()
    {
        // The one thing compaction must never reach. Instructions are not history — they travel in
        // ChatOptions — but a future change that folded them into the message list would make the
        // assistant lose its identity, its security rules and its capability list mid-conversation.
        await using var host = WithCompaction(o => o.CompactionMaxTurns = 1);
        host.Chat!.SayRepeatedly("ok", times: 12);

        for (var i = 0; i < 5; i++) await host.RunTurnAsync($"message {i}");

        host.Chat.Calls[^1].Instructions.Should().NotBeNullOrEmpty();
        host.Chat.Calls[^1].Instructions.Should().Contain("Security Rules",
            "the security block has to survive every compaction");
    }

    // ── Boundaries ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_zero_token_threshold_does_not_throw()
    {
        // Reachable from configuration binding of an unset value. The derived tool-result trigger
        // is floored at 256 rather than 0 for this reason.
        await using var host = WithCompaction(o => o.CompactionTokenThreshold = 0);
        host.Chat!.SayRepeatedly("ok", times: 4);

        var error = await host.RunTurnAsync("hello");

        error.Should().BeNull();
    }

    [Fact]
    public async Task A_negative_token_threshold_does_not_throw()
    {
        await using var host = WithCompaction(o => o.CompactionTokenThreshold = -1);
        host.Chat!.SayRepeatedly("ok", times: 4);

        var error = await host.RunTurnAsync("hello");

        error.Should().BeNull();
    }

    [Fact]
    public async Task A_zero_turn_window_does_not_throw()
    {
        await using var host = WithCompaction(o => o.CompactionMaxTurns = 0);
        host.Chat!.SayRepeatedly("ok", times: 4);

        var error = await host.RunTurnAsync("hello");

        error.Should().BeNull();
    }

    [Fact]
    public async Task A_very_large_threshold_simply_never_triggers()
    {
        await using var host = WithCompaction(o =>
        {
            o.CompactionTokenThreshold = int.MaxValue;
            o.CompactionMaxTurns = int.MaxValue;
        });
        host.Chat!.SayRepeatedly("ok", times: 4);

        var error = await host.RunTurnAsync("hello");

        error.Should().BeNull();
    }

    /// <summary>A history provider that stores nothing — its presence is the whole point.</summary>
    private sealed class NoOpHistoryProvider : Microsoft.Agents.AI.ChatHistoryProvider
    {
        protected override ValueTask<IEnumerable<Microsoft.Extensions.AI.ChatMessage>> ProvideChatHistoryAsync(
            InvokingContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Enumerable.Empty<Microsoft.Extensions.AI.ChatMessage>());

        protected override ValueTask StoreChatHistoryAsync(
            InvokedContext context, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }

    // ── BUG-104: the stored conversation, tool turns included ──────────────────
    //
    // Compaction runs on every model call. On the second call of a run — after a tool result — the Agent
    // Framework's CompactionProvider marked the messages it had already indexed, the in-flight question
    // included, as chat history IN PLACE (microsoft/agent-framework#8441), and the history provider's
    // default filter then declined to store a request message so marked. The tool call, its result and the
    // answer were stored; the question that caused them was not. Found reading a session snapshot turn by
    // turn on the published rc.13.

    private const string ToolTurn = "Quanti ordini aperti? Pratica BETA-2222";

    private static MentorHostFixture ToolTurnHost(bool compaction = true, Action<Novior.MentorAgent.Options.MentorOptions>? configure = null)
    {
        var host = MentorHostFixture.Create(o =>
        {
            o.EnableCompaction = compaction;
            o.MemoryAutoCapture = false;
            configure?.Invoke(o);
        });
        host.Chat!.CallTool("count_open_orders").Say("Due ordini aperti.").Say("prego");
        return host;
    }

    private static async Task RunToolTurnThenTextTurnAsync(MentorHostFixture host)
    {
        (await host.RunTurnAsync(ToolTurn)).Should().BeNull();
        (await host.RunTurnAsync("Grazie, pratica GAMMA-3333")).Should().BeNull();
    }

    /// <summary>
    /// Asserts the list holds the tool turn's question exactly once — and, where the tool call itself is
    /// kept (the live Chat Completions store keeps it; this harness's run stores question and answer only),
    /// before the call it caused.
    /// </summary>
    private static void HoldsTheQuestionOnceBeforeTheCall(IReadOnlyList<ChatMessage> messages, string where)
    {
        var questions = messages
            .Select((m, i) => (m, i))
            .Where(x => x.m.Role == ChatRole.User && x.m.Text.Contains("BETA-2222", StringComparison.Ordinal))
            .ToList();
        questions.Should().ContainSingle($"{where} must hold the question of the turn that called a tool");

        var call = messages.ToList().FindIndex(m => m.Contents.OfType<FunctionCallContent>().Any(c => c.Name == "count_open_orders"));
        if (call >= 0)
            questions[0].i.Should().BeLessThan(call, $"in {where} the question comes before the call it caused");
    }

    [Fact]
    public async Task A_tool_turn_keeps_its_question_in_the_stored_history()
    {
        await using var host = ToolTurnHost();

        await RunToolTurnThenTextTurnAsync(host);

        HoldsTheQuestionOnceBeforeTheCall(host.Chat!.Calls[2].Messages, "the next turn's request");
        var stored = await host.StoredHistoryAsync();
        HoldsTheQuestionOnceBeforeTheCall(stored, "the stored conversation");
        stored.Count(m => m.Role == ChatRole.User && m.Text.Contains("GAMMA-3333")).Should().Be(1);
        host.Chat.Calls.Should().OnlyContain(c => c.Streaming, "the coordinator always streams; this is that path");
    }

    [Fact]
    public async Task A_tool_turn_keeps_its_question_with_a_session_state_provider()
    {
        // The README's recipe for a custom provider, and the samples' shape (BUG-102).
        await using var host = ToolTurnHost(configure: o => o.ChatHistoryProviderFactory = _ => new SessionProvider());

        await RunToolTurnThenTextTurnAsync(host);

        HoldsTheQuestionOnceBeforeTheCall(host.Chat!.Calls[2].Messages, "the next turn's request");
        HoldsTheQuestionOnceBeforeTheCall(await host.StoredHistoryAsync(nameof(SessionProvider)), "the stored conversation");
    }

    [Fact]
    public async Task A_tool_turn_keeps_its_question_with_a_provider_instance()
    {
        var provider = new ListProvider();
        await using var host = ToolTurnHost(configure: o => o.ChatHistoryProvider = provider);

        await RunToolTurnThenTextTurnAsync(host);

        HoldsTheQuestionOnceBeforeTheCall(host.Chat!.Calls[2].Messages, "the next turn's request");
        HoldsTheQuestionOnceBeforeTheCall(provider.Stored, "what the provider was asked to store");
    }

    [Fact]
    public async Task A_turn_that_only_ran_a_ui_action_keeps_its_question()
    {
        // UIActionsMiddleware runs its own loop below the agent, calling the compacted client twice with
        // the same message instances. A session whose only turn ran a UI action stored no question at all.
        await using var host = MentorHostFixture.Create(o =>
        {
            o.EnableCompaction = true;
            o.MemoryAutoCapture = false;
        });
        host.Get<Novior.MentorAgent.Abstractions.Interfaces.IMentorPageContext>()
            .RegisterUIAction("highlight_row", "Highlights a row", _ => { });
        host.Chat!.CallTool("highlight_row", new { parameter = 5 }).Say("Riga evidenziata.").Say("prego");

        (await host.RunTurnAsync("Evidenzia la riga 5. Pratica DELTA-4444")).Should().BeNull();
        (await host.RunTurnAsync("Grazie")).Should().BeNull();

        var stored = await host.StoredHistoryAsync();
        stored.Count(m => m.Role == ChatRole.User && m.Text.Contains("DELTA-4444")).Should().Be(1);
        stored[0].Role.Should().Be(ChatRole.User, "the stored conversation starts with the user's question");
        host.Chat.Calls[^1].Messages.Count(m => m.Role == ChatRole.User && m.Text.Contains("DELTA-4444"))
            .Should().Be(1);
    }

    [Fact]
    public async Task The_stored_history_carries_no_compaction_marking()
    {
        // The index shared message objects with the store, so the next call stamped the compaction
        // provider's attribution into messages ALREADY stored — on text-only turns too. Asserted on the
        // provider's own state: compaction's state legitimately carries that attribution.
        await using var host = WithCompaction(o => o.MemoryAutoCapture = false);
        host.Chat!.Say("ok").Say("prego");

        await host.RunTurnAsync("Ciao, pratica ALFA-1111");
        await host.RunTurnAsync("Grazie");

        var raw = await host.StoredHistoryJsonAsync();
        raw.Should().NotBeNull();
        raw!.Value.GetRawText().Should().Contain("ALFA-1111").And.NotContain("CompactionProvider");
    }

    [Fact]
    public async Task With_service_managed_history_compaction_is_not_installed()
    {
        // It has nothing to compact (the service owns the conversation) — but installed, it still ran on a
        // session's first turn and stored its state.
        await using var host = WithCompaction(o =>
        {
            o.UseServiceManagedHistory = true;
            o.MemoryAutoCapture = false;
        });
        host.Chat!.SayRepeatedly("ok", 2);

        await host.RunTurnAsync("hello");

        var snapshot = await host.Orchestrator.SerializeSessionAsync();
        snapshot!.Value.GetRawText().Should().NotContain("PipelineCompactionStrategy");
        host.Logs.Records.Should().NotContain(r => r.Message.Contains("Compaction enabled"));
        host.Logs.HasWarning("Compaction has no effect with UseServiceManagedHistory").Should().BeTrue();
    }

    [Fact]
    public async Task Without_compaction_a_tool_turn_keeps_its_question()
    {
        // The control: the same script, compaction off. Proves the harness and the assertion.
        await using var host = ToolTurnHost(compaction: false);

        await RunToolTurnThenTextTurnAsync(host);

        HoldsTheQuestionOnceBeforeTheCall(host.Chat!.Calls[2].Messages, "the next turn's request");
        HoldsTheQuestionOnceBeforeTheCall(await host.StoredHistoryAsync(), "the stored conversation");
    }

    /// <summary>The README's recipe: the transcript in the session's StateBag.</summary>
    private sealed class SessionProvider : ChatHistoryProvider
    {
        private readonly ProviderSessionState<List<ChatMessage>> _transcript =
            new(_ => [], nameof(SessionProvider), AIJsonUtilities.DefaultOptions);

        protected override ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
            InvokingContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IEnumerable<ChatMessage>>([.. _transcript.GetOrInitializeState(context.Session)]);

        protected override ValueTask StoreChatHistoryAsync(
            InvokedContext context, CancellationToken cancellationToken = default)
        {
            var history = _transcript.GetOrInitializeState(context.Session);
            history.AddRange(context.RequestMessages);
            history.AddRange(context.ResponseMessages ?? []);
            _transcript.SaveState(context.Session, history);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>An instance provider (single-user host) whose stored list the test can read.</summary>
    private sealed class ListProvider : ChatHistoryProvider
    {
        internal List<ChatMessage> Stored { get; } = [];

        protected override ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
            InvokingContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IEnumerable<ChatMessage>>([.. Stored]);

        protected override ValueTask StoreChatHistoryAsync(
            InvokedContext context, CancellationToken cancellationToken = default)
        {
            Stored.AddRange(context.RequestMessages);
            Stored.AddRange(context.ResponseMessages ?? []);
            return ValueTask.CompletedTask;
        }
    }
}
