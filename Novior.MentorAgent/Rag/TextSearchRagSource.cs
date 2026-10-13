

namespace Novior.MentorAgent.Rag;

/// <summary>
/// Placeholder for a future <c>ITextSearch</c>-based RAG source.
///
/// <c>ITextSearch</c> is not yet available in the current version of
/// <c>Microsoft.Extensions.AI</c>. Developers should implement
/// <see cref="IMentorRagSource"/> directly with their preferred vector-DB client
/// (Azure AI Search, Qdrant, Pinecone, etc.) and register it before
/// <c>AddMentorAgent()</c>:
///
/// <code>
/// builder.Services.AddScoped&lt;IMentorRagSource, MyVectorDbRagSource&gt;();
///
/// builder.Services.AddMentorAgent(options =>
/// {
///     options.UseRag = true;
/// });
/// </code>
/// </summary>
internal sealed class TextSearchRagSource : IMentorRagSource
{
    public Task<IReadOnlyList<MentorRagResult>> SearchAsync(
        string query, int maxResults, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<MentorRagResult>>([]);
}
