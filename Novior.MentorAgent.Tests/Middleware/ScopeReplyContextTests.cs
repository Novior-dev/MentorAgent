using FluentAssertions;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.AI;
using Xunit;

namespace Novior.MentorAgent.Tests.Middleware;

/// <summary>
/// <c>RefuseOutOfScope</c> judges a short reply together with the assistant's question. The scope question saw the
/// message alone: on the demo the assistant closed with "Vuoi vedere i dettagli…?", the user answered "Sì", and the
/// turn was refused as out of scope, 5 times of 5 in Italian ("Yes" passed 3 of 3). The end of the assistant's last
/// reply now travels in the same call, inside the scope question; the safety question still judges the message
/// alone, and the verdict is kept for the message and that context together.
/// </summary>
public sealed class ScopeReplyContextTests
{
    private const string Question = "Ho trovato la spesa 1002 di Paolo Marino. Vuoi vedere i dettagli?";

    private static MentorHostFixture Host(FakeChatClient classifier, bool safety = true, Action<Novior.MentorAgent.Options.MentorOptions>? configure = null)
        => MentorHostFixture.Create(o =>
        {
            o.RefuseOutOfScope     = true;
            o.EnableSafetyCheck    = safety;
            o.MemoryAutoCapture    = false;
            o.ClassifierChatClient = classifier;
            configure?.Invoke(o);
        }, withRecorder: true);

    private static string Refused(MentorHostFixture host)
        => string.Join(" ", host.Recorder!.Log.Where(e => e.Event == "NotifyError").Select(e => e.Args.FirstOrDefault()?.ToString() ?? ""));

    /// <summary>The whole prompt a classifier call was given: its system text and its user message.</summary>
    private static string Prompt(FakeChatClient.RecordedCall call) => string.Join("\n", call.Messages.Select(m => m.Text));

    // ── The context travels with the scope question ─────────────────────────────

    [Fact]
    public async Task With_the_safety_check_on_the_scope_question_shows_the_end_of_the_previous_reply()
    {
        var classifier = new FakeChatClient().SayRepeatedly("SAFE ANSWER", 4);
        await using var host = Host(classifier);
        host.Chat!.Say(Question).Say("Ecco i dettagli.");

        await host.RunTurnAsync("Mostrami le spese");
        await host.RunTurnAsync("Sì");

        classifier.Calls.Should().HaveCount(2, "one call per turn answers both questions, as before");
        var second = classifier.Calls[1];
        var system = second.SystemText;
        system.Should().Contain("previous message").And.Contain("Vuoi vedere i dettagli?");
        system.IndexOf("Vuoi vedere i dettagli?", StringComparison.Ordinal)
            .Should().BeGreaterThan(system.IndexOf("SECOND", StringComparison.Ordinal),
                "the context belongs to the scope question; the safety question still judges the message alone");
        second.Messages.Last().Role.Should().Be(ChatRole.User);
        second.Messages.Last().Text.Should().Be("Sì");
        second.Messages.Should().NotContain(m => m.Role == ChatRole.Assistant, "the reply is data in the question, not a turn");
        Refused(host).Should().BeEmpty();
    }

    [Fact]
    public async Task With_the_safety_check_off_the_scope_call_shows_it_too()
    {
        var classifier = new FakeChatClient().SayRepeatedly("ANSWER", 4);
        await using var host = Host(classifier, safety: false);
        host.Chat!.Say(Question).Say("Ecco i dettagli.");

        await host.RunTurnAsync("Mostrami le spese");
        await host.RunTurnAsync("Sì");

        var prompt = Prompt(classifier.Calls[^1]);
        prompt.Should().Contain("previous message").And.Contain("Vuoi vedere i dettagli?");
        prompt.Should().EndWith("USER MESSAGE: Sì");
        prompt.IndexOf("Vuoi vedere i dettagli?", StringComparison.Ordinal)
            .Should().BeLessThan(prompt.IndexOf("Answer with ONE word only", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_first_turn_is_asked_as_before_and_the_context_is_all_that_changes()
    {
        var classifier = new FakeChatClient().SayRepeatedly("SAFE ANSWER", 4);
        await using var host = Host(classifier);
        host.Chat!.Say(Question).Say("Ecco i dettagli.");

        await host.RunTurnAsync("Sì");
        await host.RunTurnAsync("Sì");

        var first = classifier.Calls[0].SystemText;
        var second = classifier.Calls[1].SystemText;
        first.Should().NotContain("previous message", "there is nothing before the first message");
        var at = second.IndexOf("\nThe assistant's previous message", StringComparison.Ordinal);
        at.Should().BeGreaterThan(0);
        var end = second.IndexOf("is ANSWER.", at, StringComparison.Ordinal) + "is ANSWER.".Length;
        (second[..at] + second[end..]).Should().Be(first, "the prompt is the one before, with the context added");
    }

    [Fact]
    public async Task After_a_reset_there_is_no_context()
    {
        var classifier = new FakeChatClient().SayRepeatedly("SAFE ANSWER", 4);
        await using var host = Host(classifier);
        host.Chat!.Say(Question).Say("Ciao!");

        await host.RunTurnAsync("Mostrami le spese");
        await host.Orchestrator.ResetSessionAsync();
        await host.RunTurnAsync("Sì");

        classifier.Calls[^1].SystemText.Should().NotContain("previous message", "a new conversation has no previous reply");
    }

    // ── What the context is ─────────────────────────────────────────────────────

    [Fact]
    public async Task It_is_the_end_of_the_reply_on_one_line()
    {
        var reply = string.Concat(Enumerable.Repeat("Riga di dettaglio della spesa.\n", 40)) + "Vuoi\nprocedere?";
        var classifier = new FakeChatClient().SayRepeatedly("ANSWER", 4);
        await using var host = Host(classifier, safety: false);
        host.Chat!.Say(reply).Say("Fatto.");

        await host.RunTurnAsync("Mostrami le spese");
        await host.RunTurnAsync("Sì");

        var prompt = Prompt(classifier.Calls[^1]);
        prompt.Should().Contain("Vuoi procedere?", "the question closes the reply, so the end is kept, on one line");
        var start = prompt.IndexOf("ended: \"", StringComparison.Ordinal) + "ended: \"".Length;
        var stop = prompt.IndexOf("\".", start, StringComparison.Ordinal);
        var context = prompt[start..stop];
        context.Length.Should().BeLessThanOrEqualTo(301, "about 300 characters, with the ellipsis");
        context.Should().NotContain("\n");
    }

    [Fact]
    public async Task It_is_the_reply_not_the_sentence_written_before_a_tool_call()
    {
        var classifier = new FakeChatClient().SayRepeatedly("ANSWER", 4);
        await using var host = Host(classifier, safety: false);
        host.Chat!
            .Respond((_, _) => new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new TextContent("Controllo subito."), new FunctionCallContent("call_1", "count_open_orders")])))
            .Say("Ci sono 3 ordini aperti. Vuoi l'elenco?")
            .Say("Ecco l'elenco.");

        await host.RunTurnAsync("Quanti ordini sono aperti?");
        await host.RunTurnAsync("Sì");

        var prompt = Prompt(classifier.Calls[^1]);
        prompt.Should().Contain("Vuoi l'elenco?").And.NotContain("Controllo subito");
    }

