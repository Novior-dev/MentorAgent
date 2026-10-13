

namespace Novior.MentorAgent.Rag;

/// <summary>
/// Contract for RAG (Retrieval-Augmented Generation) sources in MentorAgent.
///
/// Implement this interface to connect any vector store or search backend
/// (Azure AI Search, Qdrant, Pinecone, Weaviate, custom, etc.) to the AI coordinator.
///
/// <b>Basic custom implementation:</b>
/// <code>
/// public class MyRagSource : IMentorRagSource
/// {
///     public async Task&lt;IReadOnlyList&lt;MentorRagResult&gt;&gt; SearchAsync(
///         string query, int maxResults, CancellationToken ct = default)
///     {
///         var hits = await _myStore.SearchAsync(query, maxResults, ct);
///         return hits.Select(h => new MentorRagResult(h.Text, h.Url, h.Title, h.Score)).ToList();
///     }
/// }
///
/// // Register in Program.cs BEFORE AddMentorAgent():
/// builder.Services.AddSingleton&lt;IMentorRagSource, MyRagSource&gt;();
/// builder.Services.AddMentorAgent(options => { options.UseRag = true; });
/// </code>
///
/// An <c>ITextSearch</c> is not wrapped for you (<c>TextSearchRagSource</c> is a placeholder): call your
/// search client from your own implementation, as above.
///
/// <b>Who is asking, and from where:</b> implement the <see cref="MentorRagRequest"/> overload as well.
/// MentorAgent calls that one; its default implementation calls the text-only overload, so a source
/// written for that keeps working unchanged. A source with cosine scores in [0, 1] declares its own
/// threshold with <see cref="DefaultMinScore"/>:
/// <code>
/// public class MyVectorSource : IMentorRagSource
/// {
///     public float? DefaultMinScore => 0.6f;   // cosine similarity: 0.5 loose … 0.75 strict
///
///     public Task&lt;IReadOnlyList&lt;MentorRagResult&gt;&gt; SearchAsync(
///         MentorRagRequest request, CancellationToken ct = default)
///         => SearchCoreAsync(request.Query, request.MaxResults, request.User, ct);
///
///     // Still required by the interface; MentorAgent itself calls the overload above.
///     public Task&lt;IReadOnlyList&lt;MentorRagResult&gt;&gt; SearchAsync(
///         string query, int maxResults, CancellationToken ct = default)
///         => SearchCoreAsync(query, maxResults, user: null, ct);
/// }
/// </code>
/// Do not make each overload call the other: with both delegating, a search never returns.
/// </summary>
public interface IMentorRagSource
{
    /// <summary>
    /// Searches for documents relevant to the given query.
    /// Called by the coordinator before each user message is sent to the AI, through the
    /// <see cref="MentorRagRequest"/> overload unless the source replaces that one.
    /// </summary>
    /// <param name="query">The user's message (or a reformulated search query).</param>
    /// <param name="maxResults">Maximum number of results to return.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of relevant documents ordered by relevance (most relevant first).</returns>
    Task<IReadOnlyList<MentorRagResult>> SearchAsync(
        string query,
        int maxResults,
        CancellationToken ct = default);

    /// <summary>
    /// Searches with the whole request: the message, the recent conversation, the user and the current
    /// page. This is the overload MentorAgent calls. The default implementation calls
    /// <see cref="SearchAsync(string, int, CancellationToken)"/> with <see cref="MentorRagRequest.Query"/>
    /// and <see cref="MentorRagRequest.MaxResults"/>.
    /// </summary>
    /// <param name="request">What is searched for, and the context it was asked in.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of relevant documents ordered by relevance (most relevant first).</returns>
    Task<IReadOnlyList<MentorRagResult>> SearchAsync(
        MentorRagRequest request,
        CancellationToken ct = default)
        => SearchAsync(request.Query, request.MaxResults, ct);

    /// <summary>
    /// The relevance threshold suited to this source's scores, used when the host has not set
    /// <c>RagMinScore</c> itself. <see langword="null"/> (the default) leaves the option's default, 2,
    /// which suits integer-like keyword scores. A source whose scores are cosine similarity in [0, 1] — a
    /// vector store — declares one in that range (0.5 loose to 0.75 strict), or nothing it returns can
    /// clear the default. A <c>RagMinScore</c> the host sets, in code or in configuration, wins over it.
    /// </summary>
    float? DefaultMinScore => null;
}
