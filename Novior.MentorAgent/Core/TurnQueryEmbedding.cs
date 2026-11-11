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
/// Scoped, and it holds one entry per generator: the current message and its vector from that model. A
/// vector is only ever handed to the generator that made it — keyed on the text alone, a source with a
/// model of its own was given the other model's vector (BUG-229). Nothing the user typed outlives their
/// session here: a second message replaces the first, and past <see cref="MaxGenerators"/> the oldest
/// entry goes, so a host that builds a generator for every call cannot make it grow.
/// </para>
/// </summary>
internal sealed class TurnQueryEmbedding : Rag.IMentorQueryEmbedding, IDisposable
{
    /// <summary>
    /// The generators remembered at once. The library's own features share one; a host's source may add
    /// one or two.
    /// </summary>
    internal const int MaxGenerators = 4;

    private sealed record Entry(IEmbeddingGenerator<string, Embedding<float>> Generator, string Text, ReadOnlyMemory<float> Vector);

    private readonly SemaphoreSlim _lock = new(1, 1);

    // Replaced whole under the lock, read without it: a reader sees the old array or the new one.
    private volatile Entry[] _entries = [];

    /// <summary>Generator calls this instance actually made — for tests.</summary>
    internal int Generated { get; private set; }

    /// <summary>The entries held now — for tests.</summary>
    internal int Entries => _entries.Length;

    public async Task<ReadOnlyMemory<float>> GetAsync(
        string text,
        IEmbeddingGenerator<string, Embedding<float>> generator,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(text)) return default;
        ArgumentNullException.ThrowIfNull(generator);
        if (Find(_entries, text, generator) is { } hit) return hit.Vector;

        await _lock.WaitAsync(ct);
        try
        {
            var entries = _entries;
            if (Find(entries, text, generator) is { } again) return again.Vector;

            var generated = await generator.GenerateAsync([text], cancellationToken: ct);
            var entry = new Entry(generator, text, generated[0].Vector);

            // The generator's new message replaces its old one; the others keep theirs, oldest first, as many
            // as fit beside this one.
            var others = entries.Where(e => !ReferenceEquals(e.Generator, generator)).ToList();
            _entries = [.. others.Skip(Math.Max(0, others.Count - (MaxGenerators - 1))), entry];
            Generated++;
            return entry.Vector;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static Entry? Find(Entry[] entries, string text, IEmbeddingGenerator<string, Embedding<float>> generator)
    {
        foreach (var e in entries)
        {
            if (ReferenceEquals(e.Generator, generator) && string.Equals(e.Text, text, StringComparison.Ordinal))
                return e;
        }

        return null;
    }

    public void Dispose() => _lock.Dispose();
}
