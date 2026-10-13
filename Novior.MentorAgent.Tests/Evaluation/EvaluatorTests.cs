using FluentAssertions;
using Novior.MentorAgent.Evaluation;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.AI;
using Xunit;

namespace Novior.MentorAgent.Tests.Evaluation;

/// <summary>
/// The evaluation harness (#10) — the thing a CI pipeline gates on.
///
/// <para>
/// Its whole value is the verdict, so a harness that passes when it should fail is worse than no
/// harness: it converts an unreviewed prompt change into a green build. Every gate here is
/// therefore tested from both sides, and each boundary is tested <em>at</em> the limit as well as
/// past it — "exceeds the budget" and "reaches the budget" are one character apart in the source
/// and a whole release apart in consequence.
/// </para>
///
/// <para>
/// Scope, deliberately: MentorAgent's own loop, gates and judge parsing. The optional native
/// evaluators are the Agent Framework's <c>LocalEvaluator</c> / <c>agent.EvaluateAsync</c>, and
/// testing those would be testing someone else's code — the same line B3b drew around compaction.
/// What is pinned here is that a native hiccup cannot take the core gates down with it.
/// </para>
/// </summary>
public sealed class EvaluatorTests
{
    private const string JudgePromptMarker = "Rate from 1 to 5";

    /// <summary>Replies to the judge with <paramref name="verdict"/> and to everything else with text.</summary>
    private static void Script(FakeChatClient chat, string reply, string verdict = "5", long input = 10, long output = 5)
        => chat.Respond((messages, _) =>
        {
            var isJudge = messages.Any(m => m.Text?.Contains(JudgePromptMarker, StringComparison.Ordinal) == true);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, isJudge ? verdict : reply))
            {
                Usage = isJudge ? null : new UsageDetails { InputTokenCount = input, OutputTokenCount = output },
            };
        });

    private static async Task<MentorEvalReport> RunAsync(
        MentorHostFixture host, IEnumerable<EvalCase> cases, MentorEvalOptions? options = null)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        return await host.Get<MentorEvaluator>().RunAsync(cases, options, deadline.Token);
    }

    private static MentorEvalResult Result(bool passed, double? quality = null, long input = 0, long output = 0)
        => new() { Input = "i", Output = "o", Passed = passed, QualityScore = quality,
                   InputTokens = input, OutputTokens = output };

    private static MentorEvalReport Report(params MentorEvalResult[] results)
        => new() { Results = results, Passed = results.All(r => r.Passed) };

    // ── The report, as pure arithmetic ──────────────────────────────────────────

    [Fact]
    public void An_empty_run_passes_rather_than_dividing_by_zero()
    {
        // A suite with no cases is a configuration mistake, not a regression. Reporting 0% would
        // fail the build for the wrong reason, and NaN would fail a comparison in an unreadable way.
        Report().PassRate.Should().Be(1);
    }

    [Fact]
    public void The_pass_rate_is_the_share_of_cases_that_passed()
    {
        Report(Result(true), Result(true), Result(false), Result(false)).PassRate.Should().Be(0.5);
    }

    [Fact]
    public void Average_quality_is_null_when_nothing_was_judged()
    {
        // Not zero. Zero would read as "the answers were terrible" when nobody looked at them.
        Report(Result(true), Result(true)).AverageQuality.Should().BeNull();
    }

    [Fact]
    public void Average_quality_ignores_the_cases_that_were_not_judged()
    {
        // Only cases with an expectation get a score. Counting the rest as zero would drag the
        // average down in proportion to how few expectations were written.
        Report(Result(true, quality: 1.0), Result(true, quality: 0.6), Result(true))
            .AverageQuality.Should().BeApproximately(0.8, 1e-9);
    }

    [Fact]
    public void Total_tokens_sum_input_and_output_across_every_case()
    {
        Report(Result(true, input: 100, output: 50), Result(true, input: 10, output: 5))
            .TotalTokens.Should().Be(165);
    }

    [Fact]
    public void A_failed_report_throws_with_the_reason_it_failed()
    {
        // This is the assertion a CI step runs. The message is the whole diagnostic — a bare
        // "evaluation failed" sends somebody to re-run it locally to find out why.
        var report = new MentorEvalReport
        {
            Results = [], Passed = false, FailureReason = "total tokens 900 exceed budget 500",
        };

        var act = report.ThrowIfFailed;

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*total tokens 900 exceed budget 500*");
    }

    [Fact]
    public void A_failed_report_with_no_reason_still_says_something_useful()
    {
        var act = new MentorEvalReport { Results = [], Passed = false }.ThrowIfFailed;

        act.Should().Throw<InvalidOperationException>().WithMessage("*one or more cases failed*");
    }

    [Fact]
    public void A_passing_report_throws_nothing()
    {
        var act = Report(Result(true)).ThrowIfFailed;

        act.Should().NotThrow();
    }

    // ── Running the cases ───────────────────────────────────────────────────────

    [Fact]
    public async Task Evaluation_needs_a_chat_client_and_says_so()
    {
        // It calls the model directly rather than through the coordinator, so a pre-built agent
        // cannot serve it. Failing with a message that names the path beats a NullReferenceException.
        var inner = new FakeChatClient();
        await using var host = MentorHostFixture.Create(o =>
        {
            o.ChatClient = null;
            o.Agent = inner.AsAIAgent(instructions: "test");
        }, withChatClient: false);

        var act = async () => await RunAsync(host, [new EvalCase("hello")]);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*requires a ChatClient*");
    }

    [Fact]
    public async Task A_run_with_no_cases_passes_without_calling_the_model()
    {
        await using var host = MentorHostFixture.Create();

        var report = await RunAsync(host, []);

        report.Passed.Should().BeTrue();
        report.Results.Should().BeEmpty();
        host.Chat!.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Each_case_is_one_call_carrying_its_input()
    {
        await using var host = MentorHostFixture.Create();
        Script(host.Chat!, "an answer");

        await RunAsync(host, [new EvalCase("first"), new EvalCase("second")]);

        host.Chat!.CallCount.Should().Be(2);
        host.Chat.Calls.Select(c => c.LastUserText).Should().Equal("first", "second");
    }

    [Fact]
    public async Task The_system_instructions_under_test_are_sent_with_every_case()
    {
        // The point of the harness: it is the *prompt* being evaluated, so it has to be the prompt
        // that is sent — on every case, not only the first.
        await using var host = MentorHostFixture.Create();
        Script(host.Chat!, "an answer");

        await RunAsync(host, [new EvalCase("first"), new EvalCase("second")],
            new MentorEvalOptions { SystemInstructions = "You are a terse mentor." });

        host.Chat!.Calls.Should().OnlyContain(c => c.SystemText.Contains("You are a terse mentor."));
    }

    [Fact]
    public async Task Without_instructions_no_system_message_is_invented()
    {
        await using var host = MentorHostFixture.Create();
        Script(host.Chat!, "an answer");

        await RunAsync(host, [new EvalCase("first")]);

        host.Chat!.Calls[0].SystemText.Should().BeEmpty();
    }

    [Fact]
    public async Task The_providers_own_token_counts_are_recorded_per_case()
    {
        // Read from response.Usage rather than estimated, which is what makes the budget gate
        // meaningful as a regression signal.
        await using var host = MentorHostFixture.Create();
        Script(host.Chat!, "an answer", input: 123, output: 45);

        var report = await RunAsync(host, [new EvalCase("first")]);

        var result = report.Results.Single();
        result.InputTokens.Should().Be(123);
        result.OutputTokens.Should().Be(45);
        result.TotalTokens.Should().Be(168);
    }

    [Fact]
    public async Task A_provider_that_reports_no_usage_counts_zero_rather_than_failing()
    {
        await using var host = MentorHostFixture.Create();
        host.Chat!.Respond((_, _) => new ChatResponse(new ChatMessage(ChatRole.Assistant, "an answer")));

        var report = await RunAsync(host, [new EvalCase("first")]);

        report.Results.Single().TotalTokens.Should().Be(0);
        report.Passed.Should().BeTrue("no usage is not a failure, only an unmeasured case");
    }

    // ── The built-in check ──────────────────────────────────────────────────────

    [Fact]
    public async Task An_empty_answer_fails_the_case()
    {
        await using var host = MentorHostFixture.Create();
        Script(host.Chat!, "");

        var report = await RunAsync(host, [new EvalCase("first")]);

        report.Passed.Should().BeFalse();
        report.Results.Single().Notes.Should().Contain("empty response");
    }

    [Fact]
    public async Task A_whitespace_only_answer_counts_as_empty()
    {
        await using var host = MentorHostFixture.Create();
        Script(host.Chat!, "   \n  ");

        (await RunAsync(host, [new EvalCase("first")])).Passed.Should().BeFalse();
    }

    // ── Token budgets ───────────────────────────────────────────────────────────

    [Fact]
    public async Task A_case_over_its_own_budget_fails_and_names_the_numbers()
    {
        await using var host = MentorHostFixture.Create();
        Script(host.Chat!, "an answer", input: 100, output: 50);

        var report = await RunAsync(host, [new EvalCase("first", MaxTokens: 100)]);

        report.Passed.Should().BeFalse();
        report.Results.Single().Notes.Should().Contain("150").And.Contain("100");
    }

    [Fact]
    public async Task A_case_exactly_at_its_budget_passes()
    {
        // The gate is "exceeds", not "reaches". One character in the source, and getting it wrong
        // fails every suite whose budget was set from a measured run.
        await using var host = MentorHostFixture.Create();
        Script(host.Chat!, "an answer", input: 100, output: 50);

        (await RunAsync(host, [new EvalCase("first", MaxTokens: 150)])).Passed.Should().BeTrue();
    }

    [Fact]
    public async Task A_run_over_the_total_budget_fails_even_when_every_case_passed()
    {
        // The token-regression gate. Each case can be individually reasonable while the suite as a
        // whole has drifted — that drift is exactly what this catches.
        await using var host = MentorHostFixture.Create();
        Script(host.Chat!, "an answer", input: 100, output: 50);

        var report = await RunAsync(host,
            [new EvalCase("first"), new EvalCase("second")],
            new MentorEvalOptions { MaxTotalTokens = 200 });

        report.Results.Should().OnlyContain(r => r.Passed);
        report.Passed.Should().BeFalse();
        report.FailureReason.Should().Contain("300").And.Contain("200");
    }

    [Fact]
    public async Task A_run_exactly_at_the_total_budget_passes()
    {
        await using var host = MentorHostFixture.Create();
        Script(host.Chat!, "an answer", input: 100, output: 50);

        var report = await RunAsync(host,
            [new EvalCase("first"), new EvalCase("second")],
            new MentorEvalOptions { MaxTotalTokens = 300 });

        report.Passed.Should().BeTrue();
    }

    [Fact]
    public async Task One_failing_case_fails_the_whole_report()
    {
        await using var host = MentorHostFixture.Create();
        host.Chat!.Say("fine").Say("").Say("fine");

        var report = await RunAsync(host,
            [new EvalCase("a"), new EvalCase("b"), new EvalCase("c")]);

        report.Passed.Should().BeFalse();
        report.PassRate.Should().BeApproximately(2d / 3, 1e-9);
        report.FailureReason.Should().NotBeNullOrWhiteSpace();
    }

    // ── The built-in judge ──────────────────────────────────────────────────────

    [Fact]
    public async Task Nothing_is_judged_unless_the_judge_is_switched_on()
    {
        // The judge is a second model call per case. Running it unasked would silently double the
        // cost of every evaluation.
        await using var host = MentorHostFixture.Create();
        Script(host.Chat!, "an answer");

        var report = await RunAsync(host, [new EvalCase("first", Expectation: "should be polite")]);

        report.Results.Single().QualityScore.Should().BeNull();
        host.Chat!.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task A_case_with_no_expectation_is_not_judged_even_when_the_judge_is_on()
    {
        // There is nothing to judge it against, and a score invented from no criterion would be
        // averaged into the quality gate as though it meant something.
        await using var host = MentorHostFixture.Create();
        Script(host.Chat!, "an answer");

        var report = await RunAsync(host, [new EvalCase("first")], new MentorEvalOptions { Judge = true });

        report.Results.Single().QualityScore.Should().BeNull();
        host.Chat!.CallCount.Should().Be(1);
    }

    [Theory]
    [InlineData("5", 1.0)]
    [InlineData("4", 0.8)]
    [InlineData("3", 0.6)]
    [InlineData("2", 0.4)]
    [InlineData("1", 0.2)]
    public async Task A_verdict_between_one_and_five_becomes_a_score_between_zero_and_one(
        string verdict, double expected)
    {
        await using var host = MentorHostFixture.Create();
        Script(host.Chat!, "an answer", verdict: verdict);

        var report = await RunAsync(host,
            [new EvalCase("first", Expectation: "should be polite")],
            new MentorEvalOptions { Judge = true });

        report.Results.Single().QualityScore.Should().BeApproximately(expected, 1e-9);
    }

    [Fact]
    public async Task A_chatty_verdict_is_still_read()
    {
        // Models add words however firmly you ask them not to. Taking the first digit is what makes
        // "I'd say 4 out of 5" usable instead of unscored.
        await using var host = MentorHostFixture.Create();
        Script(host.Chat!, "an answer", verdict: "I'd say 4 out of 5.");

        var report = await RunAsync(host,
            [new EvalCase("first", Expectation: "should be polite")],
            new MentorEvalOptions { Judge = true });

        report.Results.Single().QualityScore.Should().BeApproximately(0.8, 1e-9);
    }

    [Theory]
    [InlineData("excellent")]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("9")]
    public async Task A_verdict_that_is_not_on_the_scale_scores_nothing(string verdict)
    {
        // Null rather than zero, again. An unreadable verdict means the judge did not answer, not
        // that the answer was bad — and zero would drag the average gate below its threshold.
        await using var host = MentorHostFixture.Create();
        Script(host.Chat!, "an answer", verdict: verdict);

        var report = await RunAsync(host,
            [new EvalCase("first", Expectation: "should be polite")],
            new MentorEvalOptions { Judge = true });

        report.Results.Single().QualityScore.Should().BeNull();
    }

    [Fact]
    public async Task A_separate_judge_client_is_used_when_one_is_supplied()
    {
        // Judging with the model under test is a conflict of interest, and the documented way out
        // is a second client. If it were ignored, the option would look configured and do nothing.
        var judge = new FakeChatClient();
        judge.Say("5");

        await using var host = MentorHostFixture.Create();
        Script(host.Chat!, "an answer", verdict: "1");

        var report = await RunAsync(host,
            [new EvalCase("first", Expectation: "should be polite")],
            new MentorEvalOptions { Judge = true, JudgeClient = judge });

        judge.CallCount.Should().Be(1);
        report.Results.Single().QualityScore.Should().BeApproximately(1.0, 1e-9);
    }

    // ── The quality gate ────────────────────────────────────────────────────────

    [Fact]
    public async Task A_case_below_the_quality_floor_fails_and_says_by_how_much()
    {
        // BUG-020. The note is a CI diagnostic, so its numbers are formatted invariantly — under an
        // Italian machine culture this read "quality 0,40 < 0,80", which means the same failure
        // prints differently depending on where the build ran.
        await using var host = MentorHostFixture.Create();
        Script(host.Chat!, "an answer", verdict: "2");

        var report = await RunAsync(host,
            [new EvalCase("first", Expectation: "should be polite")],
            new MentorEvalOptions { Judge = true, MinQuality = 0.8 });

        report.Passed.Should().BeFalse();
        report.Results.Single().Notes.Should().Contain("0.40").And.Contain("0.80");
    }

    [Fact]
    public async Task The_average_quality_failure_reason_is_locale_independent_too()
    {
        // The same rule on the aggregate gate, which is the string ThrowIfFailed puts into an
        // exception message — and .NET exception messages are invariant by convention.
        await using var host = MentorHostFixture.Create();
        var verdicts = new Queue<string>(["1", "1"]);
        host.Chat!.Respond((messages, _) =>
        {
            var isJudge = messages.Any(m => m.Text?.Contains(JudgePromptMarker, StringComparison.Ordinal) == true);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant,
                isJudge ? verdicts.Dequeue() : "an answer"));
        });

        var report = await RunAsync(host,
            [new EvalCase("a", Expectation: "e"), new EvalCase("b", Expectation: "e")],
            new MentorEvalOptions { Judge = true, MinQuality = 0.9 });

        report.FailureReason.Should().Contain("0.20").And.Contain("0.90");
    }

    [Fact]
    public async Task A_case_exactly_at_the_quality_floor_passes()
    {
        await using var host = MentorHostFixture.Create();
        Script(host.Chat!, "an answer", verdict: "4");

        var report = await RunAsync(host,
            [new EvalCase("first", Expectation: "should be polite")],
            new MentorEvalOptions { Judge = true, MinQuality = 0.8 });

        report.Passed.Should().BeTrue();
    }

    [Fact]
    public async Task The_average_quality_is_gated_as_well_as_each_case()
    {
        // Two cases either side of the floor pass individually on average but not as a suite —
        // which is the question "did this prompt change make things worse overall" actually asks.
        await using var host = MentorHostFixture.Create();
        var verdicts = new Queue<string>(["5", "2"]);
        host.Chat!.Respond((messages, _) =>
        {
            var isJudge = messages.Any(m => m.Text?.Contains(JudgePromptMarker, StringComparison.Ordinal) == true);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant,
                isJudge ? verdicts.Dequeue() : "an answer"));
        });

        var report = await RunAsync(host,
            [new EvalCase("a", Expectation: "e"), new EvalCase("b", Expectation: "e")],
            new MentorEvalOptions { Judge = true, MinQuality = 0.7 });

        report.AverageQuality.Should().BeApproximately(0.7, 1e-9);
        report.Passed.Should().BeFalse("the second case is below the floor on its own");
        report.FailureReason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Without_a_quality_floor_a_low_score_is_recorded_but_not_fatal()
    {
        // Reporting quality without gating on it is a legitimate configuration — you watch the
        // number for a while before deciding where the floor belongs.
        await using var host = MentorHostFixture.Create();
        Script(host.Chat!, "an answer", verdict: "1");

        var report = await RunAsync(host,
            [new EvalCase("first", Expectation: "should be polite")],
            new MentorEvalOptions { Judge = true });

        report.Results.Single().QualityScore.Should().BeApproximately(0.2, 1e-9);
        report.Passed.Should().BeTrue();
    }

    // ── Reporting ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_report_carries_the_input_and_the_answer_for_every_case()
    {
        // A failed CI run is read by somebody who cannot reproduce it. Without the actual answer
        // the report says a case failed and nothing about what it said.
        await using var host = MentorHostFixture.Create();
        Script(host.Chat!, "the assistant's answer");

        var report = await RunAsync(host, [new EvalCase("the question")]);

        var result = report.Results.Single();
        result.Input.Should().Be("the question");
        result.Output.Should().Be("the assistant's answer");
    }

    [Fact]
    public async Task A_passing_run_carries_no_failure_reason_and_no_notes()
    {
        await using var host = MentorHostFixture.Create();
        Script(host.Chat!, "an answer");

        var report = await RunAsync(host, [new EvalCase("first")]);

        report.FailureReason.Should().BeNull();
        report.Results.Single().Notes.Should().BeNull();
    }

    [Fact]
    public async Task The_run_is_timed()
    {
        await using var host = MentorHostFixture.Create();
        Script(host.Chat!, "an answer");

        var report = await RunAsync(host, [new EvalCase("first")]);

        report.Duration.Should().BeGreaterThan(TimeSpan.Zero);
    }
}
