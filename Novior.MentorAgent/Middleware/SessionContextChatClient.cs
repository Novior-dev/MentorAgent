using System.Runtime.CompilerServices;
using Novior.MentorAgent.Core;

namespace Novior.MentorAgent.Middleware;

/// <summary>
/// Puts the context of the request at the start of the user's last message
/// (<see cref="Options.MentorSessionContextPlacement.LastUserMessage"/>). The context sat at the end of the
/// instructions, before the whole conversation: on every new minute or page a model that caches the prompt by its
/// prefix read the conversation again, which on a CPU can be many seconds before the first word. In the last message
/// only the last exchange is read again.
///
/// <para>
/// On copies: the list, and the messages it changes. The framework keeps the caller's messages as the conversation,
/// so the stored history has the message as the user wrote it. Every request of the turn gets the same block on the
/// same message, so the prefix stays the same inside the turn too. In every user message sent the mark's opening
/// character is made plain, so a user's text cannot pass for the block.
/// </para>
///
/// <para>
/// On the coordinator's own provider clients, the cheap and the strong one, innermost: what reads the user's words —
/// the tool filter, the request classifier, the router — reads them without the block. Never on the host's shared
/// <c>ChatClient</c>, which the classifiers, memory extraction, the specialists and the team members use too.
/// </para>
/// </summary>
internal sealed class SessionContextChatClient(IChatClient innerClient, MentorSessionContext context, ILogger logger)
    : DelegatingChatClient(innerClient)
{
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => base.GetResponseAsync(WithBlock(messages), options, cancellationToken);

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in base.GetStreamingResponseAsync(WithBlock(messages), options, cancellationToken))
            yield return update;
    }

    internal IEnumerable<ChatMessage> WithBlock(IEnumerable<ChatMessage> messages)
    {
        if (!context.InTurn || context.Block is not { } block) return messages;

        var list = messages.ToList();
        // The user's message of the turn: one with words or a picture, not the answers to approval requests.
        var last = list.FindLastIndex(m => m.Role == ChatRole.User && m.Contents.Any(c => c is TextContent or DataContent));
        if (last < 0) return messages;

        for (var i = 0; i < list.Count; i++)
        {
            var message = list[i];
            if (message.Role != ChatRole.User || (i != last && !message.Contents.Any(Imitates))) continue;

            var contents = new List<AIContent>(message.Contents.Count + 1);
            if (i == last) contents.Add(new TextContent(block));
            foreach (var content in message.Contents)
                contents.Add(Imitates(content) ? Plain((TextContent)content) : content);

            var copy = message.Clone();
            copy.AdditionalProperties = message.AdditionalProperties?.Clone();
            copy.RawRepresentation = null;
            copy.Contents = contents;
            list[i] = copy;
        }

        logger.LogDebug("[MentorAgent] Session context: sent at the start of the user's last message ({Mark}).", context.Mark);
        return list;
    }

    private static bool Imitates(AIContent content)
        => content is TextContent { Text: { } text } && text.Contains(MentorSessionContext.MarkCharacter);

    private static TextContent Plain(TextContent text)
        => new(text.Text.Replace(MentorSessionContext.MarkCharacter, '['))
        {
            AdditionalProperties = text.AdditionalProperties,
            Annotations = text.Annotations,
        };
}
