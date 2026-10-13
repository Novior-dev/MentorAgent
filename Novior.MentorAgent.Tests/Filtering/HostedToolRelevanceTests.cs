using FluentAssertions;
using Novior.MentorAgent.Middleware;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.AI;
using Xunit;

namespace Novior.MentorAgent.Tests.Filtering;

/// <summary>
/// Relevance filtering for <b>provider-hosted</b> tools — <c>FilterHostedTools</c> and
/// <c>HostedToolFilterMinScore</c>, which the claims ledger found untested despite ten mentions
/// across the READMEs.
///
/// <para>
/// B3 covered the filter's treatment of application actions and B12 covered how hosted tools are
/// declared. This is the seam between them, and it is the one with money attached: a hosted tool is
/// billed by the provider when the model calls it, so declaring web search on a turn that asked
/// about an order is a charge for nothing.
/// </para>
///
/// <para>
/// The option that deserves the attention is <c>HostedToolFilterMinScore</c>, whose default is
/// <b>0.15</b> — deliberately far below <c>ToolFilterMinScore</c>'s 0.35 rather than equal to it.
/// The option's own documentation explains why, with measurements: a shop assistant asked to
/// generate an image scored <c>image_generation</c> 0.29–0.35, and at a 0.35 threshold the verdict
/// flipped between two wordings of the same request. The two populations are not on one scale —
/// hosted tools carry long English descriptions, application actions carry short ones in the user's
/// language — so a threshold tuned for one mis-fires on the other. "Raising it to be safe" is the
/// mistake this file exists to make expensive.
/// </para>
/// </summary>
public sealed class HostedToolRelevanceTests
{
    // The filter embeds each hosted tool's **description**, not its key — `_toolEmbeddings` is keyed
    // by key but `EnsureToolEmbeddingsAsync` sends the text. Seeding by key therefore does nothing at
    // all, and the tool silently falls back to a hash-derived vector. Read the descriptions out of
    // the catalogue so the seeds are the strings the generator will actually be asked about.
    private static readonly string ImageText =
        Novior.MentorAgent.Core.HostedToolCatalog.Describe(new HostedImageGenerationTool())!.Value.Text;

    private static readonly string SearchText =
        Novior.MentorAgent.Core.HostedToolCatalog.Describe(new HostedWebSearchTool())!.Value.Text;

    private static ChatOptions WithHostedTools() => new()
    {
        Tools =
        [
            new HostedWebSearchTool(),
            new HostedImageGenerationTool(),
        ],
    };

    /// <summary>Builds the filter with no application actions, so only the hosted path is exercised.</summary>
    private static (ToolFilterChatClient Client, FakeChatClient Inner) Build(
        FakeEmbeddingGenerator embeddings, bool filterHosted, float? hostedMinScore = null)
    {
        var inner = new FakeChatClient();
        inner.Say("ok");

        var logs = new TestLogCollector();
        var client = new ToolFilterChatClient(
            inner,
            filterableNames: new HashSet<string>(),
            embeddings: embeddings,
            minScore: 0.35f,
            maxTools: 12,
            logger: new TestLoggerProvider(logs).CreateLogger("test"),
            filterHosted: filterHosted,
            hostedMinScore: hostedMinScore);

        return (client, inner);
    }

