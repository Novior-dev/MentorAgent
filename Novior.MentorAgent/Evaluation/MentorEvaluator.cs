using System.Diagnostics;
using System.Globalization;
using Novior.MentorAgent.Options;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Options;

namespace Novior.MentorAgent.Evaluation;

/// <summary>
/// Prompt / regression evaluation harness (roadmap #10). Runs each <see cref="EvalCase"/> through the
/// configured <c>ChatClient</c>, reading the provider's real per-case token usage, applies a built-in
/// non-empty check and an optional lightweight LLM-as-judge, and returns a <see cref="MentorEvalReport"/>
/// that fails when a run exceeds a token budget or drops below a quality threshold — so you can gate CI
/// on it. When the developer supplies native checks/evaluators (<see cref="MentorEvalOptions.Checks"/> /
/// <see cref="MentorEvalOptions.Evaluators"/>, e.g. <c>EvalChecks</c>, <c>FoundryEvals</c>, MEAI
/// quality/safety), the Agent Framework's native evaluation runs on top and its verdicts also gate the
/// report.
///
/// Uses only already-referenced packages (<c>Microsoft.Agents.AI</c>). Point it at your mentor's
/// <c>ChatClient</c> and pass your system instructions to catch prompt regressions; for full-pipeline
/// token checks, drive the orchestrator instead and assert on <c>IMentorMetrics.GetSnapshot()</c>.
/// </summary>
public sealed class MentorEvaluator
{
    private readonly MentorOptions _options;
    private readonly ILogger<MentorEvaluator> _logger;

