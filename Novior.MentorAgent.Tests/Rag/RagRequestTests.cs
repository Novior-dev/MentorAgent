using FluentAssertions;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Rag;
using Novior.MentorAgent.Tests.App;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Novior.MentorAgent.Tests.Rag;

/// <summary>
/// The search request a source receives, the metadata it can return, and whose relevance threshold
/// applies.
///
/// <para>
/// Up to rc.14 <c>SearchAsync</c> received the message text and a count. A source could not tell who was
/// asking — so it could not filter by permission — nor from which page or after what, and its results
/// had nowhere to put a date or a version the model should weigh. And <c>RagMinScore</c> had one default,
/// 2, for keyword scores: on a vector store, whose cosine scores never exceed 1, nothing it returned could
/// clear it. Now the source receives a <see cref="MentorRagRequest"/>, results carry
/// <see cref="MentorRagResult.Metadata"/>, and a source can declare
/// <see cref="IMentorRagSource.DefaultMinScore"/>, which applies unless the host sets the option.
/// </para>
/// </summary>
public sealed class RagRequestTests
{
    /// <summary>A source written for the request overload: it records every request it is given.</summary>
    private sealed class RequestAwareSource : IMentorRagSource
    {
        internal List<MentorRagRequest> Requests { get; } = [];
        internal List<MentorRagResult> Results { get; } = [];
        public float? DefaultMinScore { get; init; }

        public Task<IReadOnlyList<MentorRagResult>> SearchAsync(MentorRagRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult<IReadOnlyList<MentorRagResult>>(Results.Take(request.MaxResults).ToList());
        }

        public Task<IReadOnlyList<MentorRagResult>> SearchAsync(string query, int maxResults, CancellationToken ct = default)
            => throw new InvalidOperationException("MentorAgent calls the request overload.");
    }

    private static MentorHostFixture WithSource(IMentorRagSource source, Action<MentorOptions>? configure = null)
        => MentorHostFixture.Create(
            o => { o.UseRag = true; configure?.Invoke(o); },
            services => services.AddSingleton(source));

    private static string FirstPrompt(MentorHostFixture host)
        => (host.Chat!.Calls[0].Instructions ?? "") + "\n" + host.Chat.Calls[0].SystemText;

    private static string Warnings(MentorHostFixture host)
        => string.Join("\n", host.Logs.Records.Where(r => r.Level >= LogLevel.Warning).Select(r => r.Message));

    // ── The request ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_source_written_for_the_text_overload_is_searched_through_the_request()
    {
        // ScriptedRagSource implements only SearchAsync(string, int): every source written before rc.15
        // looks like it. It compiles unchanged, and the default request overload hands it the same two
        // values MentorAgent used to pass.
        var source = new ScriptedRagSource().Returns("Shipping takes 3 days.", 9f, "Shipping");

        var results = await ((IMentorRagSource)source).SearchAsync(new MentorRagRequest { Query = "shipping?", MaxResults = 2 });

        source.Searches.Should().ContainSingle().Which.Should().Be(("shipping?", 2));
        results.Should().ContainSingle();
    }

    [Fact]
    public async Task The_request_carries_the_message_the_count_the_user_and_the_page()
    {
        var source = new RequestAwareSource();
        await using var host = WithSource(source, o => o.RagResultCount = 4);
        host.Auth.SignIn("ada", "Finance");
        host.Get<Novior.MentorAgent.Abstractions.Interfaces.IMentorPageContext>()
            .SetPageName("Orders")
            .Set("SelectedOrder", 1004);
        host.Chat!.Say("ok");

        await host.RunTurnAsync("what is the refund policy for this order?");

        var request = source.Requests.Should().ContainSingle().Subject;
        request.Query.Should().Be("what is the refund policy for this order?");
        request.MaxResults.Should().Be(4);
        request.User!.Identity!.Name.Should().Be("ada");
        request.User.IsInRole("Finance").Should().BeTrue();
        request.PageName.Should().Be("Orders");
        request.PageData.Should().ContainKey("SelectedOrder").WhoseValue.Should().Be(1004);
        request.RecentMessages.Should().BeEmpty("nothing was said before the first message");
    }