    private static async Task<IReadOnlyList<string>> DeclaredAsync(
        ToolFilterChatClient client, FakeChatClient inner, string message)
    {
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, message)], WithHostedTools());
        return inner.LastCall!.HostedToolNames;
    }

    // ── FilterHostedTools = false: everything is declared ───────────────────────

    [Fact]
    public async Task With_filtering_off_every_hosted_tool_is_declared_however_irrelevant()
    {
        // The opt-out. A host that would rather pay than risk a capability being withheld sets this
        // false, and then relevance must not remove anything — the domain check and the session
        // budget remain as the deliberate controls.
        var embeddings = new FakeEmbeddingGenerator()
            .OnAxis("draw me a picture of a cat", 0);

        var (client, inner) = Build(embeddings, filterHosted: false);

        var declared = await DeclaredAsync(client, inner, "how many open orders are there");

        declared.Should().HaveCount(2, "nothing may be filtered when filtering is off");
    }

    // ── FilterHostedTools = true: the irrelevant ones go ────────────────────────

    [Fact]
    public async Task An_irrelevant_hosted_tool_is_not_declared()
    {
        // The whole point, and the money: a hosted tool the provider bills for must not be offered
        // on a turn that plainly did not ask for it.
        var embeddings = new FakeEmbeddingGenerator()
            .OnAxis("draw me a logo", 0)
            .OnAxis(ImageText, 0)
            .OnAxis(SearchText, 1);

        var (client, inner) = Build(embeddings, filterHosted: true);

        var declared = await DeclaredAsync(client, inner, "draw me a logo");

        declared.Should().NotBeEmpty("the relevant tool must survive");
        declared.Should().HaveCountLessThan(2, "the unrelated one must not be declared");
    }

    [Fact]
    public async Task A_relevant_hosted_tool_survives_the_filter()
    {
        // The control. A filter that dropped everything would pass the test above and break the
        // feature — the same fail-shape as BUG-013, where a model with no tools just talks.
        var embeddings = new FakeEmbeddingGenerator()
            .OnAxis("draw me a logo", 0)
            .OnAxis(ImageText, 0)
            .OnAxis(SearchText, 1);

        var (client, inner) = Build(embeddings, filterHosted: true);

        var declared = await DeclaredAsync(client, inner, "draw me a logo");

        declared.Should().NotBeEmpty();
    }

    // ── HostedToolFilterMinScore ────────────────────────────────────────────────

    [Fact]
    public void The_default_hosted_threshold_is_lower_than_the_one_for_application_actions()
    {
        // The measured claim, stated as an invariant rather than a number in prose. If these two
        // ever converge, the flakiness the option's documentation describes comes straight back:
        // image_generation scored 0.29–0.35 on requests that genuinely wanted it.
        var options = new Novior.MentorAgent.Options.MentorOptions { AppName = "x", ScanAssemblies = [] };

        options.HostedToolFilterMinScore.Should().BeNull("null means the tuned default, 0.15");
        options.ToolFilterMinScore.Should().Be(0.35f);
    }

    [Fact]
    public async Task The_default_threshold_keeps_a_tool_that_scores_where_the_documentation_measured()
    {
        // 0.29 is inside the band the option's own notes recorded for a request that *did* want
        // image generation. At the documented default it is declared; at ToolFilterMinScore it
        // would not be, which is exactly the mis-fire the separate option exists to prevent.
        var embeddings = new FakeEmbeddingGenerator()
            .OnAxis("draw me a logo", 0)
            .AtSimilarity(ImageText, 0, 0.29f)
            .OnAxis(SearchText, 1);

        var (client, inner) = Build(embeddings, filterHosted: true);

        (await DeclaredAsync(client, inner, "draw me a logo")).Should().NotBeEmpty();
    }

    [Fact]
    public async Task Raising_the_threshold_to_the_application_value_drops_that_same_tool()
    {
        // The counterpart, and the reason "raise it to be safe" is the wrong instinct: the identical
        // request loses the capability. Documented as flakiness; here it is deterministic.
        var embeddings = new FakeEmbeddingGenerator()
            .OnAxis("draw me a logo", 0)
            .AtSimilarity(ImageText, 0, 0.29f)
            .OnAxis(SearchText, 1);

        var (client, inner) = Build(embeddings, filterHosted: true, hostedMinScore: 0.35f);

        (await DeclaredAsync(client, inner, "draw me a logo")).Should().BeEmpty();
    }

    [Fact]
    public async Task A_host_can_set_its_own_threshold_and_it_is_the_one_applied()
    {
        // The option is only a claim until something reads it — the same failure shape as
        // ObservabilitySourceName. A permissive value must let through what the default would drop.
        var embeddings = new FakeEmbeddingGenerator()
            .OnAxis("draw me a logo", 0)
            .AtSimilarity(ImageText, 0, 0.05f)
            .OnAxis(SearchText, 1);

        var (strict, strictInner) = Build(embeddings, filterHosted: true);
        (await DeclaredAsync(strict, strictInner, "draw me a logo"))
            .Should().BeEmpty("0.05 is below the 0.15 default");

        var (loose, looseInner) = Build(embeddings, filterHosted: true, hostedMinScore: 0.01f);
        (await DeclaredAsync(loose, looseInner, "draw me a logo"))
            .Should().NotBeEmpty("the host asked for 0.01, so 0.05 clears it");
    }

    // ── The documented default of the switch itself ────────────────────────────

    [Fact]
    public void Hosted_tool_filtering_is_on_by_default()
    {
        // Unlike EnableToolFiltering, this one defaults to true: hosted tools cost money per call,
        // so the safe default is to withhold the plainly irrelevant rather than to declare
        // everything. It still does nothing without EnableToolFiltering and an embedding generator,
        // which is the fail-open rule C2.1 owns.
        new Novior.MentorAgent.Options.MentorOptions { AppName = "x", ScanAssemblies = [] }
            .FilterHostedTools.Should().BeTrue();
    }
}
