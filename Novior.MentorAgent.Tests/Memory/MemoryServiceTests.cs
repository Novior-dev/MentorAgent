using System.Security.Claims;
using System.Text.RegularExpressions;
using FluentAssertions;
using Novior.MentorAgent.Memory;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Novior.MentorAgent.Tests.Memory;

/// <summary>
/// <see cref="MentorMemoryService"/> — who a memory belongs to, and which memories reach the prompt.
///
/// <para>
/// Two separate responsibilities meet in this class and both are security-shaped. The first is
/// identity: every read and write is scoped by a user id this service resolves on its own from the
/// authentication state, so a mistake here mixes one user's remembered facts into another's prompt.
/// The second is selection: with <c>MemoryRelevanceFiltering</c> on, the service decides which
/// subset of a user's memories is worth its tokens — and that decision must be
/// <b>embedding-only</b>. A keyword heuristic would be cheaper, would look like it worked in
/// English, and would quietly stop working in every other language the widget supports.
/// </para>
///
/// <para>
/// The service is built directly rather than resolved from the fixture: these are decisions it makes
/// before any model is involved, and a turn would only add noise between the cause and the assertion.
/// </para>
/// </summary>
public sealed class MemoryServiceTests
{
    /// <summary>Service, store, options and log, wired the way the container wires them.</summary>
    private sealed class Rig : IDisposable
    {
        internal Rig(Action<MentorOptions>? configure = null,
                     AuthenticationStateProvider? auth = null,
                     bool withEmbeddings = false)
        {
            Options = NewOptions();
            Options.UseMemoryContext = true;
            if (withEmbeddings) Options.EmbeddingGenerator = Embeddings = new FakeEmbeddingGenerator();
            configure?.Invoke(Options);

            Auth = auth ?? new TestAuthStateProvider();

            var provider = new TestLoggerProvider(Logs);
            Service = new MentorMemoryService(
                Store,
                new OptionsWrapper<MentorOptions>(Options),
                new TypedLogger<MentorMemoryService>(provider.CreateLogger("Memory")),
                Auth);
        }

        internal IMentorMemoryStore Store { get; } = new InMemoryMentorMemoryStore();
        internal MentorOptions Options { get; }
        internal FakeEmbeddingGenerator? Embeddings { get; }
        internal AuthenticationStateProvider Auth { get; }
        internal TestLogCollector Logs { get; } = new();
        internal MentorMemoryService Service { get; }

        /// <summary>Signs in, when the rig was built with the default controllable provider.</summary>
        internal Rig SignIn(string userId, params string[] roles)
        {
            ((TestAuthStateProvider)Auth).SignIn(userId, roles);
            return this;
        }

        /// <summary>Writes straight to the store, with a distinguishable timestamp per entry.</summary>
        internal async Task SeedAsync(string userId, params (string Key, string Value)[] entries)
        {
            foreach (var (key, value) in entries)
            {
                await Store.SaveAsync(userId, key, value);
                await Task.Delay(15);
            }
        }

        public void Dispose() => Service.Dispose();
    }

    /// <summary>Options with only the two required members set — nothing here needs discovery.</summary>
    private static MentorOptions NewOptions() => new()
    {
        AppName = "TestApp",
        ScanAssemblies = [typeof(MemoryServiceTests).Assembly],
    };

