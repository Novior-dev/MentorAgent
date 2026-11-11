using FluentAssertions;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Rag;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Novior.MentorAgent.Tests.Rag;

/// <summary>
/// BUG-229 — the turn's vector of the message (<see cref="IMentorQueryEmbedding"/>) was kept on the text alone: a source
/// with an embedding model of its own was handed the vector of <c>MentorOptions.EmbeddingGenerator</c>, or the other way
/// round, whichever asked first. Each generator now has its own entry, compared by reference, and the entries are capped
/// at <see cref="TurnQueryEmbedding.MaxGenerators"/>, so a host that builds a generator for every call cannot make the
/// cache grow.
/// </summary>
public sealed class QueryEmbeddingTests
{
    private const string Message = "how long do refunds take?";

    [Fact]
    public async Task The_same_message_with_two_generators_gets_each_models_own_vector()
    {
        using var cache = new TurnQueryEmbedding();
        var mentor = new FakeEmbeddingGenerator().OnAxis(Message, 0);
        var other = new FakeEmbeddingGenerator().OnAxis(Message, 3);

        var first = await cache.GetAsync(Message, mentor);
        var second = await cache.GetAsync(Message, other);
        await cache.GetAsync(Message, mentor);
        await cache.GetAsync(Message, other);

        first.Span[0].Should().Be(1f);
        second.Span[3].Should().Be(1f, "the other model's vector, not the first one's");
        mentor.CallCount.Should().Be(1, "once per turn for each model");
        other.CallCount.Should().Be(1);
        cache.Entries.Should().Be(2);
    }

    [Fact]
    public async Task A_new_message_replaces_its_generators_entry_and_leaves_the_others_alone()
    {
        using var cache = new TurnQueryEmbedding();
        var mentor = new FakeEmbeddingGenerator();
        var other = new FakeEmbeddingGenerator();
        await cache.GetAsync(Message, mentor);
        await cache.GetAsync(Message, other);

        await cache.GetAsync("and for opened items?", mentor);

        cache.Entries.Should().Be(2, "the mentor generator's new message took the place of its old one");
        await cache.GetAsync(Message, other);
        other.CallCount.Should().Be(1, "the other generator's entry was kept");
        await cache.GetAsync(Message, mentor);
        mentor.CallCount.Should().Be(3, "its old message was replaced");
    }

    [Fact]
    public async Task A_generator_built_for_every_call_cannot_make_the_cache_grow()
    {
        using var cache = new TurnQueryEmbedding();
        var oldest = new FakeEmbeddingGenerator();
        await cache.GetAsync(Message, oldest);

        FakeEmbeddingGenerator? last = null;
        for (var i = 0; i < 10; i++)
        {
            last = new FakeEmbeddingGenerator();
            await cache.GetAsync(Message, last);
            cache.Entries.Should().BeLessThanOrEqualTo(TurnQueryEmbedding.MaxGenerators);
        }

        cache.Entries.Should().Be(TurnQueryEmbedding.MaxGenerators);
        await cache.GetAsync(Message, last!);
        last!.CallCount.Should().Be(1, "the newest entry is kept");
        await cache.GetAsync(Message, oldest);
        oldest.CallCount.Should().Be(2, "the oldest went first");
    }

    // ── In a turn ────────────────────────────────────────────────────────────────

    /// <summary>A generator that records every text it embeds.</summary>
    private sealed class RecordingGenerator(FakeEmbeddingGenerator inner) : IEmbeddingGenerator<string, Embedding<float>>
    {
        internal List<string> Inputs { get; } = [];

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            var list = values.ToList();
            Inputs.AddRange(list);
            return inner.GenerateAsync(list, options, cancellationToken);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    /// <summary>A host's source whose documents were embedded with a model of its own.</summary>
    private sealed class OwnModelSource(IMentorQueryEmbedding query, IEmbeddingGenerator<string, Embedding<float>> own) : IMentorRagSource
    {
        internal List<ReadOnlyMemory<float>> Vectors { get; } = [];

        public async Task<IReadOnlyList<MentorRagResult>> SearchAsync(MentorRagRequest request, CancellationToken ct = default)
        {
            Vectors.Add(await query.GetAsync(request.Query, own, ct));
            return [];
        }

        public Task<IReadOnlyList<MentorRagResult>> SearchAsync(string query, int maxResults, CancellationToken ct = default)
            => throw new InvalidOperationException("MentorAgent calls the request overload.");
    }

    [Fact]
    public async Task A_source_with_its_own_model_and_the_tool_filter_each_get_their_models_vector_in_one_turn()
    {
        var mentor = new RecordingGenerator(new FakeEmbeddingGenerator().OnAxis(Message, 0));
        var own = new FakeEmbeddingGenerator().OnAxis(Message, 3);
        await using var host = MentorHostFixture.Create(o =>
        {
            o.UseRag = true;
            o.MemoryAutoCapture = false;
            o.EnableToolFiltering = true;
            o.EmbeddingGenerator = mentor;
        }, s => s.AddScoped<IMentorRagSource>(sp => new OwnModelSource(sp.GetRequiredService<IMentorQueryEmbedding>(), own)));
        host.Chat!.Say("14 days.");

        await host.RunTurnAsync(Message);

        var source = (OwnModelSource)host.Get<IMentorRagSource>();
        source.Vectors.Should().ContainSingle().Which.Span[3].Should().Be(1f, "the source's own model");
        own.CallCount.Should().Be(1);
        mentor.Inputs.Count(t => t == Message).Should().Be(1,
            "the tool filter embedded the message with MentorOptions.EmbeddingGenerator, not with the source's vector");
    }
}
