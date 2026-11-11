using FluentAssertions;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Rag;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Novior.MentorAgent.Tests.Rag;

/// <summary>
/// Story 194 — a search of the knowledge base that kept no document left the model with nothing to go on, and it answered
/// from memory. With <see cref="MentorOptions.RagNoResultsNotice"/> the request's instructions say that the search found
/// nothing, in the language of the turn: only after a search that ran to the end with a real source, whether it returned
/// nothing or every document was below the threshold. Without the option nothing changes.
/// </summary>
public sealed class RagNoResultsNoticeTests
{
    private const string DocumentsMarker = "Use the following documents to answer:";

    private static (MentorHostFixture Host, ScriptedRagSource Source) WithRag(
        Action<ScriptedRagSource>? seed = null, Action<MentorOptions>? configure = null, bool registerSource = true)
    {
        var source = new ScriptedRagSource();
        seed?.Invoke(source);
        var host = MentorHostFixture.Create(o =>
            {
                o.UseRag = true;
                o.RagNoResultsNotice = true;
                o.MemoryAutoCapture = false;
                o.CheckAnnouncedActions = false;   // its question would take the reply scripted for the turn
                configure?.Invoke(o);
            },
            services =>
            {
                if (registerSource) services.AddSingleton<IMentorRagSource>(source);
            });
        host.Chat!.Say("Ok.");
        return (host, source);
    }

    /// <summary>The instructions of the turn's first request.</summary>
    private static string FirstInstructions(MentorHostFixture host) => host.Chat!.Calls[0].Instructions ?? "";

    private static string Notice(MentorLanguage language)
        => RagContextProvider.NothingFoundHeading + MentorPromptTexts.For(language).RagNothingFound;

    [Fact]
    public async Task With_the_option_a_search_that_finds_nothing_tells_the_model()
    {
        var (host, source) = WithRag();
        await using var _ = host;

        await host.RunTurnAsync("what is the return policy?");

        source.Searches.Should().ContainSingle();
        FirstInstructions(host).Should().Contain(Notice(MentorLanguage.English)).And.NotContain(DocumentsMarker);
    }

    [Fact]
    public async Task The_notice_is_in_the_language_of_the_turn()
    {
        var (host, _) = WithRag(configure: o => o.Language = MentorLanguage.Italian);
        await using var __ = host;

        await host.Orchestrator.SendMessageAsync(new MentorUserMessage("quelle est la politique de retour ?")
        {
            Language = MentorLanguage.French,
        });

        FirstInstructions(host).Should().Contain(Notice(MentorLanguage.French))
            .And.NotContain(MentorPromptTexts.Italian.RagNothingFound)
            .And.NotContain(MentorPromptTexts.English.RagNothingFound);
    }

    [Fact]
    public async Task A_document_above_the_threshold_is_given_as_before_and_no_notice()
    {
        var (host, _) = WithRag(s => s.Returns("Returns are accepted within 30 days.", 9f, "Returns"));
        await using var _ = host;

        await host.RunTurnAsync("what is the return policy?");

        FirstInstructions(host).Should().Contain(DocumentsMarker).And.Contain("Returns are accepted within 30 days.")
            .And.NotContain(RagContextProvider.NothingFoundHeading);
    }

    [Fact]
    public async Task Every_document_below_the_threshold_counts_as_nothing_found()
    {
        var (host, _) = WithRag(s => s.Returns("Returns are accepted within 30 days.", 0.5f, "Returns"),
            o => o.RagMinScore = 2);
        await using var _ = host;

        await host.RunTurnAsync("what is the return policy?");

        FirstInstructions(host).Should().Contain(Notice(MentorLanguage.English))
            .And.NotContain("Returns are accepted within 30 days.");
    }

    [Fact]
    public async Task Without_the_option_a_search_that_finds_nothing_tells_the_model_nothing()
    {
        var (host, source) = WithRag(configure: o => o.RagNoResultsNotice = false);
        await using var _ = host;

        await host.RunTurnAsync("what is the return policy?");

        source.Searches.Should().ContainSingle();
        FirstInstructions(host).Should().NotContain(RagContextProvider.NothingFoundHeading).And.NotContain(DocumentsMarker);
    }

    [Fact]
    public async Task A_search_that_fails_gives_no_notice()
    {
        var (host, source) = WithRag();
        await using var _ = host;
        source.ThrowOnNextSearch = new InvalidOperationException("the index is down");

        await host.RunTurnAsync("what is the return policy?");

        FirstInstructions(host).Should().NotContain(RagContextProvider.NothingFoundHeading,
            "nothing was searched: saying that nothing was found would be untrue");
    }

    [Fact]
    public async Task Without_a_source_there_is_no_notice()
    {
        var (host, _) = WithRag(registerSource: false);
        await using var __ = host;

        await host.RunTurnAsync("what is the return policy?");

        FirstInstructions(host).Should().NotContain(RagContextProvider.NothingFoundHeading);
    }

    [Fact]
    public async Task With_rag_off_there_is_no_notice()
    {
        var (host, source) = WithRag(configure: o => o.UseRag = false);
        await using var _ = host;

        await host.RunTurnAsync("what is the return policy?");

        source.Searches.Should().BeEmpty();
        FirstInstructions(host).Should().NotContain(RagContextProvider.NothingFoundHeading);
    }
}
