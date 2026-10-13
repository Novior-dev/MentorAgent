

namespace Novior.MentorAgent.Rag;

/// <summary>
/// No-op <see cref="IMentorRagSource"/> used as the default when RAG is not configured.
/// Avoids null checks in <see cref="Novior.MentorAgent.Core.RagContextProvider"/>.
/// </summary>
internal sealed class NullRagSource : IMentorRagSource
{
    public Task<IReadOnlyList<MentorRagResult>> SearchAsync(
        string query, int maxResults, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<MentorRagResult>>([]);
}