    public MentorEvaluator(IOptions<MentorOptions> options, ILogger<MentorEvaluator> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<MentorEvalReport> RunAsync(
        IEnumerable<EvalCase> cases, MentorEvalOptions? evalOptions = null, CancellationToken ct = default)
    {
        var opts = evalOptions ?? new MentorEvalOptions();
        var chatClient = _options.ChatClient
            ?? throw new InvalidOperationException("MentorEvaluator requires a ChatClient (Path A).");

        var caseList = cases as IReadOnlyList<EvalCase> ?? cases.ToList();
        if (caseList.Count == 0)
            return new MentorEvalReport { Results = [], Passed = true };

        // Run each case through the ChatClient and read the provider's real token usage (response.Usage) —
        // the same mechanism the per-call token counter uses. This is reliable and per-case; the Agent
        // Framework's native evaluators are layered on afterwards only when the developer supplies them.
        var sw = Stopwatch.StartNew();
        var results = new List<MentorEvalResult>(caseList.Count);

        foreach (var c in caseList)
        {
            var messages = new List<ChatMessage>();
            if (!string.IsNullOrWhiteSpace(opts.SystemInstructions))
                messages.Add(new ChatMessage(ChatRole.System, opts.SystemInstructions));
            messages.Add(new ChatMessage(ChatRole.User, c.Input));

            var response = await chatClient.GetResponseAsync(messages, cancellationToken: ct);
            var output = response.Text ?? string.Empty;
            long inTok  = response.Usage?.InputTokenCount  ?? 0;
            long outTok = response.Usage?.OutputTokenCount ?? 0;

            var passed = true;
            var notes = new List<string>();

            // Built-in deterministic check: the response must be non-empty.
            if (string.IsNullOrWhiteSpace(output))
            {
                passed = false;
                notes.Add("empty response");
            }

            // Quality via the built-in lightweight LLM-judge (only when the case has an expectation).
            double? quality = null;
            if (opts.Judge && !string.IsNullOrWhiteSpace(c.Expectation))
                quality = await JudgeAsync(opts.JudgeClient ?? chatClient, c.Input, output, c.Expectation!, ct);

            if (c.MaxTokens is { } maxT && inTok + outTok > maxT)
            {
                passed = false;
                notes.Add($"tokens {inTok + outTok} > {maxT}");
            }
            if (opts.MinQuality is { } minQ && quality is { } q && q < minQ)
            {
                passed = false;
                // Invariant: this text is a CI diagnostic, not user-facing prose. Formatted with the
                // ambient culture it reads "quality 0,40 < 0,80" on one machine and "0.40 < 0.80" on
                // another, so the same failure produces different log lines depending on where the
                // build ran — and any assertion or grep over the message becomes locale-dependent.
                notes.Add(string.Create(CultureInfo.InvariantCulture, $"quality {q:0.00} < {minQ:0.00}"));
            }

            results.Add(new MentorEvalResult
            {
                Input        = c.Input,
                Output       = output,
                InputTokens  = inTok,
                OutputTokens = outTok,
                QualityScore = quality,
                Passed       = passed,
                Notes        = notes.Count > 0 ? string.Join("; ", notes) : null,
            });
        }

        // Aggregate gates.
        var allPassed = results.All(r => r.Passed);
        string? failure = allPassed ? null : "one or more cases failed their check / budget / quality";

        long total = results.Sum(r => r.TotalTokens);
        if (opts.MaxTotalTokens is { } budget && total > budget)
        {
            allPassed = false;
            failure = $"total tokens {total} exceed budget {budget}";
        }

        if (opts.MinQuality is { } min && results.Any(r => r.QualityScore is not null))
        {
            var avg = results.Where(r => r.QualityScore is not null).Average(r => r.QualityScore!.Value);
            if (avg < min)
            {
                allPassed = false;
                failure = string.Create(CultureInfo.InvariantCulture,
                    $"average quality {avg:0.00} < {min:0.00}");
            }
        }

        // Optional native evaluators — the Agent Framework's LocalEvaluator (EvalChecks) and any
        // developer-supplied evaluators (FoundryEvals, MEAI quality/safety). Run only when provided, and
        // let each one's pass/fail gate the report. Kept in a try/catch so a native hiccup never breaks
        // the core token/quality gates above.
        if (opts.Checks is { Count: > 0 } || opts.Evaluators is { Count: > 0 })
        {
            try
            {
                AIAgent agent = new ChatClientAgent(chatClient, opts.SystemInstructions ?? string.Empty);
                var evaluators = new List<IAgentEvaluator>();
                if (opts.Checks is { Count: > 0 }) evaluators.Add(new LocalEvaluator([.. opts.Checks]));
                if (opts.Evaluators is { Count: > 0 }) evaluators.AddRange(opts.Evaluators);

                IReadOnlyList<AgentEvaluationResults> native =
                    await agent.EvaluateAsync(caseList.Select(c => c.Input), evaluators, cancellationToken: ct);

                foreach (var run in native)
                    if (!run.AllPassed)
                    {
                        allPassed = false;
                        failure = $"native evaluator '{run.ProviderName}' failed ({run.Passed}/{run.Total})";
                    }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[MentorAgent:Eval] Native evaluators failed to run — core gates still applied.");
            }
        }

        sw.Stop();

        _logger.LogInformation(
            "[MentorAgent:Eval] {N} case(s), {Tokens} tokens, pass={Pass}{Reason}.",
            results.Count, total, allPassed, failure is null ? "" : $" — {failure}");

        return new MentorEvalReport
        {
            Results       = results,
            Passed        = allPassed,
            FailureReason = failure,
            Duration      = sw.Elapsed,
        };
    }

    /// <summary>Built-in lightweight LLM-as-judge: scores 1–5 → 0–1. For production-grade quality use
    /// <see cref="MentorEvalOptions.Evaluators"/> with <c>FoundryEvals</c> or MEAI quality evaluators.</summary>
    private static async Task<double?> JudgeAsync(
        IChatClient judge, string input, string output, string expectation, CancellationToken ct)
    {
        var resp = await judge.GetResponseAsync(
            $"""
            Rate from 1 to 5 how well the ASSISTANT OUTPUT satisfies the EXPECTATION for the INPUT.
            Reply with ONLY a single digit 1-5, nothing else.
            INPUT: {input}
            EXPECTATION: {expectation}
            ASSISTANT OUTPUT: {output}
            """, cancellationToken: ct);

        var text = resp.Text?.Trim() ?? string.Empty;
        var digit = text.FirstOrDefault(char.IsDigit);
        return digit is >= '1' and <= '5' ? (digit - '0') / 5.0 : null;
    }
}
