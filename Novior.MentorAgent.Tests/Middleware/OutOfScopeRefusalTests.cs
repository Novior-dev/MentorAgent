using FluentAssertions;
using Novior.MentorAgent.Abstractions.Core;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Xunit;

namespace Novior.MentorAgent.Tests.Middleware;

/// <summary>
/// <c>RefuseOutOfScope</c> — the option that turns a verdict MentorAgent already has into an answer.
///
/// <para>
/// Found live on rc.4: asked for a poem about the sea, a shop assistant wrote one. The library had
/// classified that very message as OUT of scope moments earlier — the hosted-tool gate does it on
/// every turn where a provider tool is in play — and used the verdict only to withhold the tools.
/// </para>
///
/// <para>
/// Off by default: answering anything is the right behaviour for a general-purpose assistant, and a
/// refusal the host did not ask for is worse than an off-topic answer.
/// </para>
/// </summary>
public sealed class OutOfScopeRefusalTests
{
    private static string Refused(MentorHostFixture host)
        => string.Join(" ", host.Recorder!.Log
            .Where(e => e.Event == "NotifyError")
            .Select(e => e.Args.FirstOrDefault()?.ToString() ?? ""));

    private static MentorHostFixture Host(bool refuse, FakeChatClient classifier, bool safety = true)
        => MentorHostFixture.Create(o =>
        {
            o.RefuseOutOfScope     = refuse;
            o.EnableSafetyCheck    = safety;
            o.MemoryAutoCapture    = false;
            o.ClassifierChatClient = classifier;
        }, withRecorder: true);

    [Fact]
    public async Task An_out_of_scope_message_is_refused_without_reaching_the_model()
    {
        var classifier = new FakeChatClient();
        classifier.SayRepeatedly("SAFE REFUSE", 4);

        await using var host = Host(refuse: true, classifier);
        host.Chat!.SayRepeatedly("una poesia", 4);

        await host.RunTurnAsync("scrivimi una poesia sul mare");

        host.Chat.CallCount.Should().Be(0, "the coordinator is never called, so the turn costs nothing");
        Refused(host).Should().Be(new MentorLocalizer(host.Options.Language).Get("guardrail.outofscope"),
            "and the user is told, in their language, that they asked the wrong assistant");
    }

    [Fact]
    public async Task An_in_scope_message_is_answered_as_usual()
    {
        var classifier = new FakeChatClient();
        classifier.SayRepeatedly("SAFE ANSWER", 4);

        await using var host = Host(refuse: true, classifier);
        host.Chat!.SayRepeatedly("ci sono 3 ordini aperti", 4);

        await host.RunTurnAsync("quanti ordini sono aperti?");

        host.Chat.CallCount.Should().BeGreaterThan(0);
        Refused(host).Should().BeEmpty();
    }

    [Fact]
    public async Task With_the_option_off_the_poem_still_gets_written()
    {
        // The control, and the default: nothing changes for a host that never sets this.
        var classifier = new FakeChatClient();
        classifier.SayRepeatedly("SAFE OUT", 4);

        await using var host = Host(refuse: false, classifier);
        host.Chat!.SayRepeatedly("una poesia", 4);

        await host.RunTurnAsync("scrivimi una poesia sul mare");

        host.Chat.CallCount.Should().BeGreaterThan(0);
        Refused(host).Should().BeEmpty();
    }

