using System.Reflection;
using FluentAssertions;
using Novior.MentorAgent.Middleware;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.AI;
using Xunit;

namespace Novior.MentorAgent.Tests.Routing;

/// <summary>
/// <c>RoutingComplexExemplars</c> — and specifically the set used when a host supplies none.
///
/// <para>
/// B4 covered semantic routing thoroughly, but every one of its cases passed its <b>own</b>
/// exemplars in. The path nobody exercised is the one almost every host actually takes: leave the
/// option null and let the built-in multilingual set decide. The claims ledger found the option name
/// nowhere in the test project, which is how a default that routes real money went unchecked.
/// </para>
///
/// <para>
/// The failure mode is asymmetric and that is what makes it worth pinning. An empty or missing
/// exemplar set does not throw — it makes the nearest-neighbour comparison match nothing, so
/// <b>every</b> turn stays on the cheap model. The assistant still answers, a little worse, and
/// nobody investigates a feature that appears to be working.
/// </para>
/// </summary>
public sealed class DefaultExemplarTests
{
    private const int ComplexAxis = 0;

    /// <summary>
    /// The built-in set, read off the class rather than retyped. Retyping it would test the copy.
    /// </summary>
    private static readonly string[] Defaults =
        (string[])typeof(RoutingChatClient)
            .GetField("DefaultComplexExemplars", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

    private static (RoutingChatClient Client, FakeChatClient Cheap, FakeChatClient Strong) Build(
        FakeEmbeddingGenerator embeddings, IReadOnlyList<string>? exemplars, float threshold = 0.35f)
    {
        var cheap = new FakeChatClient { ModelId = "cheap-model" };
        var strong = new FakeChatClient { ModelId = "strong-model" };
        cheap.Say("cheap");
        strong.Say("strong");

        var client = new RoutingChatClient(
            cheap, strong, MentorRoutingStrategy.Semantic,
            new TestLoggerProvider(new TestLogCollector()).CreateLogger(nameof(RoutingChatClient)),
            embeddings: embeddings,
            exemplars: exemplars,
            threshold: threshold);

        return (client, cheap, strong);
    }

    private static async Task<string> RouteAsync(RoutingChatClient client, string message)
    {
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, message)]);
        return response.Text;
    }

    // ── The set exists and is not empty ─────────────────────────────────────────

    [Fact]
    public void The_built_in_set_was_found_and_covers_the_documented_ground()
    {
        // Guards the reflection: a renamed field would otherwise make every test below pass against
        // an empty array, which is exactly the silent-downgrade state they exist to catch. The
        // option's documentation promises five kinds of turn — analysis, comparison, long-form,
        // coding, multi-step reasoning — so the set cannot be a token one or two entries.
        Defaults.Should().NotBeNull();
        Defaults.Should().HaveCountGreaterThanOrEqualTo(5,
            "the documented default names five categories of complex turn");
        Defaults.Should().OnlyContain(e => e.Length > 20, "each exemplar is a full sentence");
    }

    // ── Null and empty both fall back to it ─────────────────────────────────────

    [Fact]
    public async Task With_no_exemplars_configured_the_built_in_set_still_escalates()
    {
        // The default path, and the one the ledger found untested. A message that matches a built-in
        // exemplar must reach the strong model without the host configuring anything.
        var embeddings = new FakeEmbeddingGenerator();
        foreach (var exemplar in Defaults) embeddings.OnAxis(exemplar, ComplexAxis);
        embeddings.OnAxis("analyse this deeply", ComplexAxis);

        var (client, _, _) = Build(embeddings, exemplars: null);

        (await RouteAsync(client, "analyse this deeply")).Should().Be("strong");
    }

    [Fact]
    public async Task An_empty_list_is_treated_as_absent_rather_than_as_no_exemplars()
    {
        // `exemplars is { Count: > 0 }` — an empty list falls back to the built-in set. Taking it
        // literally would compare against nothing, match nothing, and silently pin every turn to the
        // cheap model. An empty list is what a configuration binder produces from an empty array.
        var embeddings = new FakeEmbeddingGenerator();
        foreach (var exemplar in Defaults) embeddings.OnAxis(exemplar, ComplexAxis);
        embeddings.OnAxis("analyse this deeply", ComplexAxis);

        var (client, _, _) = Build(embeddings, exemplars: []);

        (await RouteAsync(client, "analyse this deeply")).Should().Be("strong");
    }

    [Fact]
    public async Task An_ordinary_message_stays_cheap_under_the_built_in_set()
    {
        // The control that makes the two above mean something: if the default set escalated
        // everything, "it escalates" would be true and worthless — and the bill would be the
        // strong model's on every turn.
        var embeddings = new FakeEmbeddingGenerator();
        foreach (var exemplar in Defaults) embeddings.OnAxis(exemplar, ComplexAxis);
        embeddings.OnAxis("what time do you close", 1);

        var (client, _, _) = Build(embeddings, exemplars: null);

        (await RouteAsync(client, "what time do you close")).Should().Be("cheap");
    }

    // ── A host's own set replaces the default ───────────────────────────────────

    [Fact]
    public async Task A_custom_set_replaces_the_built_in_one_rather_than_adding_to_it()
    {
        // Documented as "example phrases describing the kind of turn that should go to the strong
        // model" — a host narrowing the definition must actually get a narrower one. If the two sets
        // were merged, a host that deliberately excluded coding would still escalate on it and pay
        // for a model they had decided not to use.
        var embeddings = new FakeEmbeddingGenerator();
        foreach (var exemplar in Defaults) embeddings.OnAxis(exemplar, ComplexAxis);

        // The custom set lives on a different axis, and the message matches the *default* set only.
        embeddings.OnAxis("only escalate for legal review", 2);
        embeddings.OnAxis("analyse this deeply", ComplexAxis);

        var (client, _, _) = Build(embeddings, exemplars: ["only escalate for legal review"]);

        (await RouteAsync(client, "analyse this deeply")).Should().Be("cheap",
            "the built-in exemplars are not consulted once the host supplies their own");
    }

    [Fact]
    public async Task A_custom_set_escalates_what_it_was_written_for()
    {
        // The other half: replacing the set has to leave a working set.
        var embeddings = new FakeEmbeddingGenerator()
            .OnAxis("only escalate for legal review", 2)
            .OnAxis("please review this contract clause", 2);

        var (client, _, _) = Build(embeddings, exemplars: ["only escalate for legal review"]);

        (await RouteAsync(client, "please review this contract clause")).Should().Be("strong");
    }

    // ── The option's own default ────────────────────────────────────────────────

    [Fact]
    public void The_option_defaults_to_null_which_is_what_selects_the_built_in_set()
    {
        new MentorOptions { AppName = "x", ScanAssemblies = [] }
            .RoutingComplexExemplars.Should().BeNull();
    }
}
