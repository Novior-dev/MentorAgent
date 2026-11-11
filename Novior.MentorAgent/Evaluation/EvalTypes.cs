using Microsoft.Agents.AI;

namespace Novior.MentorAgent.Evaluation;

/// <summary>One evaluation case (roadmap #10).</summary>
/// <param name="Input">The user message to send.</param>
/// <param name="Expectation">Optional description of the expected behaviour, used by the LLM judge.</param>
/// <param name="MaxTokens">Optional per-case token budget; the case fails if exceeded.</param>
public sealed record EvalCase(string Input, string? Expectation = null, long? MaxTokens = null);

/// <summary>
/// One user turn of an <see cref="EvalConversation"/>, with what it must do. Every check is optional; a turn with
/// none passes when it answers with some text and without an error.
/// </summary>
/// <param name="Input">The user message of this turn.</param>
public sealed record EvalTurn(string Input)
{
    /// <summary>
    /// Tools this turn must call, by technical name (for example <c>get_order</c>): the turn fails if any of them
    /// is not in <see cref="MentorEvalResult.ToolsCalled"/>. This is how a case says "at turn 2 it must call X".
    /// </summary>
    public IReadOnlyList<string>? ExpectTools { get; init; }

    /// <summary>Tools this turn must not call, by technical name: the turn fails if any of them was called.</summary>
    public IReadOnlyList<string>? ForbidTools { get; init; }

    /// <summary>The expected behaviour, for the built-in judge, as <see cref="EvalCase.Expectation"/>.</summary>
    public string? Expectation { get; init; }

    /// <summary>Token budget of this turn — every chat-model call it made; the turn fails if exceeded.</summary>
    public long? MaxTokens { get; init; }

    /// <summary>
    /// The longest acceptable wait for the first streamed text of the answer; the turn fails if the first text came
    /// later, or never came.
    /// </summary>
    public TimeSpan? MaxTimeToFirstToken { get; init; }

    /// <summary>
    /// The refusal this turn must get: the mentor turns the message down instead of answering, and the user sees an
    /// error. With it, that refusal passes the turn and the conversation goes on; a turn that was answered, refused the
    /// other way, or failed, fails, and its notes say which. <see langword="null"/>, the default, expects none: a refusal
    /// fails the turn as any error does.
    /// <para>
    /// A refusal is one of the two judgements on the message: <see cref="EvalRefusal.OutOfScope"/> and
    /// <see cref="EvalRefusal.Guardrail"/>. A safety check that could not run, a message too long, the rate limit and a
    /// turn without a tenant are not refusals: they stay errors, expected or not, so a case that must be refused does
    /// not pass because the guardrail is down.
    /// </para>
    /// </summary>
    public EvalRefusal? ExpectRefusal { get; init; }
}

/// <summary>
/// A refusal the mentor shows instead of an answer: what <see cref="EvalTurn.ExpectRefusal"/> expects and
/// <see cref="MentorEvalResult.Refusal"/> reports.
/// </summary>
public enum EvalRefusal
{
    /// <summary>Either refusal. Only for <see cref="EvalTurn.ExpectRefusal"/>: a result reports which one it was.</summary>
    Any,

    /// <summary>The message is not about the application: <see cref="Options.MentorOptions.RefuseOutOfScope"/>.</summary>
    OutOfScope,

    /// <summary>
    /// The message was judged unsafe — by the safety check (<see cref="Options.MentorOptions.EnableSafetyCheck"/>), by the
    /// host's <see cref="Options.MentorOptions.InputGuardrail"/> — or the model itself refused to answer it.
    /// </summary>
    Guardrail,
}

/// <summary>
/// A conversation to evaluate with <see cref="MentorEvaluator.RunConversationsAsync"/>: its turns run one after the
/// other in one session of their own, so each turn sees the history of the turns before it.
/// </summary>
/// <param name="Name">The conversation's name, repeated on each of its results.</param>
/// <param name="Turns">The user turns, in order.</param>
public sealed record EvalConversation(string Name, IReadOnlyList<EvalTurn> Turns);

