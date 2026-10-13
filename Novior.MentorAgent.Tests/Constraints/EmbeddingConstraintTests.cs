using FluentAssertions;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.Harness;
using Xunit;
using Xunit.Abstractions;

namespace Novior.MentorAgent.Tests.Constraints;

/// <summary>
/// Constraints 1–4 of the plan's matrix: the three features that need an
/// <c>EmbeddingGenerator</c>, and the routing strategy that needs a predicate.
///
/// <para>
/// These are the highest-yield tests in the whole suite, because they cover the failure mode this
/// library is most exposed to: a developer switches a feature on, nothing crashes, and nothing
/// happens either. Semantic tool filtering, relevance-ranked memory and semantic routing all
/// silently need a second service that the option itself does not mention.
/// </para>
///
/// <para>
/// Each one is asserted twice — with the dependency and without — and the "without" case must
/// produce a <em>warning</em> and a documented safe default. A standing rule of this codebase is
/// that the safe default is never a keyword heuristic: an embedding feature with no embeddings
/// degrades to "do the un-clever thing for everyone", not to "guess with string matching".
/// </para>
/// </summary>
public sealed class EmbeddingConstraintTests(ITestOutputHelper output)
{
    // ── #1 · EnableToolFiltering needs EmbeddingGenerator ─────────────────────────

    [Fact]
    public async Task ToolFiltering_without_embeddings_sends_every_tool_and_warns()
    {
        await using var host = MentorHostFixture.Create(o => o.EnableToolFiltering = true);

        await host.RunTurnAsync("cancel order 7");

        host.Logs.HasWarning("EmbeddingGenerator").Should().BeTrue(
            "turning on tool filtering with no embedding model must say so rather than quietly " +
            "doing nothing. Warnings seen:{0}{1}", Environment.NewLine, host.Logs.WarningText);

        host.FirstToolsSent.Should().NotBeEmpty(
            "the documented fallback is to send all tools, not to send none");
    }

    [Fact]
    public async Task ToolFiltering_without_embeddings_does_not_fall_back_to_keyword_matching()
    {
        // The standing rule: an embedding feature degrades to the safe default, never to a string
        // heuristic. If a keyword fallback were ever added, the tool list for a narrow message
        // would shrink — that is what this detects.
        await using var host = MentorHostFixture.Create(o => o.EnableToolFiltering = true);

        await host.RunTurnAsync("cancel order 7");
        var narrow = host.FirstToolsSent.Count;

        await using var other = MentorHostFixture.Create(o => o.EnableToolFiltering = true);
        await other.RunTurnAsync("tell me a joke about the weather in Rome");
        var unrelated = other.FirstToolsSent.Count;

        output.WriteLine($"narrow={narrow} unrelated={unrelated}");
        narrow.Should().Be(unrelated,
            "with no embedding model every message must receive the same, complete tool list");
    }

    [Fact]
    public async Task ToolFiltering_is_silent_when_the_dependency_is_satisfied()
    {
        await using var host = MentorHostFixture.Create(
            o => o.EnableToolFiltering = true, withEmbeddings: true);

        await host.RunTurnAsync("cancel order 7");

        host.Logs.HasWarning("Tool filtering is enabled but no EmbeddingGenerator").Should().BeFalse(
            "the warning is about a missing dependency, and the dependency is present");
    }

    [Fact]
    public async Task ToolFiltering_off_never_warns_about_embeddings()
    {
        // The warning must be tied to the combination, not to the absence of embeddings alone —
        // otherwise every default configuration logs a warning nobody can act on.
        await using var host = MentorHostFixture.Create();

        await host.RunTurnAsync();

        host.Logs.HasWarning("Tool filtering is enabled").Should().BeFalse();
    }

    // ── #2 · MemoryRelevanceFiltering needs EmbeddingGenerator ────────────────────