    /// <summary>An <see cref="AuthenticationStateProvider"/> that fails, as a broken host's would.</summary>
    private sealed class ThrowingAuthStateProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
            => throw new InvalidOperationException("no authentication state here");
    }

    /// <summary>Signed in without a NameIdentifier claim — only <c>Identity.Name</c> is available.</summary>
    private sealed class NameOnlyAuthStateProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
            => Task.FromResult(new AuthenticationState(new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.Name, "named-only")], authenticationType: "Test"))));
    }

    // ── Identity ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_unauthenticated_visitor_is_anonymous()
    {
        using var rig = new Rig();

        (await rig.Service.GetUserIdAsync()).Should().Be("anonymous");
    }

    [Fact]
    public async Task A_host_with_no_authentication_at_all_is_anonymous()
    {
        // AuthenticationStateProvider is an optional constructor dependency — a non-Blazor host
        // (the headless SSE sample) has none.
        using var service = new MentorMemoryService(
            new InMemoryMentorMemoryStore(),
            new OptionsWrapper<MentorOptions>(NewOptions()),
            new TypedLogger<MentorMemoryService>(new TestLoggerProvider(new TestLogCollector()).CreateLogger("m")),
            auth: null);

        (await service.GetUserIdAsync()).Should().Be("anonymous");
    }

    [Fact]
    public async Task A_signed_in_user_is_identified_by_the_name_identifier_claim()
    {
        using var rig = new Rig();
        rig.SignIn("u-42");

        (await rig.Service.GetUserIdAsync()).Should().Be("u-42");
    }

    [Fact]
    public async Task Without_a_name_identifier_the_identity_name_is_used()
    {
        // Named in the key since rc.15 (BUG-142): "name:…", so a name never matches another user's
        // NameIdentifier. rc.14 used the name as it was.
        using var rig = new Rig(auth: new NameOnlyAuthStateProvider());

        (await rig.Service.GetUserIdAsync()).Should().Be("name:named-only");
    }

    [Fact]
    public async Task An_authentication_provider_that_throws_degrades_to_anonymous()
    {
        // Failing closed here would mean throwing out of every turn that has memory enabled. The
        // documented behaviour is to treat the visitor as anonymous, which is also the safe bucket.
        using var rig = new Rig(auth: new ThrowingAuthStateProvider());

        (await rig.Service.GetUserIdAsync()).Should().Be("anonymous");
    }

    [Fact]
    public async Task Signing_out_moves_the_user_back_to_the_anonymous_bucket()
    {
        using var rig = new Rig();
        rig.SignIn("u-42");
        await rig.Service.RememberAsync("user_name", "Antonio");

        ((TestAuthStateProvider)rig.Auth).SignOut();

        (await rig.Service.RecallAsync("user_name")).Should().BeNull(
            "an anonymous visitor must not inherit the previous user's memories");
        (await rig.Store.GetAsync("u-42", "user_name")).Should().Be("Antonio",
            "and the signed-in user's memories must survive the sign-out");
    }

    // ── Write / read ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Remember_writes_under_the_resolved_user()
    {
        using var rig = new Rig();
        rig.SignIn("u-42");

        await rig.Service.RememberAsync("user_name", "Antonio");

        (await rig.Store.GetAsync("u-42", "user_name")).Should().Be("Antonio");
        (await rig.Store.GetAsync("anonymous", "user_name")).Should().BeNull();
    }

    [Fact]
    public async Task Recall_reads_back_what_remember_wrote()
    {
        using var rig = new Rig();
        rig.SignIn("u-42");

        await rig.Service.RememberAsync("preferred_format", "PDF");

        (await rig.Service.RecallAsync("preferred_format")).Should().Be("PDF");
        (await rig.Service.RecallAsync("never_saved")).Should().BeNull();
    }

    [Fact]
    public async Task Remember_object_stores_json()
    {
        using var rig = new Rig();

        await rig.Service.RememberObjectAsync("last_filter", new { status = "open", limit = 25 });

        (await rig.Service.RecallAsync("last_filter"))
            .Should().Be("""{"status":"open","limit":25}""");
    }

    [Fact]
    public async Task Forget_all_clears_only_the_current_user()
    {
        // The forget_all tool is the user's own delete button, and it must not be a global one.
        using var rig = new Rig();
        await rig.SeedAsync("someone-else", ("user_name", "Beatrice"));
        rig.SignIn("u-42");
        await rig.Service.RememberAsync("user_name", "Antonio");
        await rig.Service.RememberAsync("user_role", "dev");

        await rig.Service.ForgetAllAsync();

        (await rig.Service.RecallAsync("user_name")).Should().BeNull();
        (await rig.Service.RecallAsync("user_role")).Should().BeNull();
        (await rig.Store.GetAsync("someone-else", "user_name")).Should().Be("Beatrice");
    }

    [Fact]
    public async Task Two_users_never_see_each_others_memory_block()
    {
        using var rig = new Rig();
        await rig.SeedAsync("u-1", ("user_name", "Antonio"));
        await rig.SeedAsync("u-2", ("user_name", "Beatrice"));

        rig.SignIn("u-1");
        var first = await rig.Service.GetMemoryContextAsync();
        rig.SignIn("u-2");
        var second = await rig.Service.GetMemoryContextAsync();

        first.Should().Contain("Antonio").And.NotContain("Beatrice");
        second.Should().Contain("Beatrice").And.NotContain("Antonio");
    }

    // ── The injected block ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_user_with_no_memories_contributes_nothing_to_the_prompt()
    {
        // Empty string, not an empty header: AppContextProvider only appends when the result is
        // non-blank, so a bare "## USER CONTEXTUAL MEMORY" would cost tokens on every first turn.
        using var rig = new Rig();

        (await rig.Service.GetMemoryContextAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task The_block_carries_the_documented_header()
    {
        using var rig = new Rig();
        await rig.SeedAsync("anonymous", ("user_name", "Antonio"));

        var block = await rig.Service.GetMemoryContextAsync();

        block.Should().Contain("## USER CONTEXTUAL MEMORY");
        block.Should().Contain("Memories from previous sessions with this user:");
    }

    [Fact]
    public async Task Each_memory_is_one_dated_line_of_key_and_value()
    {
        using var rig = new Rig();
        await rig.SeedAsync("anonymous", ("user_name", "Antonio"));

        var block = await rig.Service.GetMemoryContextAsync();

        // The date separator follows the current culture, so the shape is asserted rather than a
        // literal — what matters is that the model receives timestamp, key and value.
        block.Should().MatchRegex(@"- \[\d{2}.\d{2}.\d{4} \d{2}:\d{2}\] user_name: Antonio");
    }

    [Fact]
    public async Task The_block_honours_the_count()
    {
        using var rig = new Rig();
        await rig.SeedAsync("anonymous",
            ("k1", "one"), ("k2", "two"), ("k3", "three"), ("k4", "four"));

        var block = await rig.Service.GetMemoryContextAsync(count: 2);

        Regex.Matches(block, @"^- \[", RegexOptions.Multiline).Should().HaveCount(2);
        block.Should().Contain("k4").And.Contain("k3");
        block.Should().NotContain("k1").And.NotContain("k2");
    }

    [Fact]
    public async Task Without_relevance_filtering_the_most_recent_memories_win()
    {
        using var rig = new Rig(o => o.MemoryRelevanceFiltering = false);
        await rig.SeedAsync("anonymous", ("old", "1"), ("newer", "2"), ("newest", "3"));
        rig.Service.SetCurrentQuery("anything at all");

        var block = await rig.Service.GetMemoryContextAsync(count: 2);

        block.Should().Contain("newest").And.Contain("newer").And.NotContain("old:");
    }

    // ── Relevance filtering — embedding-only ────────────────────────────────────

    [Fact]
    public async Task Relevance_filtering_keeps_the_semantically_closest_memory()
    {
        using var rig = new Rig(o => o.MemoryRelevanceFiltering = true, withEmbeddings: true);
        rig.Embeddings!
           .OnAxis("what is the shipping status", 0)
           .OnAxis("note_shipping: courier is DHL", 0)
           .OnAxis("note_lunch: the canteen closes at 3", 1);

        await rig.SeedAsync("anonymous",
            ("note_shipping", "courier is DHL"),
            ("note_lunch", "the canteen closes at 3"));
        rig.Service.SetCurrentQuery("what is the shipping status");

        var block = await rig.Service.GetMemoryContextAsync(count: 1);

        block.Should().Contain("note_shipping").And.NotContain("note_lunch");
    }

    [Fact]
    public async Task Relevance_ranking_is_semantic_and_not_a_word_match()
    {
        // The whole reason this feature requires an embedding generator. A keyword heuristic would
        // pick the memory that repeats the question's words; the embedding picks the one that
        // means the same thing. Here the two are deliberately different memories, so only one of
        // the two possible implementations can pass.
        using var rig = new Rig(o => o.MemoryRelevanceFiltering = true, withEmbeddings: true);
        const string query = "come stanno andando le vendite del trimestre";

        rig.Embeddings!
           .OnAxis(query, 0)
           .OnAxis("nota_fatturato: chiuso a 1,2M", 0)                       // means it, different words
           .OnAxis("nota_varie: le vendite del trimestre non c'entrano", 1); // same words, unrelated

        await rig.SeedAsync("anonymous",
            ("nota_fatturato", "chiuso a 1,2M"),
            ("nota_varie", "le vendite del trimestre non c'entrano"));
        rig.Service.SetCurrentQuery(query);

        var block = await rig.Service.GetMemoryContextAsync(count: 1);

        block.Should().Contain("nota_fatturato",
            "the memory that means the same thing must win");
        block.Should().NotContain("nota_varie",
            "repeating the question's words must count for nothing — a keyword fallback is forbidden");
    }

    [Fact]
    public async Task Relevance_filtering_looks_beyond_the_last_N_memories()
    {
        // The pool is four times the count, so a fact from far back in the user's history can still
        // be injected when this turn is about it. Recency alone would never surface it.
        using var rig = new Rig(o => o.MemoryRelevanceFiltering = true, withEmbeddings: true);
        rig.Embeddings!.OnAxis("about the warehouse", 0).OnAxis("note_warehouse: bay 4 is closed", 0);
        for (var i = 1; i <= 5; i++) rig.Embeddings.OnAxis($"note_{i}: filler {i}", 1);

        await rig.SeedAsync("anonymous", ("note_warehouse", "bay 4 is closed"));
        await rig.SeedAsync("anonymous", Enumerable.Range(1, 5)
            .Select(i => ($"note_{i}", $"filler {i}")).ToArray());
        rig.Service.SetCurrentQuery("about the warehouse");

        var block = await rig.Service.GetMemoryContextAsync(count: 2);

        block.Should().Contain("note_warehouse",
            "it is the sixth-newest of six memories and would fall outside a recency window of two");
    }

    [Fact]
    public async Task Identity_and_preference_facts_are_sticky_however_irrelevant_they_look()
    {
        // Nothing a user asks is ever "about" their own name, so under pure similarity ranking the
        // assistant would forget who it is talking to on the first specific question.
        using var rig = new Rig(o => o.MemoryRelevanceFiltering = true, withEmbeddings: true);
        rig.Embeddings!
           .OnAxis("what is the shipping status", 0)
           .OnAxis("user_name: Antonio", 4)
           .OnAxis("preferred_format: PDF", 5)
           .OnAxis("note_shipping: courier is DHL", 0);

        await rig.SeedAsync("anonymous",
            ("user_name", "Antonio"),
            ("preferred_format", "PDF"),
            ("note_shipping", "courier is DHL"));
        rig.Service.SetCurrentQuery("what is the shipping status");

        var block = await rig.Service.GetMemoryContextAsync(count: 3);

        block.Should().Contain("user_name").And.Contain("preferred_format").And.Contain("note_shipping");
    }

    [Fact]
    public async Task Sticky_facts_alone_are_still_capped_by_the_count()
    {
        // A user whose memories are all "user_*" must not be able to blow the token budget.
        using var rig = new Rig(o => o.MemoryRelevanceFiltering = true, withEmbeddings: true);
        rig.Embeddings!.OnAxis("anything", 0);

        await rig.SeedAsync("anonymous", Enumerable.Range(1, 6)
            .Select(i => ($"user_fact_{i}", $"value {i}")).ToArray());
        rig.Service.SetCurrentQuery("anything");

        var block = await rig.Service.GetMemoryContextAsync(count: 2);

        Regex.Matches(block, @"^- \[", RegexOptions.Multiline).Should().HaveCount(2);
    }

    [Fact]
    public async Task Injected_memories_are_listed_oldest_first()
    {
        // Chronological order reads as a history rather than a ranked list, which is what the model
        // is being asked to reuse. The ranking decides the set, not the presentation.
        using var rig = new Rig(o => o.MemoryRelevanceFiltering = true, withEmbeddings: true);
        rig.Embeddings!.OnAxis("q", 0).OnAxis("a: 1", 0).OnAxis("b: 2", 0).OnAxis("c: 3", 0);

        await rig.SeedAsync("anonymous", ("a", "1"), ("b", "2"), ("c", "3"));
        rig.Service.SetCurrentQuery("q");

        var block = await rig.Service.GetMemoryContextAsync(count: 3);

        block.IndexOf("a: 1", StringComparison.Ordinal)
             .Should().BeLessThan(block.IndexOf("c: 3", StringComparison.Ordinal));
    }

    // ── Degradation ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Relevance_filtering_without_an_embedding_generator_falls_back_to_recency()
    {
        // Constraint #2. The point worth pinning here is not the warning (that is covered where the
        // constraints live) but that the fallback is *recency* — the safe, language-neutral default
        // — and never an improvised text match.
        using var rig = new Rig(o => o.MemoryRelevanceFiltering = true, withEmbeddings: false);
        await rig.SeedAsync("anonymous",
            ("note_shipping", "the shipping courier is DHL"),
            ("note_recent", "something else entirely"));
        rig.Service.SetCurrentQuery("what is the shipping status");

        var block = await rig.Service.GetMemoryContextAsync(count: 1);

        block.Should().Contain("note_recent",
            "with no generator the newest memory is injected, whatever the words say");
        block.Should().NotContain("note_shipping",
            "matching 'shipping' against the query would be exactly the keyword fallback that is forbidden");
    }

    [Fact]
    public async Task Relevance_filtering_without_a_current_query_falls_back_to_recency()
    {
        // The very first context build of a connection happens before any message exists.
        using var rig = new Rig(o => o.MemoryRelevanceFiltering = true, withEmbeddings: true);
        await rig.SeedAsync("anonymous", ("older", "1"), ("newest", "2"));

        var block = await rig.Service.GetMemoryContextAsync(count: 1);

        block.Should().Contain("newest").And.NotContain("older");
        rig.Embeddings!.CallCount.Should().Be(0, "there is nothing to rank against");
    }

    [Fact]
    public async Task A_blank_query_is_treated_as_no_query()
    {
        using var rig = new Rig(o => o.MemoryRelevanceFiltering = true, withEmbeddings: true);
        await rig.SeedAsync("anonymous", ("older", "1"), ("newest", "2"));
        rig.Service.SetCurrentQuery("   ");

        var block = await rig.Service.GetMemoryContextAsync(count: 1);

        block.Should().Contain("newest");
        rig.Embeddings!.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task An_embedding_failure_falls_back_to_recency_and_warns()
    {
        // Embedding providers rate-limit. Losing the memory block over it would be a worse outcome
        // than spending a few extra tokens on the last N.
        using var rig = new Rig(o => o.MemoryRelevanceFiltering = true, withEmbeddings: true);
        rig.Embeddings!.ThrowOnNextCall = new HttpRequestException("429 Too Many Requests");

        await rig.SeedAsync("anonymous", ("older", "1"), ("newest", "2"));
        rig.Service.SetCurrentQuery("anything");

        var block = await rig.Service.GetMemoryContextAsync(count: 1);

        block.Should().Contain("newest");
        rig.Logs.HasWarning("Memory relevance embedding failed").Should().BeTrue(rig.Logs.WarningText);
    }

    // ── Embedding cache ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_memory_is_embedded_once_per_connection_not_once_per_turn()
    {
        // The scoped service lives as long as the connection, so re-embedding every memory on every
        // message would turn a token optimisation into a per-turn network round trip.
        using var rig = new Rig(o => o.MemoryRelevanceFiltering = true, withEmbeddings: true);
        rig.Embeddings!.OnAxis("q", 0).OnAxis("a: 1", 0).OnAxis("b: 2", 1);
        await rig.SeedAsync("anonymous", ("a", "1"), ("b", "2"));
        rig.Service.SetCurrentQuery("q");

        await rig.Service.GetMemoryContextAsync(count: 1);
        var afterFirst = rig.Embeddings.InputCount;
        await rig.Service.GetMemoryContextAsync(count: 1);

        afterFirst.Should().Be(3, "two memories plus the query");
        rig.Embeddings.InputCount.Should().Be(4, "only the query is embedded again");
    }

    [Fact]
    public async Task A_memory_whose_value_changed_is_embedded_again()
    {
        // The cache is keyed by key *and* value, so an updated fact is never ranked by its old
        // meaning — which is the failure a key-only cache would produce.
        using var rig = new Rig(o => o.MemoryRelevanceFiltering = true, withEmbeddings: true);
        rig.Embeddings!.OnAxis("q", 0).OnAxis("a: 1", 0).OnAxis("a: 2", 0);
        await rig.SeedAsync("anonymous", ("a", "1"));
        rig.Service.SetCurrentQuery("q");

        await rig.Service.GetMemoryContextAsync(count: 1);
        await rig.Service.RememberAsync("a", "2");
        await rig.Service.GetMemoryContextAsync(count: 1);

        rig.Embeddings.InputCount.Should().Be(4, "memory, query, changed memory, query");
    }

    [Fact]
    public async Task Concurrent_context_builds_embed_each_memory_once()
    {
        // Two hub invocations can share a scope, and the double-checked lock inside is the only
        // thing stopping a burst from embedding the same memory several times over.
        using var rig = new Rig(o => o.MemoryRelevanceFiltering = true, withEmbeddings: true);
        rig.Embeddings!.OnAxis("q", 0);
        for (var i = 0; i < 5; i++) rig.Embeddings.OnAxis($"k{i}: v{i}", i % 2);
        await rig.SeedAsync("anonymous", Enumerable.Range(0, 5).Select(i => ($"k{i}", $"v{i}")).ToArray());
        rig.Service.SetCurrentQuery("q");

        await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => rig.Service.GetMemoryContextAsync(count: 3)));

        // 5 memories once, plus one query embedding per call.
        rig.Embeddings.InputCount.Should().Be(5 + 8);
    }
}
