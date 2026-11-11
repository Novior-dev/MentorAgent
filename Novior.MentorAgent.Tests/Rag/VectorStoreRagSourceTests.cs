using System.Runtime.CompilerServices;
using FluentAssertions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Rag;
using Novior.MentorAgent.Tests.App;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.VectorData;
using Xunit;

namespace Novior.MentorAgent.Tests.Rag;

/// <summary>
/// Story 159 — the library had no source for a vector store: <see cref="MentorVectorStoreRagSource{TRecord}"/> reads any
/// collection of <c>Microsoft.Extensions.VectorData</c>, registered with <c>AddMentorVectorStoreRag</c>. Its documents
/// reach the model and the citation chips with their title, link and metadata. A collection whose scores are distances —
/// <c>CosineDistance</c>, the Postgres default — is read the right way round: a raw score would keep the farthest
/// documents and drop the nearest. The archive here is an in-memory collection with a real cosine.
/// </summary>
public sealed class VectorStoreRagSourceTests
{
    // ── The in-memory archive ────────────────────────────────────────────────────

    /// <summary>A record as an application would declare it for pgvector: the distance on the vector's attribute.</summary>
    private sealed class Doc
    {
        [VectorStoreKey] public int Id { get; init; }
        [VectorStoreData] public string Text { get; init; } = "";
        [VectorStoreData] public string? Title { get; init; }
        [VectorStoreData] public string? Url { get; init; }
        [VectorStoreData] public string? Tenant { get; init; }
        [VectorStoreData] public string? ValidFrom { get; init; }

        [VectorStoreVector(FakeEmbeddingGenerator.Dimensions, DistanceFunction = DistanceFunction.CosineDistance)]
        public ReadOnlyMemory<float> Vector { get; init; }
    }

    private sealed class DocWithoutDistance
    {
        [VectorStoreKey] public int Id { get; init; }
        [VectorStoreVector(FakeEmbeddingGenerator.Dimensions)] public ReadOnlyMemory<float> Vector { get; init; }
    }

    private sealed class DocWithoutVector
    {
        public string Text { get; init; } = "";
    }

    private sealed class DocWithTwoVectors
    {
        [VectorStoreVector(FakeEmbeddingGenerator.Dimensions, DistanceFunction = DistanceFunction.CosineSimilarity)]
        public ReadOnlyMemory<float> TitleVector { get; init; }

        [VectorStoreVector(FakeEmbeddingGenerator.Dimensions, DistanceFunction = DistanceFunction.CosineDistance)]
        public ReadOnlyMemory<float> BodyVector { get; init; }
    }

