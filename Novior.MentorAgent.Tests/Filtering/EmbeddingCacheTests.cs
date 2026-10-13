using FluentAssertions;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Memory;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Novior.MentorAgent.Tests.Filtering;

/// <summary>
/// One vector per text, not one per consumer.
///
/// <para>
/// Three features embed the <b>same user message</b> on every turn — model routing, semantic tool
/// filtering and memory relevance ranking — and a host's RAG source usually makes a fourth. Measured
/// live on rc.4: two to four network round trips for one sentence, all of them in front of the model
/// call the user is waiting for.
/// </para>
///
/// <para>
/// And the <b>stable</b> texts were worse: the tool catalogue (51 descriptions in the sample) and the
/// routing exemplars are identical for every user, and were re-embedded once per DI scope — once per
/// browser circuit on Blazor Server, once per connection on the hub. Those belong to the process.
/// </para>
/// </summary>
public sealed class EmbeddingCacheTests
{
    private static MentorHostFixture SemanticHost()
    {
        var host = MentorHostFixture.Create(o =>
        {
            o.EnableToolFiltering      = true;
            o.UseMemoryContext         = true;
            o.MemoryRelevanceFiltering = true;
            o.MemoryAutoCapture        = false;   // keeps the turn to one model call
            o.RoutingStrategy          = MentorRoutingStrategy.Semantic;
            o.StrongChatClient         = new FakeChatClient();
        }, withEmbeddings: true);

        return host;
    }

    // ── The message: once per turn ──────────────────────────────────────────────

    [Fact]
    public async Task The_message_is_embedded_once_per_turn_however_many_features_need_it()
    {
        await using var host = SemanticHost();
        host.Chat!.SayRepeatedly("ok", 6);
        (host.Options.StrongChatClient as FakeChatClient)!.SayRepeatedly("ok", 6);

        // A memory to rank, or the relevance path never runs and the test would prove nothing.
        await host.Get<IMentorMemoryStore>().SaveAsync("anonymous", "user_city", "Milano");

        await host.RunTurnAsync("primo messaggio");     // warms catalogue, exemplars and memories
        await host.RunTurnAsync("secondo messaggio");   // warms whatever the first one missed

        var before = host.Embeddings!.CallCount;
        await host.RunTurnAsync("quanti ordini sono aperti?");

        (host.Embeddings.CallCount - before).Should().Be(1,
            "routing, tool filtering and memory relevance all needed the same sentence, and one " +
            "of them embedded it for all three");
    }

    [Fact]
    public async Task A_second_message_is_embedded_again()
    {
        // The control: the cache is the turn's, not the session's. A new message is a new vector,
        // or every turn after the first would be routed and filtered against the wrong sentence.
        await using var host = SemanticHost();
        host.Chat!.SayRepeatedly("ok", 6);
        (host.Options.StrongChatClient as FakeChatClient)!.SayRepeatedly("ok", 6);

        await host.RunTurnAsync("primo messaggio");
        var before = host.Embeddings!.CallCount;
        await host.RunTurnAsync("un messaggio completamente diverso");

        (host.Embeddings.CallCount - before).Should().BeGreaterThan(0);
    }

    // ── The catalogue: once per process ─────────────────────────────────────────

    [Fact]
    public async Task The_tool_catalogue_is_embedded_once_for_the_whole_process()
    {
        await using var host = SemanticHost();
        host.Chat!.SayRepeatedly("ok", 6);
        (host.Options.StrongChatClient as FakeChatClient)!.SayRepeatedly("ok", 6);

        await host.RunTurnAsync("quanti ordini sono aperti?");

        var cache = host.Get<MentorEmbeddingCache>();
        var cachedAfterFirstScope = cache.Count;
        cachedAfterFirstScope.Should().BeGreaterThan(1, "the catalogue and the exemplars are in there");

        var textsBefore = host.Embeddings!.InputCount;

        // A second scope is a second browser circuit: same process, same catalogue.
        await using var scope = host.Get<IServiceScopeFactory>().CreateAsyncScope();
        var second = scope.ServiceProvider.GetRequiredService<Novior.MentorAgent.Abstractions.Interfaces.IMentorOrchestrator>();
        await second.SendMessageAsync("quanti ordini sono aperti?");

        cache.Count.Should().Be(cachedAfterFirstScope,
            "nothing stable was left to embed — the second visitor starts warm");
        (host.Embeddings.InputCount - textsBefore).Should().BeLessThan(5,
            "the second scope embeds its own message, not 50 tool descriptions again");
    }

    // ── The cache itself ────────────────────────────────────────────────────────

    [Fact]
    public async Task Only_the_texts_it_has_never_seen_are_generated()
    {
        var generator = new FakeEmbeddingGenerator();
        var cache = new MentorEmbeddingCache();

        await cache.GetOrAddAsync(["a", "b"], generator);
        var after = generator.InputCount;

        var vectors = await cache.GetOrAddAsync(["b", "c"], generator);

        vectors.Should().HaveCount(2, "the caller gets a vector for every text, cached or not");
        (generator.InputCount - after).Should().Be(1, "only 'c' was missing");
    }

    [Fact]
    public async Task Two_callers_racing_on_the_same_text_generate_it_once()
    {
        // Two circuits opening together is the normal case at deploy time, and it is exactly when
        // the double-checked lock earns its keep.
        var generator = new FakeEmbeddingGenerator();
        var cache = new MentorEmbeddingCache();

        await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => cache.GetOrAddAsync(["stesso testo"], generator)));

        generator.InputCount.Should().Be(1);
    }

    [Fact]
    public async Task The_same_vector_comes_back_for_the_same_text()
    {
        var generator = new FakeEmbeddingGenerator().OnAxis("ordini", 0);
        var cache = new MentorEmbeddingCache();

        var first = (await cache.GetOrAddAsync(["ordini"], generator))[0];
        var second = (await cache.GetOrAddAsync(["ordini"], generator))[0];

        second.ToArray().Should().Equal(first.ToArray());
    }
}
