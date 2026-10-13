using System.Diagnostics;
using Novior.MentorAgent.Core;

namespace Novior.MentorAgent.Middleware;

/// <summary>
/// <see cref="DelegatingEmbeddingGenerator{TInput,TEmbedding}"/> that records embedding token usage and
/// latency into <see cref="MentorMetrics"/> per model (roadmap #9 per-model dashboard). Installed by the
/// orchestrator around <c>MentorOptions.EmbeddingGenerator</c> so every embedding consumer — semantic
/// tool filtering, RAG, memory relevance and model routing — is metered under the embedding model id.
/// </summary>
internal sealed class MeteringEmbeddingGenerator : DelegatingEmbeddingGenerator<string, Embedding<float>>
{
    private readonly MentorMetrics _metrics;
    private readonly string? _modelId;

    public MeteringEmbeddingGenerator(IEmbeddingGenerator<string, Embedding<float>> inner, MentorMetrics metrics)
        : base(inner)
    {
        _metrics = metrics;
        _modelId = (inner.GetService(typeof(EmbeddingGeneratorMetadata)) as EmbeddingGeneratorMetadata)?.DefaultModelId;
    }

    public override async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var result = await base.GenerateAsync(values, options, cancellationToken);
        sw.Stop();

        // Embeddings bill on input tokens only; output is always zero.
        long inTok = result.Usage?.InputTokenCount ?? 0;
        _metrics.RecordModelUsage(_modelId, inTok, 0, sw.Elapsed.TotalMilliseconds);

        return result;
    }
}