    /// <summary>
    /// A collection with a real cosine, in the collection's order (by relevance), with the score its distance function
    /// gives. A text input is embedded with its own generator, as a real collection configured with one does; without
    /// one it fails, as a real one does.
    /// </summary>
    private sealed class InMemoryCollection(Func<double, double> scoreOf, IEmbeddingGenerator<string, Embedding<float>>? own = null)
        : IVectorSearchable<Doc>
    {
        internal static InMemoryCollection Similarity(IEmbeddingGenerator<string, Embedding<float>>? own = null) => new(c => c, own);
        internal static InMemoryCollection Distance() => new(c => 1d - c);
        internal static InMemoryCollection NegatedDotProduct() => new(c => -c);

        internal List<Doc> Records { get; } = [];
        internal List<object> Inputs { get; } = [];
        internal List<int> Tops { get; } = [];
        internal List<VectorSearchOptions<Doc>?> Searches { get; } = [];
        internal bool WithoutScores { get; set; }
        internal Exception? Throws { get; set; }

        public async IAsyncEnumerable<VectorSearchResult<Doc>> SearchAsync<TInput>(
            TInput searchValue, int top, VectorSearchOptions<Doc>? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
            where TInput : notnull
        {
            Inputs.Add(searchValue);
            Tops.Add(top);
            Searches.Add(options);
            if (Throws is { } ex) throw ex;

            ReadOnlyMemory<float> query = searchValue switch
            {
                ReadOnlyMemory<float> vector => vector,
                string text when own is not null => (await own.GenerateAsync([text], cancellationToken: cancellationToken))[0].Vector,
                _ => throw new InvalidOperationException($"No embedding generator configured for a {typeof(TInput).Name} input."),
            };
            var filter = options?.Filter?.Compile();
            var hits = Records
                .Where(r => filter?.Invoke(r) ?? true)
                .Select(r => (Record: r, Cosine: Cosine(query.Span, r.Vector.Span)))
                .OrderByDescending(h => h.Cosine)
                .Take(top);
            foreach (var (record, cosine) in hits)
                yield return new VectorSearchResult<Doc>(record, WithoutScores ? null : scoreOf(cosine));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        private static double Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
        {
            if (a.Length != b.Length || a.Length == 0) return 0d;
            double dot = 0, na = 0, nb = 0;
            for (var i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
            return na == 0 || nb == 0 ? 0d : dot / Math.Sqrt(na * nb);
        }
    }

    /// <summary>A unit vector on <paramref name="axis"/>, tilted so that its cosine with that axis is <paramref name="cosine"/>.</summary>
    private static ReadOnlyMemory<float> Near(int axis, float cosine = 1f)
    {
        var v = new float[FakeEmbeddingGenerator.Dimensions];
        v[axis] = cosine;
        v[(axis + 1) % v.Length] = MathF.Sqrt(MathF.Max(0f, 1f - (cosine * cosine)));
        return v;
    }

    private const string Question = "how long do refunds take?";

    /// <summary>A reply that cites the first two documents: only cited documents become chips.</summary>
    private const string CitingReply = "Refunds take 14 days [1]; opened items are refunded at 80% [2].";

    /// <summary>Three documents at cosine 1, 0.7 and 0 from the question, which lies on axis 0.</summary>
    private static InMemoryCollection WithPolicies(InMemoryCollection collection)
    {
        collection.Records.Add(new Doc { Id = 1, Text = "Refunds take 14 days.", Title = "Refund policy", Url = "https://docs/refunds", ValidFrom = "2026-01-01", Vector = Near(0) });
        collection.Records.Add(new Doc { Id = 2, Text = "Opened items are refunded at 80%.", Title = "Opened items", Url = "https://docs/opened", Vector = Near(0, 0.7f) });
        collection.Records.Add(new Doc { Id = 3, Text = "Our offices open at 9.", Title = "Office hours", Vector = Near(3) });
        return collection;
    }

    private static MentorVectorStoreRagOptions<Doc> Reading(Action<MentorVectorStoreRagOptionsBuilder>? tweak = null)
    {
        var b = new MentorVectorStoreRagOptionsBuilder();
        tweak?.Invoke(b);
        return new MentorVectorStoreRagOptions<Doc>
        {
            Content = d => d.Text,
            Title = d => d.Title,
            SourceUrl = d => d.Url,
            Metadata = d => d.ValidFrom is { } from ? new Dictionary<string, string> { ["valid from"] = from } : null,
            Filter = b.Filter,
            DistanceFunction = b.DistanceFunction,
            DefaultMinScore = b.DefaultMinScore,
            EmbeddingGenerator = b.EmbeddingGenerator,
        };
    }

    private sealed class MentorVectorStoreRagOptionsBuilder
    {
        internal Func<MentorRagRequest, System.Linq.Expressions.Expression<Func<Doc, bool>>?>? Filter { get; set; }
        internal string? DistanceFunction { get; set; }
        internal float DefaultMinScore { get; set; } = 0.5f;
        internal IEmbeddingGenerator<string, Embedding<float>>? EmbeddingGenerator { get; set; }
    }

    private static MentorHostFixture Host(
        InMemoryCollection collection, MentorVectorStoreRagOptions<Doc> options,
        Action<MentorOptions>? configure = null, bool withEmbeddings = true, bool resolver = false)
        => MentorHostFixture.Create(o =>
        {
            o.UseRag = true;
            o.MemoryAutoCapture = false;
            configure?.Invoke(o);
        }, s =>
        {
            s.AddMentorVectorStoreRag(_ => collection, options);
            if (resolver) s.AddMentorTenantResolver<TestTenantResolver>();
        }, withEmbeddings: withEmbeddings, withRecorder: true);

    private static string FirstPrompt(MentorHostFixture host)
        => (host.Chat!.Calls[0].Instructions ?? "") + "\n" + host.Chat.Calls[0].SystemText;

    private static IReadOnlyList<MentorRagResult> ChipsOf(MentorHostFixture host)
        => host.Recorder!.Log
            .Where(e => e.Event == nameof(RecordingStateService.NotifyRagSourcesReady))
            .Select(e => (IReadOnlyList<MentorRagResult>)e.Args[0]!)
            .LastOrDefault() ?? [];

    // ── A turn with citations ────────────────────────────────────────────────────

    [Fact]
    public async Task A_turn_shows_the_model_the_nearest_documents_and_cites_them()
    {
        var collection = WithPolicies(InMemoryCollection.Similarity());
        await using var host = Host(collection, Reading(b => b.DistanceFunction = DistanceFunction.CosineSimilarity));
        host.Embeddings!.OnAxis(Question, 0);
        host.Chat!.Say(CitingReply);

        await host.RunTurnAsync(Question);

        FirstPrompt(host).Should().Contain("[1] Refund policy\n(valid from: 2026-01-01)\nRefunds take 14 days.")
            .And.Contain("Opened items are refunded at 80%.")
            .And.NotContain("Our offices open at 9.", "cosine 0 is below the source's 0.5");
        var chips = ChipsOf(host);
        chips.Select(c => c.Title).Should().Equal("Refund policy", "Opened items");
        chips[0].SourceUrl.Should().Be("https://docs/refunds");
        chips[0].Metadata.Should().Contain("valid from", "2026-01-01");
        chips[0].Score.Should().BeApproximately(1f, 1e-4f);
    }

    [Fact]
    public async Task A_cosine_distance_collection_like_pgvector_keeps_the_nearest_documents_not_the_farthest()
    {
        // The distance comes from the record's [VectorStoreVector] attribute: the options name none.
        var collection = WithPolicies(InMemoryCollection.Distance());
        await using var host = Host(collection, Reading());
        host.Embeddings!.OnAxis(Question, 0);
        host.Chat!.Say(CitingReply);

        await host.RunTurnAsync(Question);

        var chips = ChipsOf(host);
        chips.Select(c => c.Title).Should().Equal(["Refund policy", "Opened items"], "a distance of 0 is the nearest document");
        chips[0].Score.Should().BeApproximately(1f, 1e-4f, "1 − a distance of 0");
        chips[1].Score.Should().BeApproximately(0.7f, 1e-4f, "1 − a distance of 0.3");
        FirstPrompt(host).Should().NotContain("Our offices open at 9.", "a distance of 1 is orthogonal");
    }

    [Fact]
    public async Task A_negated_dot_product_is_read_as_a_similarity()
    {
        var collection = WithPolicies(InMemoryCollection.NegatedDotProduct());
        var source = new MentorVectorStoreRagSource<Doc>(collection, Reading(b =>
        {
            b.DistanceFunction = DistanceFunction.NegativeDotProductSimilarity;
            b.EmbeddingGenerator = new FakeEmbeddingGenerator().OnAxis(Question, 0);
        }));

        var results = await source.SearchAsync(new MentorRagRequest { Query = Question, MaxResults = 2 });

        results.Should().HaveCount(2);
        results[0].Score.Should().BeApproximately(1f, 1e-4f, "− a negated dot product of −1");
        results[1].Score.Should().BeApproximately(0.7f, 1e-4f);
    }

    // ── What the scores mean, decided at registration ────────────────────────────

    [Fact]
    public void A_collection_whose_distance_function_is_unknown_stops_the_application_at_registration()
    {
        var services = new ServiceCollection();

        var noDistance = () => services.AddMentorVectorStoreRag(
            _ => (IVectorSearchable<DocWithoutDistance>)null!, new MentorVectorStoreRagOptions<DocWithoutDistance> { Content = _ => "" });
        var noVector = () => services.AddMentorVectorStoreRag(
            _ => (IVectorSearchable<DocWithoutVector>)null!, new MentorVectorStoreRagOptions<DocWithoutVector> { Content = d => d.Text });
        var euclidean = () => services.AddMentorVectorStoreRag(
            _ => (IVectorSearchable<Doc>)null!, Reading(b => b.DistanceFunction = DistanceFunction.EuclideanDistance));

        noDistance.Should().Throw<InvalidOperationException>().WithMessage("*DistanceFunction*names none*each store has a default*");
        noVector.Should().Throw<InvalidOperationException>().WithMessage("*DocWithoutVector has no [VectorStoreVector] property*");
        euclidean.Should().Throw<InvalidOperationException>().WithMessage("*EuclideanDistance*no fixed scale*");
        services.Should().NotContain(d => d.ServiceType == typeof(IMentorRagSource));
    }

    [Fact]
    public void The_options_win_over_the_attribute_and_VectorProperty_picks_one_of_several_vectors()
    {
        MentorVectorStoreRagSource<Doc>.ScaleOf(Reading()).Should().Be(VectorScoreScale.Distance, "from the attribute");
        MentorVectorStoreRagSource<Doc>.ScaleOf(Reading(b => b.DistanceFunction = DistanceFunction.CosineSimilarity))
            .Should().Be(VectorScoreScale.Similarity, "the options name the collection's function");
        MentorVectorStoreRagSource<Doc>.ScaleOf(Reading(b => b.DistanceFunction = DistanceFunction.DotProductSimilarity))
            .Should().Be(VectorScoreScale.Similarity, "on normalised embeddings, the cosine");

        var several = () => MentorVectorStoreRagSource<DocWithTwoVectors>.ScaleOf(
            new MentorVectorStoreRagOptions<DocWithTwoVectors> { Content = _ => "" });
        several.Should().Throw<InvalidOperationException>().WithMessage("*several [VectorStoreVector] properties*VectorProperty*");
        MentorVectorStoreRagSource<DocWithTwoVectors>.ScaleOf(new MentorVectorStoreRagOptions<DocWithTwoVectors>
        {
            Content = _ => "",
            VectorProperty = d => d.BodyVector,
        }).Should().Be(VectorScoreScale.Distance);
    }

    // ── The message's vector ─────────────────────────────────────────────────────

    /// <summary>
    /// With <see cref="MentorOptions.EmbeddingGenerator"/> and no generator in the source's options, the message is
    /// embedded once per turn: the vector routing, tool filtering and memory already asked for.
    /// </summary>
    [Fact]
    public async Task The_turns_vector_is_shared_when_the_source_embeds_with_the_mentor_generator()
    {
        var collection = WithPolicies(InMemoryCollection.Similarity());
        await using var host = Host(collection, Reading(b => b.DistanceFunction = DistanceFunction.CosineSimilarity));
        host.Embeddings!.OnAxis(Question, 0);
        await host.Get<IMentorQueryEmbedding>().GetAsync(Question, host.Embeddings);   // routing or tool filtering did

        var results = await host.Get<IMentorRagSource>().SearchAsync(new MentorRagRequest { Query = Question, MaxResults = 3 });

        host.Embeddings.CallCount.Should().Be(1, "the message was embedded once this turn");
        collection.Inputs.Should().ContainSingle().Which.Should().BeOfType<ReadOnlyMemory<float>>();
        results[0].Title.Should().Be("Refund policy");
    }

    [Fact]
    public async Task A_generator_of_its_own_gets_its_models_vector_never_the_shared_one()
    {
        // The documents were embedded with another model, on which the question lies near "Office hours".
        var own = new FakeEmbeddingGenerator().OnAxis(Question, 3);
        var collection = WithPolicies(InMemoryCollection.Similarity());
        await using var host = Host(collection, Reading(b =>
        {
            b.DistanceFunction = DistanceFunction.CosineSimilarity;
            b.EmbeddingGenerator = own;
        }));
        host.Embeddings!.OnAxis(Question, 0);
        await host.Get<IMentorQueryEmbedding>().GetAsync(Question, host.Embeddings);

        var results = await host.Get<IMentorRagSource>().SearchAsync(new MentorRagRequest { Query = Question, MaxResults = 1 });

        own.CallCount.Should().Be(1);
        results.Should().ContainSingle().Which.Title.Should().Be("Office hours", "the vector of the source's own model");
    }

    [Fact]
    public async Task Without_any_generator_the_text_goes_to_the_collection_which_embeds_it()
    {
        var collection = WithPolicies(InMemoryCollection.Similarity(own: new FakeEmbeddingGenerator().OnAxis(Question, 0)));
        await using var host = Host(collection, Reading(b => b.DistanceFunction = DistanceFunction.CosineSimilarity), withEmbeddings: false);
        host.Chat!.Say(CitingReply);

        await host.RunTurnAsync(Question);

        collection.Inputs.Should().ContainSingle().Which.Should().Be(Question);
        ChipsOf(host).Select(c => c.Title).Should().StartWith("Refund policy");
    }

    // ── Who may see what ─────────────────────────────────────────────────────────

    [Fact]
    public async Task The_filter_is_given_the_request_and_keeps_the_tenants_own_documents()
    {
        var requests = new List<MentorRagRequest>();
        var collection = InMemoryCollection.Similarity();
        collection.Records.Add(new Doc { Id = 1, Text = "Contoso refunds take 14 days.", Title = "Contoso", Tenant = "contoso", Vector = Near(0) });
        collection.Records.Add(new Doc { Id = 2, Text = "Fabrikam refunds take 30 days.", Title = "Fabrikam", Tenant = "fabrikam", Vector = Near(0) });
        await using var host = Host(collection, Reading(b =>
        {
            b.DistanceFunction = DistanceFunction.CosineSimilarity;
            b.Filter = request =>
            {
                requests.Add(request);
                return d => d.Tenant == request.TenantId;
            };
        }), resolver: true);
        ((TestTenantResolver)host.Get<IMentorTenantResolver>()).Resolve = _ => "contoso";
        host.Embeddings!.OnAxis(Question, 0);
        host.Chat!.Say(CitingReply);

        await host.RunTurnAsync(Question);

        requests.Should().ContainSingle().Which.TenantId.Should().Be("contoso");
        requests[0].Query.Should().Be(Question);
        ChipsOf(host).Select(c => c.Title).Should().Equal("Contoso");
        host.Logs.HasWarning("has no Filter").Should().BeFalse();
    }

    [Fact]
    public async Task With_a_tenant_and_no_filter_the_source_warns_once_that_every_tenant_is_searched()
    {
        var collection = WithPolicies(InMemoryCollection.Similarity());
        await using var host = Host(collection, Reading(b => b.DistanceFunction = DistanceFunction.CosineSimilarity), resolver: true);
        ((TestTenantResolver)host.Get<IMentorTenantResolver>()).Resolve = _ => "contoso";
        host.Embeddings!.OnAxis(Question, 0);
        host.Chat!.SayRepeatedly("Ok.", 2);

        await host.RunTurnAsync(Question);
        await host.RunTurnAsync(Question);

        host.Logs.Records.Count(r => r.Level == LogLevel.Warning && r.Message.Contains("MentorVectorStoreRagSource<Doc> has no Filter"))
            .Should().Be(1);
    }

    // ── How many, and how near ───────────────────────────────────────────────────

    [Fact]
    public async Task RagResultCount_is_the_collections_top_and_the_hosts_RagMinScore_wins_over_the_sources()
    {
        var collection = WithPolicies(InMemoryCollection.Similarity());
        await using (var strict = Host(collection, Reading(b =>
        {
            b.DistanceFunction = DistanceFunction.CosineSimilarity;
            b.DefaultMinScore = 0.9f;
        }), o => o.RagResultCount = 2))
        {
            strict.Embeddings!.OnAxis(Question, 0);
            strict.Chat!.Say(CitingReply);
            await strict.RunTurnAsync(Question);

            collection.Tops.Should().Equal(2);
            strict.Get<IMentorRagSource>().DefaultMinScore.Should().Be(0.9f);
            ChipsOf(strict).Select(c => c.Title).Should().Equal(["Refund policy"], "0.7 is below the source's 0.9");
        }

        await using var host = Host(collection, Reading(b =>
        {
            b.DistanceFunction = DistanceFunction.CosineSimilarity;
            b.DefaultMinScore = 0.9f;
        }), o => o.RagMinScore = 0.6f);
        host.Embeddings!.OnAxis(Question, 0);
        host.Chat!.Say(CitingReply);
        await host.RunTurnAsync(Question);

        ChipsOf(host).Select(c => c.Title).Should().Equal(["Refund policy", "Opened items"], "the host's 0.6 wins");
    }

    [Fact]
    public async Task A_record_without_text_is_left_out_and_one_without_a_score_is_kept_with_a_warning()
    {
        var logs = new TestLogCollector();
        using var factory = LoggerFactory.Create(b => b.AddProvider(new TestLoggerProvider(logs)));
        var collection = WithPolicies(InMemoryCollection.Similarity());
        collection.Records.Insert(0, new Doc { Id = 9, Text = "  ", Title = "Empty", Vector = Near(0) });
        collection.WithoutScores = true;
        var source = new MentorVectorStoreRagSource<Doc>(collection, Reading(b =>
        {
            b.DistanceFunction = DistanceFunction.CosineSimilarity;
            b.EmbeddingGenerator = new FakeEmbeddingGenerator().OnAxis(Question, 0);
        }), factory.CreateLogger<MentorVectorStoreRagSource<Doc>>());

        var first = await source.SearchAsync(Question, 3);
        await source.SearchAsync(Question, 3);

        first.Select(r => r.Title).Should().NotContain("Empty");
        first.Should().OnlyContain(r => r.Score == 1f, "kept: a missing score cannot be compared");
        logs.Records.Count(r => r.Level == LogLevel.Warning && r.Message.Contains("without a score")).Should().Be(1);
    }

    [Fact]
    public async Task A_collection_that_fails_leaves_a_warning_and_the_turn_goes_on()
    {
        var collection = WithPolicies(InMemoryCollection.Similarity());
        collection.Throws = new InvalidOperationException("connection refused");
        await using var host = Host(collection, Reading(b => b.DistanceFunction = DistanceFunction.CosineSimilarity));
        host.Embeddings!.OnAxis(Question, 0);
        host.Chat!.Say("I could not look that up.");

        var error = await host.RunTurnAsync(Question);

        error.Should().BeNull();
        host.Chat.Calls.Should().HaveCount(1);
        host.Logs.HasWarning("The search of Novior.MentorAgent.Rag.MentorVectorStoreRagSource").Should().BeTrue(host.Logs.WarningText);
    }

    // ── Registration ─────────────────────────────────────────────────────────────

    [Fact]
    public void AddMentorVectorStoreRag_takes_the_place_of_the_null_source_before_or_after_AddMentorAgent_and_there_is_one()
    {
        var collection = WithPolicies(InMemoryCollection.Similarity());
        var options = Reading(b => b.DistanceFunction = DistanceFunction.CosineSimilarity);

        foreach (var before in new[] { true, false })
        {
            var services = new ServiceCollection();
            services.AddLogging();
            if (before) services.AddMentorVectorStoreRag(_ => collection, options);
            services.AddMentorAgent(o =>
            {
                o.AppName = "TestApp";
                o.ChatClient = new FakeChatClient();
                o.ScanAssemblies = [typeof(TestOrderService).Assembly];
            });
            if (!before) services.AddMentorVectorStoreRag(_ => collection, options);

            using var root = services.BuildServiceProvider();
            using var scope = root.CreateScope();
            scope.ServiceProvider.GetServices<IMentorRagSource>().Should().ContainSingle()
                .Which.Should().BeOfType<MentorVectorStoreRagSource<Doc>>(before ? "registered before" : "registered after");

            var twice = () => services.AddMentorVectorStoreRag(_ => collection, options);
            twice.Should().Throw<InvalidOperationException>().WithMessage("*already registered*there can be one*");
        }

        var custom = new ServiceCollection().AddScoped<IMentorRagSource, ScriptedRagSource>();
        var overACustomOne = () => custom.AddMentorVectorStoreRag(_ => collection, options);
        overACustomOne.Should().Throw<InvalidOperationException>().WithMessage("*ScriptedRagSource*");
    }
}
