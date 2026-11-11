using System.Runtime.CompilerServices;

namespace Novior.MentorAgent.Middleware;

/// <summary>
/// The second pass of a turn whose reply announced or reported an action that did not run (story 526, BUG-220). The
/// next request of the coordinator after <see cref="Arm"/> ends with the instruction: call the tool now, or say that it
/// was not done.
///
/// <para>
/// As the last message of that request: the model reads it after its own reply, the last thing it read before. In the
/// instructions, which Chat Completions sends as the system message at the head of the conversation, the live A/B saw
/// the model leave it unanswered. A message of the user, not a system message (BUG-231): many chat templates — Mistral's
/// and Ministral's, Gemma's, others served by llama-server or vLLM — take a system message only as the first, and
/// answered the request with an error, so the check never reached its second request. After the reply, a user message
/// keeps the user and assistant turns alternating, which those templates require too; the heading says it is
/// MentorAgent's. Below the function-invoking client, so neither the agent's history nor
/// that client's list of messages ever holds it, and only for that one request: the function-invoking client calls this
/// one again with the tool's result, and a model told once more to call the tool could call it twice. With the history
/// kept by the service (a request with a <see cref="ChatOptions.ConversationId"/>), every message of a request is stored
/// there, so the instruction stays in the instructions, as the notice of an action left out does (BUG-200).
/// </para>
///
/// <para>
/// Nothing else changes: the same tools, and the tool mode the request already had, never a required one — an action
/// that asks no one first must not run on a classification that may be wrong.
/// </para>
///
/// <para>
/// Inside the tool filter, so the names it keeps for the turn (<see cref="Offered"/>) are those of the tools the model
/// was sent, which the classifier may name.
/// </para>
/// </summary>
internal sealed class AnnouncedActionChatClient(IChatClient innerClient, ILogger? logger = null) : DelegatingChatClient(innerClient)
{
    /// <summary>The heading, English in every language as the other headings of the prompts.</summary>
    internal const string Heading = "## No action ran in this turn — MANDATORY for THIS request\n";

    private string? _instruction;

    private readonly HashSet<string> _offered = new(StringComparer.Ordinal);

    /// <summary>A user turn begins: the tools of the requests to come are this turn's.</summary>
    internal void BeginTurn()
    {
        lock (_offered) _offered.Clear();
    }

    /// <summary>The names of the tools the requests of this turn were sent with.</summary>
    internal IReadOnlyList<string> Offered
    {
        get
        {
            lock (_offered) return [.. _offered];
        }
    }

    /// <summary>The next request ends with <paramref name="instruction"/>, the heading included.</summary>
    internal void Arm(string instruction) => Volatile.Write(ref _instruction, instruction);

    /// <summary>No request is given the instruction: the second pass is over, whether its first request was sent or not.</summary>
    internal void Disarm() => Volatile.Write(ref _instruction, null);

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var (withMessages, withOptions) = WithInstruction(messages, options);
        return base.GetResponseAsync(withMessages, withOptions, cancellationToken);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (withMessages, withOptions) = WithInstruction(messages, options);
        await foreach (var update in base.GetStreamingResponseAsync(withMessages, withOptions, cancellationToken))
            yield return update;
    }

    /// <summary>
    /// The request with the instruction at its end, once: a new list of messages, or a copy of the options with the
    /// service's history, since the function-invoking client keeps its own. Unchanged outside the second pass, so the
    /// cached prefix of the prompt stays as it is; on that one request the message at the end leaves the prefix as it
    /// is too.
    /// </summary>
    private (IEnumerable<ChatMessage> Messages, ChatOptions? Options) WithInstruction(
        IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        if (options?.Tools is { Count: > 0 } tools)
            lock (_offered) _offered.UnionWith(tools.Select(t => t.Name));

        if (Interlocked.Exchange(ref _instruction, null) is not { } instruction) return (messages, options);

        // What the model is given to act with on the second request: whether it could have called the tool it reported.
        logger?.LogDebug(
            "[MentorAgent] Announced-action check: the second request carries {Count} tool(s) ({Names}), tool mode {Mode}.",
            options?.Tools?.Count ?? 0,
            string.Join(", ", options?.Tools?.Select(t => t.Name) ?? []),
            options?.ToolMode?.ToString() ?? "default");

        if (options?.ConversationId is not null)
        {
            var copy = options.Clone();
            copy.Instructions = string.IsNullOrEmpty(copy.Instructions) ? instruction : copy.Instructions + "\n\n" + instruction;
            return (messages, copy);
        }

        // A user message, never a system one: a system message that is not the first is refused by many templates (BUG-231).
        return ([.. messages, new ChatMessage(ChatRole.User, instruction)], options);
    }
}
