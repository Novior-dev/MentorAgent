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
/// Registered by <c>AddMentorAgent()</c>. The vector is the one <em>your</em> generator makes: a source
/// whose documents were embedded with another model passes that model and gets its own vector, computed
/// once per turn as well, never the one of <c>MentorOptions.EmbeddingGenerator</c>.
/// </para>
/// </summary>
public interface IMentorQueryEmbedding
{
    /// <summary>
    /// Returns the vector <paramref name="generator"/> makes of <paramref name="text"/>, generating it only
    /// if that generator has not already been asked for it this turn — the same instance, compared by
    /// reference, so keep the one you pass. An empty text returns an empty vector without calling out.
    /// </summary>
    Task<ReadOnlyMemory<float>> GetAsync(
        string text,
        IEmbeddingGenerator<string, Embedding<float>> generator,
        CancellationToken ct = default);
}
