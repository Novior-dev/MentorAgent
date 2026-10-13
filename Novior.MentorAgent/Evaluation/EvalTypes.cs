using Microsoft.Agents.AI;

namespace Novior.MentorAgent.Evaluation;

/// <summary>One evaluation case (roadmap #10).</summary>
/// <param name="Input">The user message to send.</param>
/// <param name="Expectation">Optional description of the expected behaviour, used by the LLM judge.</param>
/// <param name="MaxTokens">Optional per-case token budget; the case fails if exceeded.</param>
public sealed record EvalCase(string Input, string? Expectation = null, long? MaxTokens = null);

/// <summary>Result for a single <see cref="EvalCase"/>.</summary>
public sealed record MentorEvalResult
{
    public required string Input { get; init; }
    public required string Output { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long TotalTokens => InputTokens + OutputTokens;
    /// <summary>Quality score 0–1 when judged, else <see langword="null"/>.</summary>
    public double? QualityScore { get; init; }
    public bool Passed { get; init; }
    public string? Notes { get; init; }
}

/// <summary>Aggregate report — the CI gate.</summary>
public sealed record MentorEvalReport
{
    public required IReadOnlyList<MentorEvalResult> Results { get; init; }
    public long TotalTokens => Results.Sum(r => r.TotalTokens);
    public double? AverageQuality => Results.Any(r => r.QualityScore is not null)
        ? Results.Where(r => r.QualityScore is not null).Average(r => r.QualityScore!.Value)
        : null;
    public double PassRate => Results.Count == 0 ? 1 : (double)Results.Count(r => r.Passed) / Results.Count;
    public required bool Passed { get; init; }
    public string? FailureReason { get; init; }
    /// <summary>Wall-clock time of the whole native evaluation run.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Throws when the report did not pass — use as a CI assertion.</summary>
    public void ThrowIfFailed()
    {
        if (!Passed)
            throw new InvalidOperationException(
                $"MentorAgent evaluation failed: {FailureReason ?? "one or more cases failed"}.");
    }
}

/// <summary>Knobs for an evaluation run.</summary>
public sealed class MentorEvalOptions
{
    /// <summary>System prompt applied to every case (e.g. your mentor instructions under test).</summary>
    public string? SystemInstructions { get; set; }

    /// <summary>Global token budget for the whole run — the report fails if exceeded (token regression gate).</summary>
    public long? MaxTotalTokens { get; set; }

    /// <summary>Minimum average quality (0–1). Requires <see cref="Judge"/> = true and case expectations.</summary>
    public double? MinQuality { get; set; }

    /// <summary>Run the built-in lightweight LLM-as-judge for cases that have an <see cref="EvalCase.Expectation"/>.</summary>
    public bool Judge { get; set; }

    /// <summary>Client used for the built-in judge; defaults to the mentor's configured <c>ChatClient</c>.</summary>
    public IChatClient? JudgeClient { get; set; }

    /// <summary>
    /// Extra native Agent Framework deterministic checks applied to every case — e.g.
    /// <c>EvalChecks.KeywordCheck("weather")</c>, <c>EvalChecks.ToolCalledCheck("get_weather")</c>,
    /// or <c>FunctionEvaluator.Create(...)</c>. Added on top of the built-in non-empty check.
    /// </summary>
    public IReadOnlyList<EvalCheck>? Checks { get; set; }

    /// <summary>
    /// Additional native Agent Framework evaluators run alongside the local checks — e.g.
    /// <c>FoundryEvals</c> (cloud quality &amp; safety) or MEAI quality/safety evaluators wrapped for
    /// the agent. Each one's pass/fail also gates the report. Requires the corresponding package
    /// (<c>Microsoft.Agents.AI.Foundry</c>, <c>Microsoft.Extensions.AI.Evaluation.Quality/Safety</c>).
    /// </summary>
    public IReadOnlyList<IAgentEvaluator>? Evaluators { get; set; }
}
