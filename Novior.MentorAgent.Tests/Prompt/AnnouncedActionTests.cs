using FluentAssertions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Middleware;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.App;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Novior.MentorAgent.Tests.Prompt;

/// <summary>
/// Story 526 (BUG-220) — in Blocking mode with Chat Completions the model can announce an action ("Procedo a registrare la
/// nota…") and end its reply without calling it; a rule in the prompt only turned the miss into an invented "done". With
/// <see cref="MentorOptions.CheckAnnouncedActions"/>, after a turn in which nothing ran, a classifier model reads the reply;
/// when it announces an action or reports one as done, the coordinator gets one more request in the same turn, with the
/// same tools, told to call the tool now or to say that it was not done. Never a required call, never a second check.
/// </summary>
public sealed class AnnouncedActionTests
{
    private static readonly string Instruction =
        AnnouncedActionChatClient.Heading + MentorPromptTexts.English.AnnouncedActionNotRun;

    private static MentorHostFixture Host(FakeChatClient? classifier, Action<MentorOptions>? configure = null)
        => MentorHostFixture.Create(o =>
        {
            o.MemoryAutoCapture = false;
            o.CheckAnnouncedActions = true;
            if (classifier is not null) o.ClassifierChatClient = classifier;
            configure?.Invoke(o);
        }, withRecorder: true);

    private static FakeChatClient Classifier(params string[] answers)
    {
        var classifier = new FakeChatClient();
        foreach (var answer in answers) classifier.Say(answer);
        return classifier;
    }

    /// <summary>The coordinator's requests: the ones that carried tools.</summary>
    private static List<FakeChatClient.RecordedCall> Coordinator(MentorHostFixture host)
        => [.. host.Chat!.Calls.Where(c => c.Tools.Count > 0)];

    /// <summary>Whether a request carries the check's instruction, in its messages or in its instructions.</summary>
    private static bool HasInstruction(FakeChatClient.RecordedCall call)
        => (call.Instructions ?? "").Contains(AnnouncedActionChatClient.Heading)
           || call.Messages.Any(m => (m.Text ?? "").Contains(AnnouncedActionChatClient.Heading));

    /// <summary>The second request's last message when it is a user message: where the instruction goes (BUG-231).</summary>
    private static string? LastInstruction(FakeChatClient.RecordedCall call)
        => call.Messages[^1].Role == ChatRole.User ? call.Messages[^1].Text : null;

    // ── The second request ───────────────────────────────────────────────────────

    [Fact]
    public async Task An_announced_action_gets_one_more_request_that_may_call_it()
    {
        var classifier = Classifier("ANNOUNCED");
        await using var host = Host(classifier);
        host.Chat!.Say("Procedo a contare gli ordini aperti…")
            .CallTool("count_open_orders").Say("Ci sono 3 ordini aperti.");

        (await host.RunTurnAsync("quanti ordini sono aperti?")).Should().BeNull(host.Logs.WarningText);

        classifier.CallCount.Should().Be(1);
        classifier.Calls[0].Messages[1].Text.Should().Contain("quanti ordini sono aperti?").And.Contain("Procedo a contare");
        var requests = Coordinator(host);
        requests.Should().HaveCount(3, "the reply, the second request that calls the tool, and the one that reads its result");
        HasInstruction(requests[0]).Should().BeFalse();
        LastInstruction(requests[1]).Should().Be(Instruction,
            "the instruction is the second request's last message, read after the reply it is about");
        requests[1].Instructions.Should().NotContain(AnnouncedActionChatClient.Heading);
        requests[1].ToolNames.Should().Equal(requests[0].ToolNames, "the same tools");
        requests[1].ToolMode.Should().NotBe(ChatToolMode.RequireAny, "a call is never required");
        HasInstruction(requests[2]).Should().BeFalse(
            "the request that reads the tool's result is not told again to call it");
        host.Get<TestOrderService>().Invoked.Should().Equal(nameof(TestOrderService.CountOpenOrders));

        host.Recorder!.StreamedText.Should().Contain("Procedo a contare").And.Contain("Ci sono 3 ordini aperti.",
            "the first reply is not held back, and the second goes on in the same turn");
        host.Recorder.Sequence.Count(e => e == nameof(RecordingStateService.NotifyStreamingCompleted)).Should().Be(2,
            "each request's reply is a message of its own, as in the approval rounds of Native: the check comes after the first ended");
        (await host.StoredHistoryAsync()).Should().NotContain(m => (m.Text ?? "").Contains(AnnouncedActionChatClient.Heading),
            "the instruction is in the request, never in the conversation");
    }

