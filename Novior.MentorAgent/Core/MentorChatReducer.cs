namespace Novior.MentorAgent.Core;

/// <summary>
/// The reducer that keeps MentorAgent's built-in in-memory history bounded: it keeps the most recent
/// <b>whole turns</b> that fit in <see cref="MaxMessages"/>, and every tool call together with its result.
///
/// <para>
/// <b>Why not <c>MessageCountingChatReducer</c>.</b> The framework's reducer drops every message that
/// carries a function call or a function result. From the next turn on, the model no longer sees the
/// data a tool read, and answers a follow-up ("and the second one?") by guessing or by calling the
/// tool again. This reducer counts those messages like any other and cuts only where a turn starts.
/// </para>
///
/// <para>
/// <b>How it cuts.</b> A turn starts at a user message (one that only answers a tool approval does
/// not start a turn). Turns are kept newest first while they fit; the first that does not fit is
/// dropped with everything older, so a turn is never kept in part. When the newest turn alone is
/// over the budget, its question and its most recent messages are kept. A tool result, or an
/// approval response, whose call was cut away is removed with it: the model never sees a result
/// without the call that produced it. The first system message, if any, is always kept and not
/// counted, as <c>MessageCountingChatReducer</c> does.
/// </para>
///
/// <para>
/// It is stateless, so one instance can serve every session. Use it in a history provider of your
/// own: <c>new InMemoryChatHistoryProvider(new() { ChatReducer = new MentorChatReducer(50) })</c>.
/// </para>
/// </summary>
public sealed class MentorChatReducer : IChatReducer
{
    /// <summary>Creates a reducer that keeps at most <paramref name="maxMessages"/> messages.</summary>
    /// <param name="maxMessages">
    /// The budget, counting every message except the first system message — tool calls and tool
    /// results included. Must be at least 1.
    /// </param>
    public MentorChatReducer(int maxMessages)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxMessages, 1);
        MaxMessages = maxMessages;
    }

    /// <summary>The most messages the reduced history holds, not counting the first system message.</summary>
    public int MaxMessages { get; }

    /// <inheritdoc />
    public Task<IEnumerable<ChatMessage>> ReduceAsync(
        IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return Task.FromResult<IEnumerable<ChatMessage>>(Reduce([.. messages]));
    }

    private List<ChatMessage> Reduce(List<ChatMessage> all)
    {
        var systemAt = all.FindIndex(m => m.Role == ChatRole.System);
        var rest = systemAt < 0 ? all : [.. all.Where((_, i) => i != systemAt)];
        if (rest.Count <= MaxMessages)
            return all;

        // Turn starts, oldest first. The first message always opens one, even when it is not a
        // question (a greeting stored before the user spoke).
        var starts = new List<int> { 0 };
        for (var i = 1; i < rest.Count; i++)
            if (StartsTurn(rest[i]))
                starts.Add(i);

        // Whole turns, newest first, while they fit.
        var from = rest.Count;
        for (var t = starts.Count - 1; t >= 0 && rest.Count - starts[t] <= MaxMessages; t--)
            from = starts[t];

        var kept = new bool[rest.Count];
        if (from < rest.Count)
        {
            for (var i = from; i < rest.Count; i++) kept[i] = true;
        }
        else
        {
            // The newest turn alone is over budget: its question, then its most recent messages.
            var turn = starts[^1];
            var budget = MaxMessages;
            if (StartsTurn(rest[turn]) && budget > 1)
            {
                kept[turn] = true;
                budget--;
            }
            for (var i = rest.Count - 1; i > turn && budget > 0; i--, budget--)
                kept[i] = true;
        }

        var reduced = DropOrphans(rest, kept);
        if (systemAt >= 0)
            reduced.Insert(0, all[systemAt]);
        return reduced;
    }

    /// <summary>
    /// A user message starts a turn unless all it carries is the answer to a tool approval or a tool
    /// result — those belong to the turn that asked.
    /// </summary>
    private static bool StartsTurn(ChatMessage message)
        => message.Role == ChatRole.User
           && !(message.Contents.Count > 0
                && message.Contents.All(c => c is InputResponseContent or ToolResultContent));

    /// <summary>
    /// The kept messages, minus any result or approval response whose call or request was cut away.
    /// Only what this reduction cut is considered: a history that already held an unmatched result
    /// is passed through as it was.
    /// </summary>
    private static List<ChatMessage> DropOrphans(List<ChatMessage> messages, bool[] kept)
    {
        HashSet<string> keptCalls = new(StringComparer.Ordinal), cutCalls = new(StringComparer.Ordinal);
        HashSet<string> keptRequests = new(StringComparer.Ordinal), cutRequests = new(StringComparer.Ordinal);
        for (var i = 0; i < messages.Count; i++)
        {
            var calls = kept[i] ? keptCalls : cutCalls;
            var requests = kept[i] ? keptRequests : cutRequests;
            foreach (var content in messages[i].Contents)
            {
                switch (content)
                {
                    case ToolCallContent call:
                        calls.Add(call.CallId);
                        break;
                    case ToolApprovalRequestContent request:
                        requests.Add(request.RequestId);
                        calls.Add(request.ToolCall.CallId);
                        break;
                    case InputRequestContent request:
                        requests.Add(request.RequestId);
                        break;
                }
            }
        }

        bool Orphaned(AIContent content) => content switch
        {
            ToolResultContent result => cutCalls.Contains(result.CallId) && !keptCalls.Contains(result.CallId),
            InputResponseContent response => cutRequests.Contains(response.RequestId) && !keptRequests.Contains(response.RequestId),
            _ => false,
        };

        var reduced = new List<ChatMessage>(messages.Count);
        for (var i = 0; i < messages.Count; i++)
        {
            if (!kept[i]) continue;

            var message = messages[i];
            if (!message.Contents.Any(Orphaned))
            {
                reduced.Add(message);
                continue;
            }

            // A copy: the stored message is not ours to change.
            var remaining = message.Contents.Where(c => !Orphaned(c)).ToList();
            if (remaining.Count == 0) continue;
            var copy = message.Clone();
            copy.Contents = remaining;
            reduced.Add(copy);
        }

        return reduced;
    }
}
