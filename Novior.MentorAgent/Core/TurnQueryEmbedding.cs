using Microsoft.Extensions.AI;

namespace Novior.MentorAgent.Core;

/// <summary>
/// The embedding of the message being answered right now, computed once and shared by everything in
/// the turn that needs it: model routing, semantic tool filtering and memory relevance ranking (and a
/// host's RAG source, through <see cref="Novior.MentorAgent.Rag.IMentorQueryEmbedding"/>).
///
/// <para>
/// Each of those used to embed the same sentence on its own — measured live, two to four network
/// round trips per turn for one string, in front of the model call the user is waiting for.
/// </para>
///
/// <para>
/// Scoped, and it holds one entry: the current message. Nothing the user typed outlives their
/// session here, and a second message replaces the first rather than accumulating.
/// </para>
/// </summary>
internal sealed class TurnQueryEmbedding : Rag.IMentorQueryEmbedding, IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _text;
    private ReadOnlyMemory<float> _vector;

    /// <summary>Generator calls this instance actually made — for tests.</summary>
    internal int Generated { get; private set; }

    public async Task<ReadOnlyMemory<float>> GetAsync(
        string text,
        IEmbeddingGenerator<string, Embedding<float>> generator,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(text)) return default;
        if (string.Equals(_text, text, StringComparison.Ordinal)) return _vector;

        await _lock.WaitAsync(ct);
        try
        {
            if (string.Equals(_text, text, StringComparison.Ordinal)) return _vector;

            var generated = await generator.GenerateAsync([text], cancellationToken: ct);
            _vector = generated[0].Vector;
            _text = text;
            Generated++;
            return _vector;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose() => _lock.Dispose();
}
