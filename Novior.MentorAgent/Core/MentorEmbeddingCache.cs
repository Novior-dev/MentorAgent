using System.Collections.Concurrent;
using Microsoft.Extensions.AI;

namespace Novior.MentorAgent.Core;

/// <summary>
/// Process-wide cache for the embeddings of <b>stable</b> text — tool descriptions and the routing
/// exemplars. One entry per exact text, for the life of the application.
///
/// <para>
/// The texts it holds are identical for every user: the same tool catalogue, the same exemplars. They
/// were being re-embedded once per <em>DI scope</em>, which on Blazor Server is once per browser
/// circuit and on the hub once per connection — 51 descriptions plus 6 exemplars for every visitor,
/// paid in latency on their first message and in tokens on the bill.
/// </para>
///
/// <para>
/// <b>What must never go in here:</b> the user's message. It is neither stable nor shareable, and
/// keeping it in a process-wide dictionary would be both a leak of user text between sessions and an
/// unbounded cache. The per-turn vector belongs to <see cref="TurnQueryEmbedding"/>, which is scoped
/// and forgets it when the scope ends.
/// </para>
/// </summary>
internal sealed class MentorEmbeddingCache
{
    private readonly ConcurrentDictionary<string, ReadOnlyMemory<float>> _stable = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>Entries currently cached — for tests and diagnostics.</summary>
    internal int Count => _stable.Count;

    /// <summary>
    /// Returns the vectors for <paramref name="texts"/>, in order, generating only the ones this
    /// process has not embedded yet. A single generator call covers all the missing ones.
    /// </summary>
    internal async Task<IReadOnlyList<ReadOnlyMemory<float>>> GetOrAddAsync(
        IReadOnlyList<string> texts,
        IEmbeddingGenerator<string, Embedding<float>> generator,
        CancellationToken ct = default)
    {
        if (texts.Count == 0) return [];

        if (Missing(texts) is { Count: > 0 })
        {
            await _lock.WaitAsync(ct);
            try
            {
                // Re-checked under the lock: two circuits opening at once would otherwise both pay
                // for the same catalogue, which is the whole cost this cache exists to remove.
                var missing = Missing(texts);
                if (missing.Count > 0)
                {
                    var generated = await generator.GenerateAsync(missing, cancellationToken: ct);
                    for (var i = 0; i < missing.Count; i++)
                        _stable[missing[i]] = generated[i].Vector;
                }
            }
            finally
            {
                _lock.Release();
            }
        }

        return [.. texts.Select(t => _stable.TryGetValue(t, out var v) ? v : default)];
    }

    private List<string> Missing(IReadOnlyList<string> texts)
        => [.. texts.Distinct(StringComparer.Ordinal).Where(t => !_stable.ContainsKey(t))];
}