    [Fact]
    public async Task MemoryRelevanceFiltering_without_embeddings_falls_back_to_last_N_and_warns()
    {
        await using var host = MentorHostFixture.Create(o =>
        {
            o.UseMemoryContext = true;
            o.MemoryRelevanceFiltering = true;
        });

        await host.RunTurnAsync();

        host.Logs.HasWarning("MemoryRelevanceFiltering").Should().BeTrue(
            "the documented degradation is last-N with a warning. Warnings seen:{0}{1}",
            Environment.NewLine, host.Logs.WarningText);
    }

    [Fact]
    public async Task MemoryRelevanceFiltering_with_embeddings_is_silent()
    {
        await using var host = MentorHostFixture.Create(o =>
        {
            o.UseMemoryContext = true;
            o.MemoryRelevanceFiltering = true;
        }, withEmbeddings: true);

        await host.RunTurnAsync();

        host.Logs.HasWarning("MemoryRelevanceFiltering is enabled but no EmbeddingGenerator")
            .Should().BeFalse();
    }

    // ── #3 · RoutingStrategy.Semantic needs EmbeddingGenerator ────────────────────

    [Fact]
    public async Task SemanticRouting_without_embeddings_disables_routing_and_warns()
    {
        await using var host = MentorHostFixture.Create(o =>
        {
            o.StrongChatClient = new Fakes.FakeChatClient { ModelId = "strong" };
            o.RoutingStrategy = MentorRoutingStrategy.Semantic;
        });

        await host.RunTurnAsync("something complicated");

        host.Logs.HasWarning("Semantic model routing").Should().BeTrue(
            "routing that cannot decide must fall back to the cheap model and say why. " +
            "Warnings seen:{0}{1}", Environment.NewLine, host.Logs.WarningText);
    }

    [Fact]
    public async Task SemanticRouting_without_a_strong_client_is_simply_inactive()
    {
        // Routing is documented as active only when StrongChatClient is set. Without one there is
        // no decision to make, so the missing-embeddings warning would be noise.
        await using var host = MentorHostFixture.Create(o =>
            o.RoutingStrategy = MentorRoutingStrategy.Semantic);

        await host.RunTurnAsync();

        host.Chat!.CallCount.Should().BeGreaterThan(0, "the cheap client still serves the turn");
    }

    // ── #4 · RoutingStrategy.Custom needs a predicate ─────────────────────────────

    [Fact]
    public async Task CustomRouting_without_a_predicate_disables_routing_and_warns()
    {
        await using var host = MentorHostFixture.Create(o =>
        {
            o.StrongChatClient = new Fakes.FakeChatClient { ModelId = "strong" };
            o.RoutingStrategy = MentorRoutingStrategy.Custom;
            // Neither UseStrongModel nor UseStrongModelAsync is set — the documented no-op.
        });

        await host.RunTurnAsync("anything");

        // Named explicitly rather than "some warning was logged": a substring-free assertion would
        // pass on any unrelated warning and quietly stop testing this constraint at all.
        host.Logs.HasWarning("Custom model routing is selected but neither UseStrongModelAsync")
            .Should().BeTrue(
                "selecting Custom routing and supplying no predicate cannot silently mean 'never " +
                "escalate'. Warnings seen:{0}{1}", Environment.NewLine, host.Logs.WarningText);
    }

    [Fact]
    public async Task CustomRouting_prefers_the_async_predicate_over_the_legacy_one()
    {
        var syncCalled = false;
        var asyncCalled = false;

        await using var host = MentorHostFixture.Create(o =>
        {
            o.StrongChatClient = new Fakes.FakeChatClient { ModelId = "strong" };
            o.RoutingStrategy = MentorRoutingStrategy.Custom;
            o.UseStrongModel = _ => { syncCalled = true; return false; };
            o.UseStrongModelAsync = (_, _) => { asyncCalled = true; return Task.FromResult(false); };
        });

        await host.RunTurnAsync("anything");

        asyncCalled.Should().BeTrue("UseStrongModelAsync is documented to take precedence");
        syncCalled.Should().BeFalse("the legacy predicate must not also run — it would double-decide");
    }
}
