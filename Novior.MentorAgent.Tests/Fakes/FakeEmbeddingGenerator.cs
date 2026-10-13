using Microsoft.Extensions.AI;

namespace Novior.MentorAgent.Tests.Fakes;

/// <summary>
/// A deterministic <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/>.
///
/// <para>
/// Semantic tool filtering, semantic routing and relevance-ranked memory all turn on a cosine
/// similarity compared against a threshold. Testing those against a real embedding model would make
/// the assertions depend on a number nobody controls — and, worse, on a number that is not
/// calibrated: unrelated short phrases routinely score around 0.4, so a "clearly irrelevant" case
/// can sit above a 0.35 threshold for reasons that have nothing to do with the code under test.
/// </para>
///
/// <para>
/// This generator lets a test <em>state</em> the geometry: register the vectors, and the similarity
/// between any two inputs is then exactly what the test intended. Unregistered text gets a stable
/// hash-derived vector, so the same string always embeds the same way across runs.
/// </para>
/// </summary>
internal sealed class FakeEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    internal const int Dimensions = 8;

    private readonly Dictionary<string, float[]> _seeded = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Number of embedding calls — used to assert the metering path counts them.</summary>
    internal int CallCount { get; private set; }

    /// <summary>Total inputs embedded across all calls (a call may batch).</summary>
    internal int InputCount { get; private set; }

    /// <summary>
    /// Pins <paramref name="text"/> to a unit vector pointing along <paramref name="axis"/>.
    /// Two texts on the same axis have cosine 1; on different axes, 0. That makes "clearly
    /// related" and "clearly unrelated" exact rather than approximate.
    /// </summary>
    internal FakeEmbeddingGenerator OnAxis(string text, int axis)
    {
        var v = new float[Dimensions];
        v[axis % Dimensions] = 1f;
        _seeded[text] = v;
        return this;
    }

    /// <summary>
    /// Pins <paramref name="text"/> at a chosen cosine similarity to <paramref name="axis"/>, so a
    /// test can sit deliberately just above or just below a threshold.
    /// </summary>
    internal FakeEmbeddingGenerator AtSimilarity(string text, int axis, float cosine)
    {
        var v = new float[Dimensions];
        var a = axis % Dimensions;
        var other = (a + 1) % Dimensions;

        v[a] = cosine;
        v[other] = MathF.Sqrt(MathF.Max(0f, 1f - (cosine * cosine)));
        _seeded[text] = v;
        return this;
    }

    /// <summary>Pins an exact vector.</summary>
    internal FakeEmbeddingGenerator Exactly(string text, params float[] vector)
    {
        _seeded[text] = vector;
        return this;
    }

    /// <summary>
    /// Thrown by the next call, then cleared. Embedding providers rate-limit and time out, and the
    /// library's response to that — fail <em>open</em>, keep every tool, warn — is a deliberate
    /// choice that cannot be reached without a way to make the generator fail.
    /// </summary>
    internal Exception? ThrowOnNextCall { get; set; }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        CallCount++;

        if (ThrowOnNextCall is { } ex)
        {
            ThrowOnNextCall = null;
            throw ex;
        }

        var result = new GeneratedEmbeddings<Embedding<float>>();
        foreach (var value in values)
        {
            InputCount++;
            result.Add(new Embedding<float>(Vector(value)));
        }

        return Task.FromResult(result);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }

    private float[] Vector(string text)
    {
        if (_seeded.TryGetValue(text, out var seeded)) return seeded;

        // Unregistered input: stable across runs and across processes. A plain string hash is not
        // (it is randomised per process by default), which would make a threshold test flaky in
        // exactly the way this class exists to prevent.
        var v = new float[Dimensions];
        unchecked
        {
            var h = 2166136261u;
            foreach (var c in text)
            {
                h = (h ^ c) * 16777619u;
            }

            for (var i = 0; i < Dimensions; i++)
            {
                h = (h ^ (uint)i) * 16777619u;
                v[i] = ((h % 1000u) / 1000f) - 0.5f;
            }
        }

        Normalise(v);
        return v;
    }

    private static void Normalise(float[] v)
    {
        var length = MathF.Sqrt(v.Sum(x => x * x));
        if (length == 0f) return;
        for (var i = 0; i < v.Length; i++) v[i] /= length;
    }
}
