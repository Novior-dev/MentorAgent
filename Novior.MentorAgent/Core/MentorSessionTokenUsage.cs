namespace Novior.MentorAgent.Core;

/// <summary>
/// The chat-model tokens of one session (story 171): the calls <see cref="MentorTokenUsage"/> counts for the whole
/// process, counted again for the scope they were made in — the circuit, the MAUI app's scope, the hub connection,
/// an evaluated conversation.
///
/// <para>
/// Scoped, and fed by every metered client the session's scope builds: the coordinator's pipeline, the classifiers
/// and the agents under the coordinator. <see cref="Evaluation.MentorEvaluator.RunConversationsAsync"/> reads a
/// turn's tokens as the difference across the turn, which a process-wide counter cannot give while other sessions
/// are talking. Embedding calls are not in it: the embedding cache is shared by the whole process.
/// </para>
/// </summary>
internal sealed class MentorSessionTokenUsage
{
    private long _input;
    private long _output;
    private long _requests;

    /// <summary>Input tokens of the session's model calls so far.</summary>
    internal long InputTokens => Interlocked.Read(ref _input);

    /// <summary>Output tokens of the session's model calls so far.</summary>
    internal long OutputTokens => Interlocked.Read(ref _output);

    /// <summary>The session's model calls so far.</summary>
    internal long RequestCount => Interlocked.Read(ref _requests);

    /// <summary>Records one model round-trip of the session.</summary>
    internal void Record(long inputTokens, long outputTokens)
    {
        Interlocked.Add(ref _input, inputTokens);
        Interlocked.Add(ref _output, outputTokens);
        Interlocked.Increment(ref _requests);
    }
}
