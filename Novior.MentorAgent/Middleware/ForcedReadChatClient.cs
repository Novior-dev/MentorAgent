using System.Runtime.CompilerServices;

namespace Novior.MentorAgent.Middleware;

/// <summary>
/// <see cref="Options.MentorOptions.RequireLookupForRecordQuestions"/> without semantic tool filtering (BUG-222). The
/// forced read ran inside <see cref="ToolFilterChatClient"/>, which needs an embedding model: without one the option
/// had no effect, and asked in Italian which expenses were still waiting for approval, a host without the filter got
/// a list the model made up — no read called — about one time in three.
///
/// <para>
/// The same decision, on the same request: when the classifier says the first message of a turn asks for a value of
/// the records, the first call must call a declared read, or hand the request to a specialist or a team. With no
/// ranking to choose among the reads, every one on the request is offered — the request has already lost those its
/// user may not run (<see cref="RoleFilterChatClient"/> sits outside). The follow-ups of the turn are never decided
/// again, and keep every tool.
/// </para>
///
/// <para>
/// Installed only where <see cref="ToolFilterChatClient"/> is not, at its place in the pipeline: outside
/// <see cref="UIActionsMiddleware"/>, which adds no UI action to a forced request. One decision per turn.
/// </para>
/// </summary>
internal sealed class ForcedReadChatClient : DelegatingChatClient
{
    /// <summary>Which tools the first call of a record question may be required to call (declared read-only, no confirmation, no role).</summary>
    private readonly Func<string, bool> _isForcibleRead;

    /// <summary>The classifier, asked about reads only; null when it could not tell, and the turn runs as without the option.</summary>
    private readonly Func<string, Core.RequestShape, CancellationToken, Task<Core.RequestKind?>> _classify;

    /// <summary><c>route_to_specialist</c> and the teams: a request meant for another assistant can still be handed over.</summary>
    private readonly IReadOnlySet<string> _waysOut;

    private readonly ILogger _logger;

    /// <summary>Set by <see cref="BeginUserTurn"/>; consumed by the next request.</summary>
    private bool _userTurnStarting;

    /// <summary>The turn answers approval requests a stopped turn left open (BUG-116): never forced.</summary>
    private bool _answersLeftOpen;

    private static readonly Core.RequestShape Reads = new(Lookup: true, [], Navigation: false);

    public ForcedReadChatClient(
        IChatClient innerClient,
        Func<string, bool> isForcibleRead,
        Func<string, Core.RequestShape, CancellationToken, Task<Core.RequestKind?>> classify,
        IReadOnlySet<string> waysOut,
        ILogger logger) : base(innerClient)
    {
        _isForcibleRead = isForcibleRead;
        _classify = classify;
        _waysOut = waysOut;
        _logger = logger;
    }

    /// <summary>
    /// Tells the client that the next request opens a user turn; the orchestrator calls it before every turn.
    /// <paramref name="leftOpenCalls"/> are the tools of the approval requests a stopped turn left open, answered on
    /// that request — which then ends on a tool result, and is still the turn's first (BUG-116).
    /// </summary>
    internal void BeginUserTurn(IReadOnlyCollection<string>? leftOpenCalls = null)
    {
        _userTurnStarting = true;
        _answersLeftOpen = leftOpenCalls is { Count: > 0 };
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var decided = await DecideAsync(messages, options, cancellationToken);
        return await base.GetResponseAsync(messages, decided, cancellationToken);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var decided = await DecideAsync(messages, options, cancellationToken);
        await foreach (var update in base.GetStreamingResponseAsync(messages, decided, cancellationToken))
            yield return update;
    }

    private async Task<ChatOptions?> DecideAsync(IEnumerable<ChatMessage> messages, ChatOptions? options, CancellationToken ct)
    {
        // Consumed first, so no early return below leaves it set for a request that does continue the turn.
        var startsTurn = _userTurnStarting;
        var answersLeftOpen = _answersLeftOpen;
        _userTurnStarting = false;
        _answersLeftOpen = false;

        if (answersLeftOpen || options?.Tools is not { Count: > 0 } tools) return options;

        // Mid-turn: the model is handed the result of a call, or the answer to an approval. Decided once per turn.
        var list = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        if (!startsTurn && TurnContinuation.IsToolResult(list)) return options;

        var message = list.LastOrDefault(m => m.Role == ChatRole.User)?.Text;
        if (string.IsNullOrWhiteSpace(message)) return options;

        var reads = tools.Where(t => t is AIFunction f && _isForcibleRead(f.Name)).ToList();
        if (reads.Count == 0) return options;

        if (await _classify(message, Reads, ct) != Core.RequestKind.Lookup) return options;

        var forced = new List<AITool>(reads.Count + _waysOut.Count);
        forced.AddRange(reads);
        forced.AddRange(tools.Where(t => _waysOut.Contains(t.Name)));
        _logger.LogDebug(
            "[MentorAgent] Forced read: the message asks for a record value — the first call must read ({Names}).",
            string.Join(", ", forced.Select(t => t.Name)));
        return ToolFilterChatClient.Forced(options, forced, null);
    }
}
