using Microsoft.Extensions.AI;

namespace Novior.MentorAgent.Rag;

/// <summary>
/// The embedding of the message MentorAgent is answering right now, computed once per turn.
///
/// <para>
/// Model routing, semantic tool filtering and memory relevance already share it. A host's
/// <see cref="IMentorRagSource"/> is the fourth consumer of the same sentence and the only one
/// outside the library, so it is offered here: ask for the vector instead of embedding the query
/// again, and the turn makes one round trip instead of two.
/// </para>
///
/// <code>
/// public sealed class MyRagSource(IMentorQueryEmbedding query, IEmbeddingGenerator&lt;string, Embedding&lt;float&gt;&gt; embeddings)
///     : IMentorRagSource
/// {
///     public async Task&lt;IReadOnlyList&lt;MentorRagResult&gt;&gt; SearchAsync(string q, int maxResults, CancellationToken ct = default)
///     {
///         var vector = await query.GetAsync(q, embeddings, ct);   // free if something already embedded it
///         return Search(vector, maxResults);
///     }
/// }
/// </code>
///
/// <para>
/// Scoped: the vector belongs to the session that asked for it and is replaced by the next message.
/// Registered by <c>AddMentorAgent()</c>; the generator you pass is the one used on a cache miss.
/// </para>
/// </summary>
public interface IMentorQueryEmbedding
{
    /// <summary>
    /// Returns the vector for <paramref name="text"/>, generating it only if it is not the one
    /// already computed for this turn. An empty text returns an empty vector without calling out.
    /// </summary>
    Task<ReadOnlyMemory<float>> GetAsync(
        string text,
        IEmbeddingGenerator<string, Embedding<float>> generator,
        CancellationToken ct = default);
}
