using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Rag;

namespace Novior.MentorAgent.Tests.Fakes;

/// <summary>
/// An <see cref="IMentorRagSource"/> a test can script — the host's half of the RAG seam.
///
/// <para>
/// A real vector store answers with scores nobody controls, which is fatal for the two things worth
/// asserting here: what crosses the <c>RagMinScore</c> threshold, and what the model is actually
/// shown. This one returns exactly what the test says, and records the query and the result count it
/// was asked for, so "the coordinator searched for the user's message" is an assertion rather than
/// an assumption.
/// </para>
/// </summary>
internal sealed class ScriptedRagSource : IMentorRagSource
{
    private readonly List<MentorRagResult> _results = [];

    /// <summary>Every search, in order: the query and the maximum asked for.</summary>
    internal List<(string Query, int MaxResults)> Searches { get; } = [];

    /// <summary>Thrown by the next search, then cleared — the outage path.</summary>
    internal Exception? ThrowOnNextSearch { get; set; }

    /// <summary>
    /// Returns <see langword="null"/> instead of a list. Not reachable through a correct
    /// implementation, and precisely why a third-party seam needs the try/catch it has.
    /// </summary>
    internal bool ReturnNull { get; set; }

    internal ScriptedRagSource Returns(string content, float score, string? title = null, string? url = null)
    {
        _results.Add(new MentorRagResult(content, url, title, score));
        return this;
    }

    public Task<IReadOnlyList<MentorRagResult>> SearchAsync(
        string query, int maxResults, CancellationToken ct = default)
    {
        Searches.Add((query, maxResults));

        if (ThrowOnNextSearch is { } ex)
        {
            ThrowOnNextSearch = null;
            throw ex;
        }

        return Task.FromResult<IReadOnlyList<MentorRagResult>>(
            ReturnNull ? null! : _results.Take(maxResults).ToList());
    }
}
