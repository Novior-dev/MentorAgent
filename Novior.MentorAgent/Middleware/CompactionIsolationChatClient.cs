using System.Runtime.CompilerServices;

namespace Novior.MentorAgent.Middleware;

/// <summary>
/// Hands history compaction copies of the messages, so it can change what is sent to the model and never
/// the caller's messages or what the history provider stores (BUG-104).
///
/// <para>
/// Compaction is installed on the chat-client pipeline, so it runs on every model call — the tool loop
/// and the UI-action loop included, which send the SAME message instances again. The Agent Framework's
/// <c>CompactionProvider</c> indexes the caller's messages as they are and, on every call after the first,
/// marks each one it has already indexed as chat history, in place (microsoft/agent-framework#8441). On
/// the second call of a run the in-flight question is one of them, and the history provider's default
/// filter declines to store a request message so marked: the tool call, its result and the answer were
/// stored without the question that caused them. Because the index kept the very objects the store kept,
/// the marking also reached messages already stored. Found reading a session snapshot turn by turn on the
/// published rc.13.
/// </para>
///
/// <para>
/// Each message is shallow-copied with its own <see cref="ChatMessage.AdditionalProperties"/> — the only
/// thing the provider writes to; <c>Contents</c> are shared (no strategy mutates them). This is what the
/// upstream fix (microsoft/agent-framework#8461) does inside the provider. Remove this client once the
/// Agent Framework version MentorAgent references ships it — <c>CompactionIsolationTests</c> has a canary
/// that turns red on that day.
/// </para>
/// </summary>
internal sealed class CompactionIsolationChatClient(IChatClient innerClient) : DelegatingChatClient(innerClient)
{
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => base.GetResponseAsync(Copy(messages), options, cancellationToken);

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in base.GetStreamingResponseAsync(Copy(messages), options, cancellationToken))
            yield return update;
    }

    /// <summary>A new list of new message shells, each with a dictionary of its own.</summary>
    internal static List<ChatMessage> Copy(IEnumerable<ChatMessage> messages)
    {
        var copies = messages is ICollection<ChatMessage> c ? new List<ChatMessage>(c.Count) : [];
        foreach (var message in messages)
        {
            var copy = message.Clone();
            copy.AdditionalProperties = message.AdditionalProperties?.Clone();
            copies.Add(copy);
        }
        return copies;
    }
}
