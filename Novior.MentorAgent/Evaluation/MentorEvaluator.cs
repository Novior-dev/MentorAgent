using System.Diagnostics;
using System.Globalization;
using Novior.MentorAgent.Core;
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
/// <c>ChatClient</c> and pass your system instructions to catch prompt regressions.
///
/// <para>
/// <see cref="RunAsync"/> sends single messages straight to the <c>ChatClient</c>: no tools, no history, no pipeline.
/// <see cref="RunConversationsAsync"/> runs whole conversations through the mentor itself — the orchestrator,
/// streaming, with its tools, filters, roles, RAG and memory — and says, turn by turn, which tools were called, how
/// many tokens the turn used and how long the first text took.
/// </para>
/// </summary>
public sealed class MentorEvaluator
{
    private readonly MentorOptions _options;
    private readonly ILogger<MentorEvaluator> _logger;

    /// <summary>The application's scopes, one per conversation; <see langword="null"/> when built by hand without them.</summary>
    private readonly IServiceScopeFactory? _scopes;

    public MentorEvaluator(IOptions<MentorOptions> options, ILogger<MentorEvaluator> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// The constructor the container uses: with the application's services, which
    /// <see cref="RunConversationsAsync"/> needs to give each conversation a session of its own.
    /// </summary>
    public MentorEvaluator(IOptions<MentorOptions> options, ILogger<MentorEvaluator> logger, IServiceScopeFactory scopes)
        : this(options, logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        _scopes = scopes;
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
        var (allPassed, failure) = AggregateGates(results, opts, "one or more cases failed their check / budget / quality");
        long total = results.Sum(r => r.TotalTokens);

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

    /// <summary>
    /// Runs each conversation through the mentor itself, turn by turn, and evaluates every turn.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>How it runs.</b> Each conversation has a scope of its own — a session, as a new circuit would — and its
    /// turns go to that scope's <see cref="IMentorOrchestrator"/>, one after the other, streaming: the whole pipeline,
    /// with the conversation's history from one turn to the next. The scope is closed when the conversation ends, so
    /// no conversation sees another's history.
    /// </para>
    /// <para>
    /// <b>What each turn reports:</b> <see cref="MentorEvalResult.ToolsCalled"/>; the tokens of every chat-model call
    /// the turn made in its session — the coordinator, the classifiers and the agents under it — exact even while
    /// other sessions are talking (embedding calls are not counted); <see cref="MentorEvalResult.TimeToFirstToken"/>
    /// and <see cref="MentorEvalResult.TurnDuration"/>. The output is the text the turn streamed.
    /// </para>
    /// <para>
    /// <b>A turn fails</b> when an expected tool was not called, a forbidden one was, the turn showed an error — a
    /// refusal shown as an error included, unless the turn expects that refusal (<see cref="EvalTurn.ExpectRefusal"/>),
    /// and then it fails when it was not refused so — or streamed no text, its tokens exceed <see cref="EvalTurn.MaxTokens"/>,
    /// its first text came later than <see cref="EvalTurn.MaxTimeToFirstToken"/>, or the judge scored it under
    /// <see cref="MentorEvalOptions.MinQuality"/>; its notes say which. A failed turn does not stop its conversation:
    /// the next turns run on the history it left. The report's gates are those of <see cref="RunAsync"/>, over every
    /// turn of every conversation, and its <see cref="MentorEvalReport.FailureReason"/> names the first failed turn.
    /// </para>
    /// <para>
    /// <b>Confirmations</b> are answered no unless <see cref="MentorEvalOptions.ApproveConfirmations"/> says yes, and
    /// <b>the user</b> is nobody unless <see cref="MentorEvalOptions.PrepareSession"/> sets one.
    /// <see cref="MentorEvalOptions.SystemInstructions"/>, <see cref="MentorEvalOptions.Checks"/> and
    /// <see cref="MentorEvalOptions.Evaluators"/> are for <see cref="RunAsync"/> only: a warning says so when they
    /// are set. The built-in judge runs on <see cref="MentorEvalOptions.JudgeClient"/>, else the <c>ChatClient</c>.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The evaluator was constructed without the application's services, or the judge has no client.
    /// </exception>
    /// <exception cref="ArgumentException">A conversation has no name, no turns, or a turn with no input.</exception>
    public async Task<MentorEvalReport> RunConversationsAsync(
        IEnumerable<EvalConversation> conversations, MentorEvalOptions? evalOptions = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(conversations);
        var opts = evalOptions ?? new MentorEvalOptions();
        var scopes = _scopes ?? throw new InvalidOperationException(
            "RunConversationsAsync runs each conversation in a session of its own, so it needs the application's " +
            "services: resolve MentorEvaluator from the container (AddMentorAgent registers it) instead of constructing it.");

        var list = conversations as IReadOnlyList<EvalConversation> ?? conversations.ToList();

        // Checked before the first turn: a malformed case found halfway would have spent every turn before it.
        foreach (var conversation in list)
        {
            if (conversation is null || string.IsNullOrWhiteSpace(conversation.Name))
                throw new ArgumentException("Every conversation needs a name.", nameof(conversations));
            if (conversation.Turns is not { Count: > 0 })
                throw new ArgumentException($"Conversation '{conversation.Name}' has no turns.", nameof(conversations));
            if (conversation.Turns.Any(t => t is null || string.IsNullOrWhiteSpace(t.Input)))
                throw new ArgumentException($"Conversation '{conversation.Name}' has a turn with no input.", nameof(conversations));
        }

        if (list.Count == 0)
            return new MentorEvalReport { Results = [], Passed = true };

        IChatClient? judge = null;
        if (opts.Judge && list.Any(c => c.Turns.Any(t => !string.IsNullOrWhiteSpace(t.Expectation))))
            judge = opts.JudgeClient ?? _options.ChatClient ?? throw new InvalidOperationException(
                "The built-in judge needs MentorEvalOptions.JudgeClient, or a ChatClient in MentorOptions.");

        var ignored = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(opts.SystemInstructions)) ignored.Add(nameof(MentorEvalOptions.SystemInstructions));
        if (opts.Checks is { Count: > 0 }) ignored.Add(nameof(MentorEvalOptions.Checks));
        if (opts.Evaluators is { Count: > 0 }) ignored.Add(nameof(MentorEvalOptions.Evaluators));
        if (ignored.Count > 0)
            _logger.LogWarning(
                "[MentorAgent:Eval] {Options} apply to RunAsync only: RunConversationsAsync runs the mentor on its own " +
                "instructions and runs no native checks or evaluators.", string.Join(", ", ignored));

        var sw = Stopwatch.StartNew();
        var results = new List<MentorEvalResult>();
        foreach (var conversation in list)
            await RunConversationAsync(scopes, conversation, opts, judge, results, ct);
        sw.Stop();

        var failedTurns = results.Where(r => !r.Passed).ToList();
        var (allPassed, failure) = AggregateGates(results, opts, failedTurns.Count == 0 ? null
            : $"{failedTurns.Count} of {results.Count} turn(s) failed — first: '{failedTurns[0].Conversation}' " +
              $"turn {failedTurns[0].Turn}: {failedTurns[0].Notes}");

        _logger.LogInformation(
            "[MentorAgent:Eval] {Conversations} conversation(s), {Turns} turn(s), {Tokens} tokens, pass={Pass}{Reason}.",
            list.Count, results.Count, results.Sum(r => r.TotalTokens), allPassed, failure is null ? "" : $" — {failure}");

        return new MentorEvalReport
        {
            Results       = results,
            Passed        = allPassed,
            FailureReason = failure,
            Duration      = sw.Elapsed,
        };
    }

    private async Task RunConversationAsync(
        IServiceScopeFactory scopes, EvalConversation conversation, MentorEvalOptions opts, IChatClient? judge,
        List<MentorEvalResult> results, CancellationToken ct)
    {
        // A scope of its own: a session and a history of its own, as a new circuit would have.
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        if (opts.PrepareSession is { } prepare)
            await prepare(services, ct);

        var orchestrator = services.GetRequiredService<IMentorOrchestrator>();
        var tokens = services.GetRequiredService<MentorSessionTokenUsage>();
        using var probe = new TurnProbe(
            services.GetRequiredService<IMentorStateService>(), services.GetRequiredService<MentorTurnContext>(),
            orchestrator, opts.ApproveConfirmations);

        for (var i = 0; i < conversation.Turns.Count; i++)
        {
            var turn = conversation.Turns[i];
            long inBefore = tokens.InputTokens, outBefore = tokens.OutputTokens;

            probe.Begin();
            Exception? thrown = null;
            try
            {
                await orchestrator.SendMessageAsync(turn.Input, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                thrown = ex;
            }
            var seen = probe.End();

            // The orchestrator ends a cancelled turn quietly: the run must not go on as if it had been answered.
            ct.ThrowIfCancellationRequested();

            results.Add(await EvaluateTurnAsync(
                conversation.Name, i + 1, turn, seen, thrown,
                tokens.InputTokens - inBefore, tokens.OutputTokens - outBefore, opts, judge, ct));
        }
    }

    private static async Task<MentorEvalResult> EvaluateTurnAsync(
        string conversation, int number, EvalTurn turn, TurnObservation seen, Exception? thrown,
        long inTok, long outTok, MentorEvalOptions opts, IChatClient? judge, CancellationToken ct)
    {
        var failures = new List<string>();
        List<string> errors = thrown is null ? [.. seen.Errors] : [.. seen.Errors, thrown.Message];

        // Story 1623 — the refusal the turn expects is its answer, not an error. Only that one message is taken out:
        // anything else the turn showed still fails it.
        string? refusedAsExpected = null;
        if (turn.ExpectRefusal is { } refusalExpected)
        {
            if (seen.Refusal is { } refused && (refusalExpected == EvalRefusal.Any || refusalExpected == refused))
            {
                errors.Remove(seen.RefusalText!);
                refusedAsExpected = $"refused: {RefusalName(refused)}";
            }
            else if (seen.Refusal is { } other)
                failures.Add($"expected {ExpectedRefusal(refusalExpected)}, refused: {RefusalName(other)}");
            else
                failures.Add($"expected {ExpectedRefusal(refusalExpected)}, " +
                             (errors.Count > 0 ? "the turn failed" : "the turn was answered"));
        }

        if (errors.Count > 0)
            failures.Add("error: " + string.Join(" | ", errors));
        else if (refusedAsExpected is null && string.IsNullOrWhiteSpace(seen.Output))
            failures.Add("empty response");

        if (turn.ExpectTools is { Count: > 0 } expected
            && expected.Where(t => !seen.ToolsCalled.Contains(t, StringComparer.Ordinal)).ToList() is { Count: > 0 } missing)
            failures.Add($"expected {string.Join(", ", missing)}, called " +
                         (seen.ToolsCalled.Count == 0 ? "none" : string.Join(", ", seen.ToolsCalled)));

        if (turn.ForbidTools is { Count: > 0 } forbidden
            && forbidden.Where(t => seen.ToolsCalled.Contains(t, StringComparer.Ordinal)).ToList() is { Count: > 0 } hit)
            failures.Add($"called forbidden {string.Join(", ", hit)}");

        if (turn.MaxTokens is { } maxT && inTok + outTok > maxT)
            failures.Add($"tokens {inTok + outTok} > {maxT}");

        if (turn.MaxTimeToFirstToken is { } limit)
        {
            if (seen.TimeToFirstToken is not { } first)
                failures.Add(string.Create(CultureInfo.InvariantCulture,
                    $"no text, first token limit {limit.TotalMilliseconds:0} ms"));
            else if (first > limit)
                failures.Add(string.Create(CultureInfo.InvariantCulture,
                    $"first token after {first.TotalMilliseconds:0} ms > {limit.TotalMilliseconds:0} ms"));
        }

        double? quality = null;
        if (judge is not null && !string.IsNullOrWhiteSpace(turn.Expectation))
            quality = await JudgeAsync(judge, turn.Input, seen.Output, turn.Expectation!, ct);
        if (opts.MinQuality is { } minQ && quality is { } q && q < minQ)
            failures.Add(string.Create(CultureInfo.InvariantCulture, $"quality {q:0.00} < {minQ:0.00}"));

        // What the turn did that is worth reading even when it passed: its confirmations, the calls the gate refused, the
        // refusal it expected.
        var notes = seen.Notes.Concat(refusedAsExpected is null ? [] : [refusedAsExpected]).Concat(failures).ToList();

        return new MentorEvalResult
        {
            Input            = turn.Input,
            Output           = seen.Output,
            InputTokens      = inTok,
            OutputTokens     = outTok,
            QualityScore     = quality,
            Passed           = failures.Count == 0,
            Notes            = notes.Count > 0 ? string.Join("; ", notes) : null,
            Conversation     = conversation,
            Turn             = number,
            ToolsCalled      = seen.ToolsCalled,
            TimeToFirstToken = seen.TimeToFirstToken,
            TurnDuration     = seen.Duration,
            Refusal          = seen.Refusal,
        };
    }

    private static string RefusalName(EvalRefusal refusal) => refusal switch
    {
        EvalRefusal.OutOfScope => "out of scope",
        EvalRefusal.Guardrail  => "guardrail",
        _                      => refusal.ToString(),
    };

    private static string ExpectedRefusal(EvalRefusal refusal) => refusal switch
    {
        EvalRefusal.OutOfScope => "an out-of-scope refusal",
        EvalRefusal.Guardrail  => "a guardrail refusal",
        _                      => "a refusal",
    };

    /// <summary>
    /// The run's gates over its results: every result passed, the whole run's token budget, the average quality.
    /// <paramref name="resultFailed"/> is the reason when only a result failed.
    /// </summary>
    private static (bool Passed, string? Failure) AggregateGates(
        IReadOnlyList<MentorEvalResult> results, MentorEvalOptions opts, string? resultFailed)
    {
        var allPassed = results.All(r => r.Passed);
        string? failure = allPassed ? null : resultFailed;

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

        return (allPassed, failure);
    }

    /// <summary>What one turn did, as its session told it.</summary>
    private sealed record TurnObservation(
        string Output,
        IReadOnlyList<string> ToolsCalled,
        IReadOnlyList<string> Notes,
        IReadOnlyList<string> Errors,
        TimeSpan? TimeToFirstToken,
        TimeSpan Duration,
        EvalRefusal? Refusal,
        string? RefusalText);

    /// <summary>
    /// Listens to a conversation's session for the turn under way: the text it streams, the gate's decisions, the UI
    /// actions, the confirmations and the errors shown. The tools come from the gate itself, not from the action
    /// feedback, which <c>EnableActionFeedback</c> can turn off. Each confirmation is answered as the evaluation says,
    /// through <see cref="IMentorOrchestrator.RespondToApprovalAsync"/>: the path the widget's buttons and
    /// <c>POST /mentor/approve</c> reach, in Blocking and in Native mode.
    /// </summary>
    private sealed class TurnProbe : IDisposable
    {
        private readonly object _lock = new();
        private readonly IMentorStateService _state;
        private readonly MentorTurnContext _turn;
        private readonly IMentorOrchestrator _orchestrator;
        private readonly bool _approve;

        private readonly StringBuilder _text = new();
        private readonly List<string> _tools = [];
        private readonly List<string> _notes = [];
        private readonly List<string> _errors = [];
        private Stopwatch _clock = new();
        private TimeSpan? _firstToken;
        private EvalRefusal? _refusal;
        private string? _refusalText;

        internal TurnProbe(IMentorStateService state, MentorTurnContext turn, IMentorOrchestrator orchestrator, bool approve)
        {
            _state = state;
            _turn = turn;
            _orchestrator = orchestrator;
            _approve = approve;

            _state.OnStreamingChunk       += OnChunk;
            _state.OnUIActionExecuting    += OnUIAction;
            _state.OnConfirmationRequired += OnConfirmation;
            _state.OnError                += OnError;
            _turn.Decided                  = OnDecided;
            _turn.Refused                  = OnRefused;
        }

        internal void Begin()
        {
            lock (_lock)
            {
                _text.Clear();
                _tools.Clear();
                _notes.Clear();
                _errors.Clear();
                _firstToken = null;
                _refusal = null;
                _refusalText = null;
                _clock = Stopwatch.StartNew();
            }
        }

        internal TurnObservation End()
        {
            lock (_lock)
            {
                _clock.Stop();
                return new TurnObservation(
                    _text.ToString(), [.. _tools], [.. _notes], [.. _errors], _firstToken, _clock.Elapsed,
                    _refusal, _refusalText);
            }
        }

        private void OnChunk(string chunk)
        {
            if (string.IsNullOrEmpty(chunk)) return;
            lock (_lock)
            {
                _firstToken ??= _clock.Elapsed;
                _text.Append(chunk);
            }
        }

        private void OnDecided(string tool, MentorActionOutcome outcome, string reason)
        {
            lock (_lock)
            {
                switch (outcome)
                {
                    // Let through: it runs now, or asks for a confirmation next.
                    case MentorActionOutcome.Allowed:
                    case MentorActionOutcome.ConfirmationRequested:
                        Called(tool);
                        break;
                    // The model asked for it and the gate refused: not called, but worth a line.
                    case MentorActionOutcome.Denied:
                    case MentorActionOutcome.Blocked:
                    case MentorActionOutcome.PolicyFailed:
                        _notes.Add($"not run: {tool} ({reason})");
                        break;
                }
            }
        }

        private void OnUIAction(string action)
        {
            lock (_lock) Called(action);
        }

        private void OnConfirmation(ConfirmationRequest request)
        {
            // The call is already in the tools: the gate decided ConfirmationRequested before raising this.
            lock (_lock)
                _notes.Add($"confirmation asked: {request.ToolName} — {(_approve ? "approved" : "denied")}");

            // The turn's waiter exists before this event is raised, so an answer given from here is not lost.
            _ = _orchestrator.RespondToApprovalAsync(request, _approve);
        }

        private void OnError(string message)
        {
            lock (_lock) _errors.Add(message);
        }

        private void OnRefused(EvalRefusal refusal, string shown)
        {
            lock (_lock)
            {
                _refusal = refusal;
                _refusalText = shown;
            }
        }

        private void Called(string tool)
        {
            if (!_tools.Contains(tool, StringComparer.Ordinal)) _tools.Add(tool);
        }

        public void Dispose()
        {
            _state.OnStreamingChunk       -= OnChunk;
            _state.OnUIActionExecuting    -= OnUIAction;
            _state.OnConfirmationRequired -= OnConfirmation;
            _state.OnError                -= OnError;
            _turn.Decided                  = null;
            _turn.Refused                  = null;
        }
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