/// <summary>
/// Result for a single <see cref="EvalCase"/>, or for one turn of an <see cref="EvalConversation"/>.
/// </summary>
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

    /// <summary>The <see cref="EvalConversation.Name"/> of the turn; <see langword="null"/> for an <see cref="EvalCase"/>.</summary>
    public string? Conversation { get; init; }

    /// <summary>The turn's position in its conversation, from 1; <see langword="null"/> for an <see cref="EvalCase"/>.</summary>
    public int? Turn { get; init; }

    /// <summary>
    /// The tools the turn called, by technical name, each once, in the order first called: the calls MentorAgent's
    /// tool gate let through — run, or asked to confirm, whatever the answer — and the page's UI actions. A call the
    /// gate refused (a role missing, a policy) is not here, and the notes say "not run: &lt;tool&gt; (&lt;reason&gt;)";
    /// a tool the user's roles leave out is not even offered to the model. Empty for an <see cref="EvalCase"/>,
    /// which offers no tools.
    /// </summary>
    public IReadOnlyList<string> ToolsCalled { get; init; } = [];

    /// <summary>
    /// From the start of the turn to its first streamed text; <see langword="null"/> when the turn streamed no text,
    /// and for an <see cref="EvalCase"/>, which does not stream.
    /// </summary>
    public TimeSpan? TimeToFirstToken { get; init; }

    /// <summary>How long the whole turn took; <see langword="null"/> for an <see cref="EvalCase"/>.</summary>
    public TimeSpan? TurnDuration { get; init; }

    /// <summary>
    /// The refusal the turn showed instead of an answer — <see cref="EvalRefusal.OutOfScope"/> or
    /// <see cref="EvalRefusal.Guardrail"/> — whether the turn expected it or not; <see langword="null"/> when it was not
    /// refused, and for an <see cref="EvalCase"/>.
    /// </summary>
    public EvalRefusal? Refusal { get; init; }
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
    /// <summary>
    /// System prompt applied to every case (e.g. your mentor instructions under test). For
    /// <see cref="MentorEvaluator.RunAsync"/> only: the conversations of
    /// <see cref="MentorEvaluator.RunConversationsAsync"/> run on the mentor's own instructions.
    /// </summary>
    public string? SystemInstructions { get; set; }

    /// <summary>Global token budget for the whole run — the report fails if exceeded (token regression gate).</summary>
    public long? MaxTotalTokens { get; set; }

    /// <summary>Minimum average quality (0–1). Requires <see cref="Judge"/> = true and case expectations.</summary>
    public double? MinQuality { get; set; }

    /// <summary>
    /// Run the built-in lightweight LLM-as-judge for cases that have an <see cref="EvalCase.Expectation"/>, and for
    /// turns that have an <see cref="EvalTurn.Expectation"/>.
    /// </summary>
    public bool Judge { get; set; }

    /// <summary>Client used for the built-in judge; defaults to the mentor's configured <c>ChatClient</c>.</summary>
    public IChatClient? JudgeClient { get; set; }

    /// <summary>
    /// Extra native Agent Framework deterministic checks applied to every case — e.g.
    /// <c>EvalChecks.KeywordCheck("weather")</c>, <c>EvalChecks.ToolCalledCheck("get_weather")</c>,
    /// or <c>FunctionEvaluator.Create(...)</c>. Added on top of the built-in non-empty check.
    /// For <see cref="MentorEvaluator.RunAsync"/> only: they evaluate an agent on single inputs, and
    /// <see cref="MentorEvaluator.RunConversationsAsync"/> does not run them.
    /// </summary>
    public IReadOnlyList<EvalCheck>? Checks { get; set; }

    /// <summary>
    /// Additional native Agent Framework evaluators run alongside the local checks — e.g.
    /// <c>FoundryEvals</c> (cloud quality &amp; safety) or MEAI quality/safety evaluators wrapped for
    /// the agent. Each one's pass/fail also gates the report. Requires the corresponding package
    /// (<c>Microsoft.Agents.AI.Foundry</c>, <c>Microsoft.Extensions.AI.Evaluation.Quality/Safety</c>).
    /// For <see cref="MentorEvaluator.RunAsync"/> only, as <see cref="Checks"/>.
    /// </summary>
    public IReadOnlyList<IAgentEvaluator>? Evaluators { get; set; }

    /// <summary>
    /// How <see cref="MentorEvaluator.RunConversationsAsync"/> answers an action that asks for a confirmation.
    /// <b>By default every confirmation is answered no: an evaluation must not write data on its own.</b> The
    /// model's call still counts in <see cref="MentorEvalResult.ToolsCalled"/>, and the result's notes say
    /// "confirmation asked: &lt;tool&gt; — denied". Set <see langword="true"/> to answer yes, and the action runs —
    /// against whatever services the evaluation's scope resolves, so only on data meant for it.
    /// </summary>
    public bool ApproveConfirmations { get; set; }

    /// <summary>
    /// Called with the services of each conversation's own scope, before its first turn, for
    /// <see cref="MentorEvaluator.RunConversationsAsync"/>. A conversation runs outside any request or circuit, so
    /// it has no user: the tools that need roles are left out unless this sets one — for example
    /// <c>((IHostEnvironmentAuthenticationStateProvider)sp.GetRequiredService&lt;AuthenticationStateProvider&gt;())
    /// .SetAuthenticationState(Task.FromResult(new AuthenticationState(user)))</c> in a Blazor Server host, or the
    /// host's own test provider. The page and its data can be set here too.
    /// </summary>
    public Func<IServiceProvider, CancellationToken, Task>? PrepareSession { get; set; }
}