    [Fact]
    public async Task An_anonymous_visitor_is_an_unauthenticated_principal()
    {
        var source = new RequestAwareSource();
        await using var host = WithSource(source);
        host.Chat!.Say("ok");

        await host.RunTurnAsync("hello");

        source.Requests.Single().User?.Identity?.IsAuthenticated.Should().NotBe(true);
    }

    [Fact]
    public async Task The_second_search_is_told_what_was_said_before_and_a_reset_forgets_it()
    {
        var source = new RequestAwareSource();
        await using var host = WithSource(source);

        host.Chat!.Say("Shipping costs 5 euro in Italy.");
        await host.RunTurnAsync("how much is shipping?");
        host.Chat.Say("Abroad it is 15 euro.");
        await host.RunTurnAsync("and abroad?");

        source.Requests[1].RecentMessages.Select(m => (m.Role, m.Text)).Should().Equal(
            (ChatRole.User, "how much is shipping?"),
            (ChatRole.Assistant, "Shipping costs 5 euro in Italy."));

        await host.Orchestrator.ResetSessionAsync();
        host.Chat.Say("ok");
        await host.RunTurnAsync("hello again");

        source.Requests[2].RecentMessages.Should().BeEmpty("a new conversation has nothing before it");
    }

    [Fact]
    public async Task Only_the_last_three_turns_are_kept()
    {
        var source = new RequestAwareSource();
        await using var host = WithSource(source);

        for (var i = 1; i <= 5; i++)
        {
            host.Chat!.Say($"reply {i}");
            await host.RunTurnAsync($"message {i}");
        }

        source.Requests[4].RecentMessages.Should().HaveCount(6);
        source.Requests[4].RecentMessages[0].Text.Should().Be("message 2");
        source.Requests[4].RecentMessages[^1].Text.Should().Be("reply 4");
    }

    // ── Metadata ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Metadata_reaches_the_model_with_its_document()
    {
        var source = new RequestAwareSource();
        source.Results.Add(new MentorRagResult("Refunds within 14 days.", null, "Refund policy", 9f)
        {
            Metadata = new Dictionary<string, string> { ["valid from"] = "2026-01-01", ["version"] = "3" },
        });
        await using var host = WithSource(source);
        host.Chat!.Say("ok");

        await host.RunTurnAsync("refunds?");

        FirstPrompt(host).Should().Contain("[1] Refund policy\n(valid from: 2026-01-01; version: 3)\nRefunds within 14 days.");
    }

    [Fact]
    public async Task A_line_break_in_a_metadata_value_does_not_start_the_document_early()
    {
        var source = new RequestAwareSource();
        source.Results.Add(new MentorRagResult("Body.", null, "Doc", 9f)
        {
            Metadata = new Dictionary<string, string> { ["note"] = "first\nsecond" },
        });
        await using var host = WithSource(source);
        host.Chat!.Say("ok");

        await host.RunTurnAsync("doc?");

        FirstPrompt(host).Should().Contain("(note: first second)\nBody.");
    }

    [Fact]
    public async Task A_result_without_metadata_is_shown_as_before()
    {
        var source = new RequestAwareSource();
        source.Results.Add(new MentorRagResult("Body.", null, "Doc", 9f));
        await using var host = WithSource(source);
        host.Chat!.Say("ok");

        await host.RunTurnAsync("doc?");

        FirstPrompt(host).Should().Contain("[1] Doc\nBody.");
    }

    // ── Whose threshold ──────────────────────────────────────────────────────────