    [Fact]
    public async Task Under_service_managed_history_the_context_is_there_too()
    {
        var classifier = new FakeChatClient().SayRepeatedly("ANSWER", 4);
        await using var host = Host(classifier, safety: false, o => o.UseServiceManagedHistory = true);
        host.Chat!
            .Respond((_, _) => new ChatResponse(new ChatMessage(ChatRole.Assistant, Question)) { ConversationId = "resp_1" })
            .Say("Ecco i dettagli.");

        await host.RunTurnAsync("Mostrami le spese");
        await host.RunTurnAsync("Sì");

        Prompt(classifier.Calls[^1]).Should().Contain("Vuoi vedere i dettagli?", "the reply is taken from the stream, not from a local history");
    }

    // ── The verdict and a refused turn ──────────────────────────────────────────

    [Fact]
    public async Task The_same_reply_after_another_question_is_asked_again()
    {
        // Without the safety check the verdict was kept by the message alone: a "Sì" judged once was judged the
        // same way after any later question.
        var classifier = new FakeChatClient().Say("ANSWER").Say("ANSWER").Say("REFUSE");
        await using var host = Host(classifier, safety: false);
        host.Chat!.Say(Question).Say("Ecco i dettagli. Vuoi anche approvarla?").Say("Approvata.");

        await host.RunTurnAsync("Mostrami le spese");
        await host.RunTurnAsync("Sì");
        await host.RunTurnAsync("Sì");

        classifier.Calls.Should().HaveCount(3, "the second \"Sì\" answers another question, and is judged again");
        Prompt(classifier.Calls[2]).Should().Contain("Vuoi anche approvarla?");
        Refused(host).Should().NotBeEmpty("the classifier's new verdict is the one that counts");
    }

    [Fact]
    public async Task A_refusal_given_after_another_question_is_never_applied()
    {
        // "Sì" refused after the first question; then a message the classifier cannot read is answered, and a new
        // question asked; the next "Sì", read unreadable again, must not be refused by the verdict of the first one.
        var classifier = new FakeChatClient().Say("ANSWER").Say("REFUSE").Say("boh").Say("boh");
        await using var host = Host(classifier, safety: false);
        host.Chat!.Say(Question).Say("Ecco la spesa 1002. Vuoi approvarla?").Say("Approvata.");

        await host.RunTurnAsync("Mostrami le spese");
        await host.RunTurnAsync("Sì");
        Refused(host).Should().NotBeEmpty("the first \"Sì\" was refused");
        var before = host.Recorder!.Log.Count(e => e.Event == "NotifyError");

        await host.RunTurnAsync("Mostrami la spesa 1002");
        await host.RunTurnAsync("Sì");

        host.Recorder.Log.Count(e => e.Event == "NotifyError").Should().Be(before, "an unreadable answer is answered");
        host.Chat.Calls.Last().Messages.Last().Text.Should().Be("Sì");
    }

    [Fact]
    public async Task After_a_refused_turn_the_context_is_still_the_reply_before_it()
    {
        var classifier = new FakeChatClient().Say("ANSWER").Say("REFUSE").Say("ANSWER");
        await using var host = Host(classifier, safety: false);
        host.Chat!.Say(Question).Say("Ecco i dettagli.");

        await host.RunTurnAsync("Mostrami le spese");
        await host.RunTurnAsync("Scrivimi una poesia sul mare");
        await host.RunTurnAsync("Sì");

        Refused(host).Should().NotBeEmpty("the poem was refused");
        var prompt = Prompt(classifier.Calls[2]);
        prompt.Should().Contain("Vuoi vedere i dettagli?", "a refused turn has no reply: the conversation still ends on the question");
        prompt.Should().EndWith("USER MESSAGE: Sì");
        host.Chat.Calls.Last().Messages.Last().Text.Should().Be("Sì", "and the \"Sì\" was answered");
    }
}
