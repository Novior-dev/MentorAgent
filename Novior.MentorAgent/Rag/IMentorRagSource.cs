

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
/// A collection of <c>Microsoft.Extensions.VectorData</c> — pgvector, Qdrant, Azure AI Search and the others — needs no
/// implementation of its own: register <see cref="MentorVectorStoreRagSource{TRecord}"/> with <c>AddMentorVectorStoreRag</c>.
/// For any other store, call its search client from your own implementation, as above.
///
/// <b>Who is asking, and from where:</b> implement the <see cref="MentorRagRequest"/> overload as well.
/// MentorAgent calls that one; its default implementation calls the text-only overload, so a source
/// written for that keeps working unchanged. With an <c>IMentorTenantResolver</c> the request carries the
/// turn's tenant (<see cref="MentorRagRequest.TenantId"/>), which only that overload sees: a source that
/// holds several tenants' documents implements it and returns the tenant's own, and one that does not is
/// logged once as a warning at its first search with a tenant. A source with cosine scores in [0, 1] declares its own
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

    /// <summary>
    /// The session's conversation changed — a new conversation, a restored one in its place, or one the service no longer
    /// knew replaced (<see cref="MentorConversationChange.Reason"/>) — so a source that keeps state for the conversation
    /// clears it here. Called with <c>UseRag</c> on, on the instance the session searches with: a scoped source clears
    /// its own state; a singleton keys it on <see cref="MentorConversationChange.SessionId"/>. The default does nothing.
    /// </summary>
    /// <remarks>
    /// Keep it quick: it is bounded by <c>SafetyCheckTimeout</c>. A source that throws or does not answer in time is
    /// logged as a warning, and nothing else changes: the conversation has changed already.
    /// </remarks>
    /// <param name="change">Why the conversation changed, for which session and tenant.</param>
    /// <param name="ct">Cancellation token.</param>
    Task OnConversationChangedAsync(MentorConversationChange change, CancellationToken ct = default)
        => Task.CompletedTask;
}