    [Fact]
    public async Task With_default_options_a_vector_source_that_declares_its_threshold_reaches_the_model()
    {
        // The case the option's default could never serve: cosine scores below 1 against a default of 2.
        var source = new RequestAwareSource { DefaultMinScore = 0.6f };
        source.Results.Add(new MentorRagResult("Refunds within 14 days.", null, "Refund policy", 0.82f));
        source.Results.Add(new MentorRagResult("Unrelated.", null, "Noise", 0.41f));
        await using var host = WithSource(source);
        host.Chat!.Say("ok");

        await host.RunTurnAsync("refunds?");

        FirstPrompt(host).Should().Contain("Refunds within 14 days.").And.NotContain("Unrelated.");
        Warnings(host).Should().NotContain("discarded all");
    }

    [Fact]
    public async Task A_threshold_the_host_sets_wins_over_the_source()
    {
        var source = new RequestAwareSource { DefaultMinScore = 0.6f };
        source.Results.Add(new MentorRagResult("Refunds within 14 days.", null, "Refund policy", 0.82f));
        await using var host = WithSource(source, o => o.RagMinScore = 0.9f);
        host.Chat!.Say("ok");

        await host.RunTurnAsync("refunds?");

        FirstPrompt(host).Should().NotContain("Refunds within 14 days.",
            "the host knows its application; the source only knows its own scale");
    }

    [Fact]
    public async Task A_threshold_bound_from_configuration_is_the_hosts()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Mentor:RagMinScore"] = "0.9" })
            .Build();
        var source = new RequestAwareSource { DefaultMinScore = 0.6f };
        source.Results.Add(new MentorRagResult("Refunds within 14 days.", null, "Refund policy", 0.82f));
        await using var host = WithSource(source, o => configuration.GetSection("Mentor").Bind(o));
        host.Chat!.Say("ok");

        await host.RunTurnAsync("refunds?");

        host.Get<IOptions<MentorOptions>>().Value.RagMinScoreSetByHost.Should().BeTrue();
        FirstPrompt(host).Should().NotContain("Refunds within 14 days.");
    }

    [Fact]
    public void The_registration_snapshot_and_the_options_both_know_who_set_the_threshold()
    {
        static (bool Snapshot, bool Options) SetByHost(Action<MentorOptions> configure)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMentorAgent(o =>
            {
                o.AppName = "App";
                o.ScanAssemblies = [typeof(TestOrderService).Assembly];
                o.ChatClient = new FakeChatClient();
                configure(o);
            });
            var snapshot = (MentorRegistrationSnapshot)services
                .Single(d => d.ServiceType == typeof(MentorRegistrationSnapshot)).ImplementationInstance!;
            using var provider = services.BuildServiceProvider();
            return (snapshot.Options.RagMinScoreSetByHost,
                    provider.GetRequiredService<IOptions<MentorOptions>>().Value.RagMinScoreSetByHost);
        }

        SetByHost(o => o.RagMinScore = 2f).Should().Be((true, true),
            "setting the default value explicitly is still the host's decision");
        SetByHost(_ => { }).Should().Be((false, false));
    }

    [Fact]
    public async Task Without_a_declared_threshold_the_option_default_still_applies()
    {
        var source = new RequestAwareSource();
        source.Results.Add(new MentorRagResult("Keyword hit.", null, "Doc", 3f));
        source.Results.Add(new MentorRagResult("Weak hit.", null, "Doc 2", 1f));
        await using var host = WithSource(source);
        host.Chat!.Say("ok");

        await host.RunTurnAsync("keyword?");

        FirstPrompt(host).Should().Contain("Keyword hit.").And.NotContain("Weak hit.");
    }

    [Fact]
    public async Task The_wrong_scale_warning_names_the_source_threshold_when_that_is_what_discarded_everything()
    {
        // A source that declares 2 and returns cosine scores: its own threshold is on the wrong scale, and
        // the operator has to be sent to the source rather than to an option nobody set.
        var source = new RequestAwareSource { DefaultMinScore = 2f };
        source.Results.Add(new MentorRagResult("Refunds within 14 days.", null, "Refund policy", 0.82f));
        await using var host = WithSource(source);
        host.Chat!.Say("ok");

        await host.RunTurnAsync("refunds?");

        var warned = Warnings(host);
        warned.Should().Contain("the source's DefaultMinScore").And.Contain("0.82");
    }
}