    [Fact]
    public async Task A_template_that_takes_a_system_message_only_first_still_gets_the_second_request()
    {
        // BUG-231 — Ministral 3 on llama-server, the kit's default model: its chat template raises on a system message
        // that is not the first ("Only user, assistant and tool roles are supported"), as those of Mistral, Gemma and
        // others do. The instruction was a system message at the end, so the second request failed with a 500 every
        // time and the check never reached it.
        var classifier = Classifier("ANNOUNCED");
        await using var host = Host(classifier);
        host.Chat!.Say("Procedo a contare gli ordini aperti…")
            .Respond((messages, _) => StrictTemplate(messages, new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("call-1", "count_open_orders", new Dictionary<string, object?>())]))
                { ModelId = host.Chat.ModelId }))
            .Respond((messages, _) => StrictTemplate(messages, new ChatResponse(
                new ChatMessage(ChatRole.Assistant, "Ci sono 3 ordini aperti.")) { ModelId = host.Chat.ModelId }));

        (await host.RunTurnAsync("quanti ordini sono aperti?")).Should().BeNull();

        host.Logs.WarningText.Should().NotContain("second request after an announced action failed");
        host.Get<TestOrderService>().Invoked.Should().Equal(nameof(TestOrderService.CountOpenOrders));
        host.Recorder!.StreamedText.Should().Contain("Ci sono 3 ordini aperti.");
    }

    [Fact]
    public async Task With_the_history_kept_by_the_service_no_second_request_is_made()
    {
        // The check runs on the history MentorAgent keeps: with UseServiceManagedHistory every message of a request would
        // be stored on the service, so the reply is not even asked about.
        var classifier = Classifier("ANNOUNCED");
        await using var host = Host(classifier, o => o.UseServiceManagedHistory = true);
        host.Chat!.Respond((_, _) => new ChatResponse(new ChatMessage(ChatRole.Assistant, "Procedo a contare gli ordini aperti…"))
        {
            ModelId = host.Chat.ModelId,
            ConversationId = "resp_1",
        });

        (await host.RunTurnAsync("quanti ordini sono aperti?")).Should().BeNull(host.Logs.WarningText);

        classifier.CallCount.Should().Be(0);
        Coordinator(host).Should().ContainSingle().Which.Messages.Should().NotContain(m => (m.Text ?? "").Contains(AnnouncedActionChatClient.Heading));
    }

    /// <summary>A chat template that takes a system message only as the first: what llama-server answers with a 500.</summary>
    private static ChatResponse StrictTemplate(IReadOnlyList<ChatMessage> messages, ChatResponse response)
        => messages.Skip(1).Any(m => m.Role == ChatRole.System)
            ? throw new InvalidOperationException("Only user, assistant and tool roles are supported after the first message.")
            : response;

    [Fact]
    public async Task An_action_reported_as_done_when_nothing_ran_gets_one_more_request()
    {
        // The case of the live probe of 03/11: "I've handled the rejection", and no call.
        var classifier = Classifier("CLAIMED");
        await using var host = Host(classifier);
        host.Chat!.Say("I've handled the rejection.").Say("It was not done: I cannot do it here.");

        await host.RunTurnAsync("yes");

        var requests = Coordinator(host);
        requests.Should().HaveCount(2);
        LastInstruction(requests[1]).Should().Be(Instruction);
        host.Recorder!.StreamedText.Should().Contain("It was not done");
    }

    [Fact]
    public async Task The_second_request_is_made_once_and_its_reply_is_not_checked()
    {
        var classifier = Classifier("ANNOUNCED", "ANNOUNCED");
        await using var host = Host(classifier);
        host.Chat!.Say("Procedo…").Say("Procedo di nuovo…");

        await host.RunTurnAsync("registra la nota");

        classifier.CallCount.Should().Be(1);
        Coordinator(host).Should().HaveCount(2);
    }

    [Fact]
    public async Task The_instruction_is_in_the_language_of_the_application()
    {
        var classifier = Classifier("ANNOUNCED");
        await using var host = Host(classifier, o => o.Language = MentorLanguage.Italian);
        host.Chat!.Say("Procedo…").Say("Non è stato fatto.");

        await host.RunTurnAsync("registra la nota");

        LastInstruction(Coordinator(host)[1]).Should().Be(
            AnnouncedActionChatClient.Heading + MentorPromptTexts.For(MentorLanguage.Italian).AnnouncedActionNotRun);
    }

    [Fact]
    public async Task A_second_request_that_fails_leaves_the_turn_on_its_first_reply()
    {
        var classifier = Classifier("ANNOUNCED");
        await using var host = Host(classifier);
        host.Chat!.Say("Procedo a registrare la nota…").Respond((_, _) => throw new InvalidOperationException("down"));

        (await host.RunTurnAsync("registra la nota")).Should().BeNull("the user has the first reply");

        host.Recorder!.StreamedText.Should().Contain("Procedo a registrare la nota");
        host.Logs.WarningText.Should().Contain("second request after an announced action failed");
    }

    [Fact]
    public async Task A_stop_after_the_check_leaves_no_instruction_for_the_next_turn()
    {
        // The second request can end before it is sent: its instruction must not reach the next turn's first request.
        using var stop = new CancellationTokenSource();
        var classifier = new FakeChatClient();
        classifier.Respond((_, _) =>
        {
            stop.Cancel();
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "ANNOUNCED"));
        }).Say("NONE");
        await using var host = Host(classifier);
        host.Chat!.Say("Procedo…").Say("Ecco.");

        await host.RunTurnAsync("registra la nota", stop.Token);
        var before = Coordinator(host).Count;
        await host.RunTurnAsync("e adesso?");

        HasInstruction(Coordinator(host).Skip(before).Should().ContainSingle().Which).Should().BeFalse();
    }

    [Fact]
    public async Task A_second_request_that_fails_before_it_is_sent_leaves_no_instruction_for_the_next_turn()
    {
        // A host's middleware, outside the check's client, refuses the second request: the instruction is not taken,
        // and must not wait for the next turn's first request.
        var classifier = Classifier("ANNOUNCED", "NONE");
        var requests = 0;
        await using var host = Host(classifier, o => o.ConfigureChatClientPipeline = b => b.Use(
            async (messages, options, next, ct) =>
            {
                if (Interlocked.Increment(ref requests) == 2) throw new InvalidOperationException("refused");
                await next(messages, options, ct);
            }));
        host.Chat!.Say("Procedo…").Say("Ecco.");

        (await host.RunTurnAsync("registra la nota")).Should().BeNull();
        await host.RunTurnAsync("e adesso?");

        var coordinator = Coordinator(host);
        coordinator.Should().HaveCount(2, "the refused request never reached the model");
        HasInstruction(coordinator[1]).Should().BeFalse();
    }

    [Fact]
    public async Task The_instruction_is_the_last_message_of_the_request_never_of_the_callers_list()
    {
        var inner = new FakeChatClient().Say("ok");
        using var client = new AnnouncedActionChatClient(inner);
        var options = new ChatOptions { Instructions = "base" };
        List<ChatMessage> messages = [new ChatMessage(ChatRole.User, "hi")];

        client.Arm("## Heading\ntext");
        await client.GetResponseAsync(messages, options);

        inner.Calls[0].Messages.Select(m => (m.Role, m.Text)).Should()
            .Equal([(ChatRole.User, "hi"), (ChatRole.User, "## Heading\ntext")], "a user message: many templates refuse a system message that is not the first (BUG-231)");
        inner.Calls[0].Instructions.Should().Be("base");
        messages.Should().ContainSingle("the caller's list is its own: the function-invoking client keeps it, and stores none of it");
    }

    [Fact]
    public async Task With_the_history_kept_by_the_service_the_instruction_stays_in_the_instructions()
    {
        // Every message of a request with a ConversationId is stored on the service: a message there would stay in the
        // conversation (BUG-200).
        var inner = new FakeChatClient().Say("ok");
        using var client = new AnnouncedActionChatClient(inner);
        var options = new ChatOptions { Instructions = "base", ConversationId = "resp_1" };

        client.Arm("## Heading\ntext");
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options);

        inner.Calls[0].Messages.Should().ContainSingle();
        inner.Calls[0].Instructions.Should().Be("base\n\n## Heading\ntext");
        options.Instructions.Should().Be("base", "the caller's options are its own: the function-invoking client sends them again");
    }

    [Fact]
    public async Task The_call_of_the_second_request_passes_the_gate()
    {
        // issue_refund asks for a confirmation: the second request's call is shown to the user like any other.
        var classifier = Classifier("ANNOUNCED");
        await using var host = Host(classifier);
        host.Chat!.Say("Procedo con il rimborso…")
            .CallTool("issue_refund", new { orderId = 1001 }).Say("Non confermato.");
        var asked = 0;
        var state = host.Get<IMentorStateService>();
        state.OnConfirmationRequired += req =>
        {
            asked++;
            state.Cancel(req.ActionId);
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await host.RunTurnAsync("rimborsa l'ordine 1001", deadline.Token);

        asked.Should().Be(1, "the user is asked");
        host.Get<TestOrderService>().Invoked.Should().BeEmpty("and declined it");
    }

    // ── When nothing more happens ────────────────────────────────────────────────

    [Theory]
    [InlineData("NONE")]
    [InlineData("I think the reply is fine")]   // unreadable: no label first
    public async Task A_reply_the_classifier_does_not_flag_gets_no_second_request(string answer)
    {
        var classifier = Classifier(answer);
        await using var host = Host(classifier);
        host.Chat!.Say("Vuoi che registri la nota?").Say("unexpected");

        await host.RunTurnAsync("registra la nota");

        classifier.CallCount.Should().Be(1);
        Coordinator(host).Should().ContainSingle();
    }

    [Fact]
    public async Task A_classifier_that_fails_leaves_the_turn_as_it_is()
    {
        var classifier = new FakeChatClient();
        classifier.Respond((_, _) => throw new InvalidOperationException("down"));
        await using var host = Host(classifier);
        host.Chat!.Say("Procedo…").Say("unexpected");

        (await host.RunTurnAsync("registra la nota")).Should().BeNull();

        Coordinator(host).Should().ContainSingle();
    }

    [Fact]
    public async Task A_classifier_that_does_not_answer_in_time_leaves_the_turn_as_it_is()
    {
        await using var host = Host(classifier: null, o =>
        {
            o.ClassifierChatClient = new HangingChatClient();
            o.SafetyCheckTimeout = TimeSpan.FromMilliseconds(100);
        });
        host.Chat!.Say("Procedo…").Say("unexpected");

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        (await host.RunTurnAsync("registra la nota", deadline.Token)).Should().BeNull();

        Coordinator(host).Should().ContainSingle();
    }

    [Fact]
    public async Task A_turn_that_called_a_tool_is_not_checked()
    {
        var classifier = Classifier("ANNOUNCED");
        await using var host = Host(classifier);
        host.Chat!.CallTool("count_open_orders").Say("Ci sono 3 ordini aperti.")
            .Say("Procedo…").Say("Non è stato fatto.");

        await host.RunTurnAsync("quanti ordini sono aperti?");
        classifier.CallCount.Should().Be(0, "something ran: the reply reports it");

        await host.RunTurnAsync("registra la nota");
        classifier.CallCount.Should().Be(1, "the next turn, in which nothing ran, is checked");
    }

    [Fact]
    public async Task Without_a_classifier_client_the_question_goes_to_the_chat_client()
    {
        // On by default, with ClassifierChatClient ?? ChatClient: the protection does not depend on a second client.
        new MentorOptions { AppName = "x", ScanAssemblies = [] }.CheckAnnouncedActions.Should().BeTrue();
        await using var host = Host(classifier: null);
        host.Chat!.Say("Procedo…").Say("NONE");

        await host.RunTurnAsync("registra la nota");

        host.Chat.Calls.Should().HaveCount(2);
        host.Chat.Calls[1].Messages[0].Text.Should().Be(AnnouncedActionCheck.Question);
        host.Chat.Calls[1].Tools.Should().BeEmpty("the question offers no tool");
    }

    [Fact]
    [Trait("RuntimeOption", nameof(MentorOptions.CheckAnnouncedActions))]
    public async Task The_check_turned_off_while_the_application_runs_stops_from_the_next_turn()
    {
        var classifier = Classifier("NONE", "NONE");
        await using var host = Host(classifier);
        host.Chat!.SayRepeatedly("Ecco.", 4);

        await host.RunTurnAsync("ciao");
        classifier.CallCount.Should().Be(1);

        host.Options.CheckAnnouncedActions = false;
        await host.RunTurnAsync("ciao di nuovo");

        classifier.CallCount.Should().Be(1, "the next turn is not checked");
    }

    // ── The answer, read by position ─────────────────────────────────────────────

    [Theory]
    [InlineData("ANNOUNCED", nameof(AnnouncedAction.Announced))]
    [InlineData("**CLAIMED**", nameof(AnnouncedAction.Claimed))]
    [InlineData(" none.", nameof(AnnouncedAction.None))]
    [InlineData("ANNOUNCED - the reply says so", nameof(AnnouncedAction.Announced))]
    public void The_label_is_read_from_the_first_word(string answer, string expected)
        => AnnouncedActionCheck.Read(answer).ToString().Should().Be(expected);   // AnnouncedAction is internal

    [Theory]
    [InlineData("The reply is ANNOUNCED")]
    [InlineData("")]
    [InlineData(null)]
    public void A_label_that_is_not_the_first_word_is_not_read(string? answer)
        => AnnouncedActionCheck.Read(answer).Should().BeNull("a label is never searched for in the text (BUG-105)");

    /// <summary>A model that never answers: the check's timeout ends it.</summary>
    private sealed class HangingChatClient : IChatClient
    {
        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    // ── The tool the classifier names (C1) ───────────────────────────────────────

    [Fact]
    public async Task The_classifier_reads_the_tools_the_model_was_sent_and_the_one_it_names_is_named_to_the_model()
    {
        var classifier = Classifier("CLAIMED count_open_orders");
        await using var host = Host(classifier);
        host.Chat!.Say("Ci sono 3 ordini aperti.").CallTool("count_open_orders").Say("Ci sono 3 ordini aperti.");

        await host.RunTurnAsync("quanti ordini sono aperti?");

        classifier.Calls[0].Messages[1].Text.Should().Contain("\nTools: ").And.Contain("count_open_orders")
            .And.NotContain("export_ledger", "an action left out for this user was not sent to the model");
        LastInstruction(Coordinator(host)[1]).Should().Be(AnnouncedActionChatClient.Heading
            + MentorPromptTexts.English.AnnouncedActionCall.Replace("{tool}", "count_open_orders"));
        host.Get<TestOrderService>().Invoked.Should().Equal(nameof(TestOrderService.CountOpenOrders));
        host.Logs.Records.Should().Contain(r => r.Message.Contains("Announced-action check: CLAIMED count_open_orders"));
    }

    [Theory]
    [InlineData("CLAIMED export_ledger")]      // left out for a user who is not signed in: never sent to the model
    [InlineData("CLAIMED delete_everything")]  // no tool of the application
    [InlineData("CLAIMED")]                    // none named
    public async Task A_tool_the_model_was_not_sent_is_never_named_to_it(string verdict)
    {
        var classifier = Classifier(verdict);
        await using var host = Host(classifier);
        host.Chat!.Say("Il registro è stato esportato.").Say("Non è stato fatto.");

        await host.RunTurnAsync("esporta il registro");

        LastInstruction(Coordinator(host)[1]).Should().Be(Instruction,
            "without a tool it was sent, the model is told only that nothing was done");
        Instruction.Should().NotContain("cannot be done here", "the notice of an action left out says why, when it does");
    }

    [Theory]
    [InlineData("CLAIMED count_open_orders", "count_open_orders")]
    [InlineData("claimed `Count_Open_Orders`.", "count_open_orders")]
    [InlineData("CLAIMED - count_open_orders", "count_open_orders")]
    [InlineData("ANNOUNCED get-weather", "get-weather")]   // a dash inside an MCP server's name is part of it
    [InlineData("ANNOUNCED", null)]
    [InlineData("CLAIMED made_up_tool", null)]
    [InlineData("NONE", null)]
    public void The_tool_is_read_by_position_and_only_among_the_tools_sent(string answer, string? expected)
        => AnnouncedActionCheck.Tool(answer, ["count_open_orders", "get-weather"]).Should().Be(expected);

    [Fact]
    public async Task The_tools_the_classifier_reads_are_those_of_this_turn()
    {
        // An administrator's turn is sent export_ledger; signed out, the next turn is not, and it is not named to it.
        var classifier = Classifier("NONE", "CLAIMED export_ledger");
        await using var host = Host(classifier);
        host.Auth.SignIn("ada", "Admin");
        host.Chat!.Say("Ciao.").Say("Il registro è stato esportato.").Say("Non è stato fatto.");

        await host.RunTurnAsync("ciao");
        host.Auth.SignOut();
        await host.RunTurnAsync("esporta il registro");

        classifier.Calls[0].Messages[1].Text.Should().Contain("export_ledger");
        classifier.Calls[1].Messages[1].Text.Should().NotContain("export_ledger");
        LastInstruction(Coordinator(host)[^1]).Should().Be(Instruction);
    }

    // ── A call that runs nothing (C4) ────────────────────────────────────────────

    [Fact]
    public async Task A_call_to_an_action_its_agent_was_not_given_runs_nothing_and_leaves_the_reply_to_be_checked()
    {
        // archive_order ships off ([MentorAction(Enabled = false)]): the agent is not given it, and answers "not found".
        var classifier = Classifier("CLAIMED");
        await using var host = Host(classifier);
        host.Chat!.CallTool("archive_order", new { orderId = 3 }).Say("L'ordine 3 è archiviato.").Say("Non è stato fatto.");

        await host.RunTurnAsync("archivia l'ordine 3");

        host.Get<TestOrderService>().Invoked.Should().BeEmpty();
        classifier.CallCount.Should().Be(1, "nothing ran in the turn, so its reply is checked");
        host.Logs.Records.Should().Contain(r =>
            r.Message.Contains("The model called 'archive_order', which is not among the tools of this turn"));
    }

    [Fact]
    public async Task A_call_that_reached_the_gate_counts_even_when_the_gate_refused_it()
    {
        // export_ledger is the agent's, and the gate refuses it to a user who is not signed in: a decision was made.
        var classifier = Classifier("CLAIMED");
        await using var host = Host(classifier);
        host.Chat!.CallTool("export_ledger").Say("Non posso esportarlo senza accesso.");

        await host.RunTurnAsync("esporta il registro");

        classifier.CallCount.Should().Be(0, "the call reached the gate: the turn acted");
        host.Logs.Records.Should().NotContain(r => r.Message.Contains("not among the tools of this turn"));
    }
}