    [Fact]
    public async Task The_verdict_costs_one_call_when_the_safety_check_is_off()
    {
        // Alone, the option pays for its own question — but only one, and only a few tokens.
        var classifier = new FakeChatClient();
        classifier.SayRepeatedly("REFUSE", 4);

        await using var host = Host(refuse: true, classifier, safety: false);
        host.Chat!.SayRepeatedly("una poesia", 4);

        await host.RunTurnAsync("scrivimi una poesia sul mare");

        classifier.CallCount.Should().Be(1);
        host.Chat.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Both_questions_still_travel_together_when_the_safety_check_is_on()
    {
        var classifier = new FakeChatClient();
        classifier.SayRepeatedly("SAFE REFUSE", 4);

        await using var host = Host(refuse: true, classifier);
        host.Chat!.SayRepeatedly("x", 4);

        await host.RunTurnAsync("scrivimi una poesia sul mare");

        classifier.CallCount.Should().Be(1, "one call answered both questions");
    }

    [Fact]
    public async Task A_classifier_outage_lets_the_message_through()
    {
        // Fail open, like every other gate in the library: an assistant that refuses everything
        // because a classifier is down is worse than one that answers an off-topic question.
        var classifier = new FakeChatClient { ThrowOnNextCall = new HttpRequestException("classifier down") };
        classifier.SayRepeatedly("REFUSE", 4);

        await using var host = Host(refuse: true, classifier, safety: false);
        host.Chat!.SayRepeatedly("una poesia", 4);

        await host.RunTurnAsync("scrivimi una poesia sul mare");

        host.Chat.CallCount.Should().BeGreaterThan(0, "the turn went ahead");
        Refused(host).Should().BeEmpty();
    }

    // ── BUG-105: a refusal question of its own ─────────────────────────────────
    //
    // The refusal judged every message against the sentence written for the hosted-tool COST gate
    // (BUG-050: "OUT when the subject is anything else, even if one of those capabilities could technically
    // produce it"), where a false OUT only withholds a web search — here it ends the turn. And the
    // classifier saw page names, never what the application holds, so "Quanto costa il Dell XPS 15?" read
    // as general knowledge: 4 of 4 catalogue price questions refused on the published rc.13.

    /// <summary>RefuseOutOfScope on a host that also has hosted tools behind the cost gate.</summary>
    private static MentorHostFixture GatedHost(FakeChatClient classifier, bool safety = true)
        => MentorHostFixture.Create(o =>
        {
            o.RefuseOutOfScope      = true;
            o.EnableSafetyCheck     = safety;
            o.MemoryAutoCapture     = false;
            o.ClassifierChatClient  = classifier;
            o.EnableToolFiltering   = true;
            o.FilterHostedTools     = false;   // hosted tools go straight to the cost gate
            o.HostedTools           = Novior.MentorAgent.Abstractions.Models.MentorHostedTools.WebSearch
                                      | Novior.MentorAgent.Abstractions.Models.MentorHostedTools.CodeInterpreter;
            o.HostedToolDomainCheck = true;
        }, withEmbeddings: true, withRecorder: true);

    private static void AssertTheRefusalQuestion(string prompt)
    {
        prompt.Should().Contain("ANSWER or REFUSE").And.Contain("If unsure, ANSWER")
            .And.Contain("you cannot see them",
                "a name the classifier does not recognise may be one of the application's own records");
        prompt.Should().Contain("Orders (List and manage customer orders)",
            "the classifier is told what each section holds, not only its name");
        prompt.Should().NotContain("could technically produce it",
            "the hosted-tool cost rule is the gate's, not the refusal's");
        prompt.Should().NotContain("IN or OUT");
    }

    [Fact]
    public async Task The_refusal_question_defaults_to_answering_and_carries_no_hosted_tool_rule()
    {
        var classifier = new FakeChatClient();
        classifier.Say("SAFE ANSWER").Say("IN");

        await using var host = GatedHost(classifier);
        host.Chat!.SayRepeatedly("Il Dell XPS 15 costa 1.899,99 €.", 2);

        await host.RunTurnAsync("Quanto costa il Dell XPS 15?");

        AssertTheRefusalQuestion(classifier.Calls[0].Messages[0].Text ?? "");
    }

    [Fact]
    public async Task The_refusal_question_is_the_same_when_the_safety_check_is_off()
    {
        var classifier = new FakeChatClient();
        classifier.Say("ANSWER").Say("IN");

        await using var host = GatedHost(classifier, safety: false);
        host.Chat!.SayRepeatedly("Il Dell XPS 15 costa 1.899,99 €.", 2);

        await host.RunTurnAsync("Quanto costa il Dell XPS 15?");

        var prompt = classifier.Calls[0].Messages[0].Text ?? "";
        AssertTheRefusalQuestion(prompt);
        prompt.Should().EndWith("USER MESSAGE: Quanto costa il Dell XPS 15?");
    }

    [Fact]
    public async Task A_refusal_ends_the_turn_before_any_hosted_tool_question()
    {
        var classifier = new FakeChatClient();
        classifier.SayRepeatedly("SAFE REFUSE", 2);

        await using var host = GatedHost(classifier);
        host.Chat!.SayRepeatedly("una poesia", 2);

        await host.RunTurnAsync("scrivimi una poesia sul mare");

        host.Chat.CallCount.Should().Be(0);
        classifier.CallCount.Should().Be(1, "a refused turn never reaches the cost gate");
        Refused(host).Should().Be(new MentorLocalizer(host.Options.Language).Get("guardrail.outofscope"));
    }

    [Fact]
    public async Task An_answered_message_still_meets_the_hosted_tool_gate()
    {
        // Answering the question does not buy it a web search: the cost gate asks its own, strict question,
        // and only on a turn where a hosted tool is about to be declared.
        var classifier = new FakeChatClient();
        classifier.Say("SAFE ANSWER").Say("OUT");

        await using var host = GatedHost(classifier);
        host.Chat!.SayRepeatedly("ok", 2);

        await host.RunTurnAsync("genera immagine di un cane");

        host.Chat.CallCount.Should().BeGreaterThan(0);
        Refused(host).Should().BeEmpty();
        classifier.CallCount.Should().Be(2);
        (classifier.Calls[1].Messages.LastOrDefault()?.Text ?? "").Should().Contain("could technically produce it",
            "the gate keeps its BUG-050 wording");
        host.Chat.Calls[0].HostedToolNames.Should().BeEmpty();
    }

    [Fact]
    public async Task An_explanation_after_the_label_is_not_read_as_a_refusal()
    {
        // The combined reply was read with Contains("OUT"), which matches "about" and "without".
        var classifier = new FakeChatClient();
        classifier.Say("SAFE ANSWER, without doubt it is about a product");

        await using var host = Host(refuse: true, classifier);
        host.Chat!.SayRepeatedly("1.899,99 €", 2);

        await host.RunTurnAsync("Quanto costa il Dell XPS 15?");

        host.Chat.CallCount.Should().BeGreaterThan(0);
        Refused(host).Should().BeEmpty();
    }

    [Fact]
    public async Task An_unreadable_label_is_answered_and_logged()
    {
        var classifier = new FakeChatClient();
        classifier.Say("Sure, it is fine");

        await using var host = Host(refuse: true, classifier);
        host.Chat!.SayRepeatedly("ok", 2);

        await host.RunTurnAsync("Quanto costa il Dell XPS 15?");

        host.Chat.CallCount.Should().BeGreaterThan(0, "a refusal needs a clear REFUSE");
        host.Logs.Records.Should().Contain(r => r.Message.Contains("classifier said 'Sure, it is fine'"));
    }

    [Fact]
    public async Task A_refusal_is_counted_as_a_guardrail_block()
    {
        // The refusal counted nowhere: an operator could not see how often it fired.
        var classifier = new FakeChatClient();
        classifier.SayRepeatedly("SAFE REFUSE", 2);

        await using var host = Host(refuse: true, classifier);
        host.Chat!.SayRepeatedly("una poesia", 2);

        await host.RunTurnAsync("scrivimi una poesia sul mare");

        var metrics = host.Get<Novior.MentorAgent.Core.MentorMetrics>().GetSnapshot();
        metrics.GuardrailBlockCount.Should().Be(1);
        metrics.TurnCount.Should().Be(0, "like a message the safety check refuses, it is not a turn served");
    }

    [Fact]
    public async Task The_combined_call_keeps_its_request_shape()
    {
        // No output cap and no temperature on the safety call, as in rc.13: on a reasoning deployment a
        // small cap can be spent before the label is written, and an empty reply reads as SAFE.
        var classifier = new FakeChatClient();
        classifier.SayRepeatedly("SAFE ANSWER", 2);

        await using var host = Host(refuse: true, classifier);
        host.Chat!.SayRepeatedly("ok", 2);

        await host.RunTurnAsync("quanti ordini sono aperti?");

        classifier.Calls[0].MaxOutputTokens.Should().BeNull();
        classifier.Calls[0].Temperature.Should().BeNull();
    }

    [Fact]
    public async Task The_standalone_check_is_bounded_and_a_timeout_is_not_remembered()
    {
        // Review of the rc.14 fix: the stand-alone call had no SafetyCheckTimeout, and a failure was cached
        // as ANSWER for that message.
        var classifier = new FakeChatClient { BeforeRespond = () => Task.Delay(1500) };
        classifier.SayRepeatedly("REFUSE", 4);

        await using var host = MentorHostFixture.Create(o =>
        {
            o.RefuseOutOfScope     = true;
            o.EnableSafetyCheck    = false;
            o.MemoryAutoCapture    = false;
            o.ClassifierChatClient = classifier;
            o.SafetyCheckTimeout   = TimeSpan.FromMilliseconds(100);
        }, withRecorder: true);
        host.Chat!.SayRepeatedly("ok", 4);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await host.RunTurnAsync("scrivimi una poesia sul mare");
        await host.RunTurnAsync("scrivimi una poesia sul mare");

        host.Chat.CallCount.Should().BeGreaterThan(0, "no verdict: the message is answered (fail open)");
        classifier.CallCount.Should().Be(2, "a timeout is not a verdict, so the same text is asked about again");
        host.Logs.HasWarning("Out-of-scope check timed out").Should().BeTrue();
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Stop_during_the_standalone_check_ends_the_turn_without_counting_it()
    {
        var classifier = new FakeChatClient();
        classifier.SayRepeatedly("ANSWER", 4);

        await using var host = MentorHostFixture.Create(o =>
        {
            o.RefuseOutOfScope     = true;
            o.EnableSafetyCheck    = false;
            o.MemoryAutoCapture    = false;
            o.ClassifierChatClient = classifier;
        }, withRecorder: true);
        classifier.BeforeRespond = () => { host.Orchestrator.CancelCurrentRequest(); return Task.CompletedTask; };
        host.Chat!.SayRepeatedly("ok", 4);

        await host.RunTurnAsync("quanti ordini sono aperti?");

        host.Chat.CallCount.Should().Be(0);
        host.Get<Novior.MentorAgent.Core.MentorMetrics>().GetSnapshot().TurnCount.Should().Be(0);
        host.Logs.HasWarning("Out-of-scope check").Should().BeFalse("Stop is not a failure");
    }
}
