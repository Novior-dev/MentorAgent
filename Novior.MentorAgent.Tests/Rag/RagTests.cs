using FluentAssertions;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Rag;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Novior.MentorAgent.Tests.Rag;

/// <summary>
/// Retrieval-augmented generation: what is searched, what survives the score threshold, what the
/// model is shown, and what the user is told it was shown.
///
/// <para>
/// RAG has an unusual failure shape. Everything else in MentorAgent either works or reports itself;
/// a retrieval that returns nothing is indistinguishable from a retrieval that was never wired up,
/// because the assistant answers perfectly well from its own knowledge either way. The user-visible
/// consequence is a confident answer that ignores the knowledge base — which is the exact outcome
/// RAG was switched on to prevent.
/// </para>
///
/// <para>
/// The other half is the citation chips. They are a claim made to the user about where an answer
/// came from, so a chip for a document that never reached the model is worse than no chip at all.
/// Every assertion about chips here is paired with one about the prompt.
/// </para>
/// </summary>
public sealed class RagTests
{
    private const string Marker = "Use the following documents to answer:";

    /// <summary>A host with a scripted knowledge base registered the documented way.</summary>
    private static (MentorHostFixture Host, ScriptedRagSource Source) WithRag(
        Action<ScriptedRagSource> seed,
        Action<MentorOptions>? configure = null)
    {
        var source = new ScriptedRagSource();
        seed(source);

        var host = MentorHostFixture.Create(
            o =>
            {
                o.UseRag = true;
                configure?.Invoke(o);
            },
            // Before AddMentorAgent — the position the README documents for a custom source, and
            // the one TryAddScoped depends on.
            services => services.AddSingleton<IMentorRagSource>(source),
            withRecorder: true);

        return (host, source);
    }

    /// <summary>Everything the model was told this turn, across instructions and system messages.</summary>
    private static string PromptOf(FakeChatClient.RecordedCall call)
        => (call.Instructions ?? "") + "\n" + call.SystemText;

    private static string FirstPrompt(MentorHostFixture host) => PromptOf(host.Chat!.Calls[0]);

    private static IReadOnlyList<MentorRagResult> ChipsOf(MentorHostFixture host)
        => host.Recorder!.Log
            .Where(e => e.Event == nameof(RecordingStateService.NotifyRagSourcesReady))
            .Select(e => (IReadOnlyList<MentorRagResult>)e.Args[0]!)
            .LastOrDefault() ?? [];

    // ── Retrieval ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Rag_off_never_searches()
    {
        // The search costs a round trip to a vector store on every single message. An option that
        // is off has to cost nothing at all.
        var (host, source) = WithRag(s => s.Returns("anything", 9f), o => o.UseRag = false);
        await using var _ = host;

        await host.RunTurnAsync("where is my order");

        source.Searches.Should().BeEmpty();
    }

    [Fact]
    public async Task The_search_query_is_the_user_message()
    {
        var (host, source) = WithRag(s => s.Returns("Shipping takes 3 days.", 9f, "Shipping policy"));
        await using var _ = host;

        await host.RunTurnAsync("how long does shipping take");

        source.Searches.Should().ContainSingle().Which.Query.Should().Be("how long does shipping take");
    }

    [Fact]
    public async Task The_source_is_asked_for_exactly_RagResultCount_documents()
    {
        var (host, source) = WithRag(s => s.Returns("a", 9f), o => o.RagResultCount = 3);
        await using var _ = host;

        await host.RunTurnAsync("anything");

        source.Searches.Single().MaxResults.Should().Be(3);
    }

    [Fact]
    public async Task Without_a_registered_source_nothing_is_retrieved()
    {
        // TryAddScoped installs a no-op source so the provider never has to null-check. The
        // consequence is that RAG "works" with no backend at all — see the reporting test below.
        await using var host = MentorHostFixture.Create(o => o.UseRag = true);

        await host.RunTurnAsync("how long does shipping take");

        FirstPrompt(host).Should().NotContain(Marker);
    }

    // ── Injection ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Retrieved_documents_reach_the_model()
    {
        var (host, _) = WithRag(s => s.Returns("Shipping takes 3 working days.", 9f, "Shipping policy"));
        await using var __ = host;

        await host.RunTurnAsync("how long does shipping take");

        var prompt = FirstPrompt(host);
        prompt.Should().Contain(Marker, "the default template wraps the documents");
        prompt.Should().Contain("Shipping takes 3 working days.");
    }

    [Fact]
    public async Task A_custom_template_is_used_verbatim()
    {
        var (host, _) = WithRag(
            s => s.Returns("Bay 4 is closed.", 9f, "Warehouse"),
            o => o.RagSystemPromptTemplate = "SOURCES BELOW — cite them.\n{documents}\nEND");
        await using var _ = host;

        await host.RunTurnAsync("what about the warehouse");

        var prompt = FirstPrompt(host);
        prompt.Should().Contain("SOURCES BELOW — cite them.").And.Contain("Bay 4 is closed.").And.Contain("END");
        prompt.Should().NotContain(Marker, "a custom template replaces the default, it does not join it");
    }

    [Fact]
    public async Task The_placeholder_is_matched_whatever_its_case()
    {
        // Replace is called with OrdinalIgnoreCase, so {DOCUMENTS} and {Documents} work too. Worth
        // pinning: a template is hand-written configuration and the failure is silent — the wrong
        // case leaves the literal placeholder in the prompt and drops every document.
        var (host, _) = WithRag(
            s => s.Returns("Bay 4 is closed.", 9f, "Warehouse"),
            o => o.RagSystemPromptTemplate = "Sources:\n{DOCUMENTS}");
        await using var _ = host;

        await host.RunTurnAsync("what about the warehouse");

        var prompt = FirstPrompt(host);
        prompt.Should().Contain("Bay 4 is closed.");
        prompt.Should().NotContain("{DOCUMENTS}");
    }

    [Fact]
    public async Task Each_document_is_headed_by_its_title()
    {
        var (host, _) = WithRag(s => s.Returns("Bay 4 is closed.", 9f, "Warehouse notice"));
        await using var _ = host;

        await host.RunTurnAsync("anything");

        FirstPrompt(host).Should().Contain("[1] Warehouse notice\nBay 4 is closed.");
    }

    [Fact]
    public async Task A_document_with_no_title_is_headed_by_its_url()
    {
        var (host, _) = WithRag(s => s.Returns("Bay 4 is closed.", 9f, title: null, url: "https://kb/warehouse"));
        await using var _ = host;

        await host.RunTurnAsync("anything");

        FirstPrompt(host).Should().Contain("[1] https://kb/warehouse\nBay 4 is closed.");
    }

    [Fact]
    public async Task A_document_with_neither_a_title_nor_a_url_still_gets_a_heading()
    {
        // Without the fallback the block would open with "[]", which reads to a model as an empty
        // citation rather than an unnamed one.
        var (host, _) = WithRag(s => s.Returns("Bay 4 is closed.", 9f));
        await using var _ = host;

        await host.RunTurnAsync("anything");

        FirstPrompt(host).Should().Contain("[1] Document\nBay 4 is closed.");
    }

    [Fact]
    public async Task Documents_are_separated_so_two_cannot_read_as_one()
    {
        var (host, _) = WithRag(s => s
            .Returns("First fact.", 9f, "A")
            .Returns("Second fact.", 9f, "B"));
        await using var _ = host;

        await host.RunTurnAsync("anything");

        FirstPrompt(host).Should().Contain("[1] A\nFirst fact.\n\n[2] B\nSecond fact.");
    }

    [Fact]
    public async Task Retrieving_nothing_injects_nothing()
    {
        // Not an empty documents block: the template itself must not be sent, or the model is told
        // to use documents it cannot see and will invent them.
        var (host, _) = WithRag(_ => { });
        await using var __ = host;

        await host.RunTurnAsync("anything");

        FirstPrompt(host).Should().NotContain(Marker);
    }

    // ── The score threshold ─────────────────────────────────────────────────────

    [Fact]
    public async Task Documents_below_the_minimum_score_never_reach_the_model()
    {
        var (host, _) = WithRag(
            s => s.Returns("Relevant.", 5f, "Good").Returns("Barely related.", 1f, "Weak"),
            o => o.RagMinScore = 2f);
        await using var _ = host;

        await host.RunTurnAsync("anything");

        var prompt = FirstPrompt(host);
        prompt.Should().Contain("Relevant.");
        prompt.Should().NotContain("Barely related.");
    }

    [Fact]
    public async Task A_document_exactly_at_the_threshold_is_kept()
    {
        // The comparison is >=, and the documentation says "must reach" — an off-by-one here
        // silently narrows every knowledge base by one band of relevance.
        var (host, _) = WithRag(s => s.Returns("Exactly at two.", 2f, "Edge"), o => o.RagMinScore = 2f);
        await using var _ = host;

        await host.RunTurnAsync("anything");

        FirstPrompt(host).Should().Contain("Exactly at two.");
    }

    [Fact]
    public async Task A_minimum_score_of_zero_keeps_everything()
    {
        // Documented: "Set to 0 to disable filtering". A score of exactly 0 must survive it, which
        // a naive `>= 0` filter would also do — but a `> 0` one would not.
        var (host, _) = WithRag(
            s => s.Returns("Zero scored.", 0f, "Zero").Returns("Negative scored.", -1f, "Neg"),
            o => o.RagMinScore = 0f);
        await using var _ = host;

        await host.RunTurnAsync("anything");

        var prompt = FirstPrompt(host);
        prompt.Should().Contain("Zero scored.").And.Contain("Negative scored.");
    }

    [Fact]
    public async Task A_negative_minimum_score_also_disables_filtering()
    {
        var (host, _) = WithRag(s => s.Returns("Low.", 0.1f, "Low"), o => o.RagMinScore = -1f);
        await using var _ = host;

        await host.RunTurnAsync("anything");

        FirstPrompt(host).Should().Contain("Low.");
    }

    [Fact]
    public async Task Everything_falling_below_the_threshold_is_the_same_as_retrieving_nothing()
    {
        var (host, _) = WithRag(
            s => s.Returns("Weak.", 0.5f, "Weak"),
            o => o.RagMinScore = 2f);
        await using var _ = host;

        await host.RunTurnAsync("anything");

        FirstPrompt(host).Should().NotContain(Marker);
        ChipsOf(host).Should().BeEmpty("a chip for a document the model never saw is a false citation");
    }

    // ── Citation chips ──────────────────────────────────────────────────────────

    [Fact]
    public async Task The_chips_are_the_documents_the_answer_cited()
    {
        // Changed in rc.5, and the reason is what the live pass showed: the chips used to be
        // "whatever the retriever returned", so every answer carried three sources — including the
        // ones that came from a skill, from a tool result, or from the model's own knowledge. A
        // source the answer did not use is not a source, and a citation the reader cannot trust is
        // worse than no citation at all.
        var (host, _) = WithRag(s => s.Returns("Kept.", 5f, "Good").Returns("Other.", 5f, "Second"));
        await using var _ = host;
        host.Chat!.Say("Secondo [1], la risposta è sì.");

        await host.RunTurnAsync("anything");

        string.Join(",", ChipsOf(host).Select(c => c.Title)).Should().Be("Good");
    }

    [Fact]
    public async Task An_answer_that_cites_nothing_shows_no_chips()
    {
        // The retrieval still happened and still cost a search; it simply did not end up in the
        // answer, and saying otherwise would be a false citation.
        var (host, _) = WithRag(s => s.Returns("Kept.", 5f, "Good"));
        await using var _ = host;
        host.Chat!.Say("Te lo dico a memoria, senza consultare nulla.");

        await host.RunTurnAsync("anything");

        ChipsOf(host).Should().BeEmpty();
    }

    [Fact]
    public async Task A_citation_number_that_does_not_exist_is_ignored()
    {
        // Models invent citations. An out-of-range marker must not crash the turn and must not
        // produce a chip for a document nobody has.
        var (host, _) = WithRag(s => s.Returns("Kept.", 5f, "Good"));
        await using var _ = host;
        host.Chat!.Say("Secondo [7] e [1], sì.");

        await host.RunTurnAsync("anything");

        string.Join(",", ChipsOf(host).Select(c => c.Title)).Should().Be("Good");
    }

    [Fact]
    public async Task The_same_document_cited_twice_is_one_chip()
    {
        var (host, _) = WithRag(s => s.Returns("Kept.", 5f, "Good"));
        await using var _ = host;
        host.Chat!.Say("Come dice [1], e ancora [1].");

        await host.RunTurnAsync("anything");

        ChipsOf(host).Should().HaveCount(1);
    }

    [Fact]
    public async Task The_model_is_told_how_to_cite()
    {
        // Without the instruction the numbering is decoration and no answer would ever cite, which
        // would silently turn the chips off for everybody.
        var (host, _) = WithRag(s => s.Returns("Kept.", 5f, "Good"));
        await using var _ = host;

        await host.RunTurnAsync("anything");

        FirstPrompt(host).Should().Contain("cite it inline as");
    }

    [Fact]
    public async Task No_chips_are_raised_when_nothing_was_retrieved()
    {
        var (host, _) = WithRag(_ => { });
        await using var __ = host;

        await host.RunTurnAsync("anything");

        host.Recorder!.Count(nameof(RecordingStateService.NotifyRagSourcesReady)).Should().Be(0);
    }

    [Fact]
    public async Task Chips_from_one_turn_do_not_reappear_on_the_next()
    {
        // The provider caches the retrieval between Prepare and Store; failing to reset it would
        // show the previous question's citations under an answer that used none.
        var source = new ScriptedRagSource().Returns("Shipping takes 3 days.", 9f, "Shipping policy");
        await using var host = MentorHostFixture.Create(
            o => o.UseRag = true,
            services => services.AddSingleton<IMentorRagSource>(source),
            withRecorder: true);

        await host.RunTurnAsync("how long does shipping take");
        host.Recorder!.Reset();

        // Second turn: the store answers with nothing at all.
        var empty = new ScriptedRagSource();
        typeof(ScriptedRagSource).GetProperty(nameof(ScriptedRagSource.ReturnNull));   // keep the analyser quiet
        _ = empty;

        source.ThrowOnNextSearch = new InvalidOperationException("index offline");
        await host.RunTurnAsync("and what about returns");

        host.Recorder.Count(nameof(RecordingStateService.NotifyRagSourcesReady)).Should().Be(0);
        PromptOf(host.Chat!.Calls[^1]).Should().NotContain("Shipping takes 3 days.");
    }

    // ── Failure ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_source_that_throws_does_not_take_the_turn_down()
    {
        // A vector store is a network dependency. Losing the documents is acceptable; losing the
        // answer is not — the user asked a question that the model can very likely still handle.
        var (host, source) = WithRag(s => s.Returns("Never seen.", 9f, "Doc"));
        await using var _ = host;
        source.ThrowOnNextSearch = new HttpRequestException("index offline");

        var error = await host.RunTurnAsync("anything");

        error.Should().BeNull();
        host.Chat!.Calls.Should().NotBeEmpty("the turn still reached the model");
        FirstPrompt(host).Should().NotContain(Marker);
        host.Logs.HasWarning("PrepareAsync failed").Should().BeTrue(host.Logs.WarningText);
    }

    [Fact]
    public async Task A_source_that_answers_with_null_is_survivable()
    {
        // Not reachable through a correct implementation, which is the point: this is somebody
        // else's code behind an interface, and the contract cannot be enforced at compile time.
        var (host, source) = WithRag(_ => { });
        await using var _ = host;
        source.ReturnNull = true;

        var error = await host.RunTurnAsync("anything");

        error.Should().BeNull();
        host.Logs.HasWarning("PrepareAsync failed").Should().BeTrue(host.Logs.WarningText);
    }

    [Fact]
    public async Task A_failed_retrieval_does_not_poison_the_following_turn()
    {
        var (host, source) = WithRag(s => s.Returns("Shipping takes 3 days.", 9f, "Shipping policy"));
        await using var _ = host;
        source.ThrowOnNextSearch = new HttpRequestException("index offline");

        await host.RunTurnAsync("first");
        await host.RunTurnAsync("second");

        PromptOf(host.Chat!.Calls[^1]).Should().Contain("Shipping takes 3 days.",
            "the outage was transient and the second search succeeded");
    }

    // ── Scope ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Rag_is_attached_to_the_coordinator_and_not_to_a_specialist()
    {
        // One search per turn, on the coordinator. A Level-2 specialist receives what it needs
        // through the request the coordinator hands it; attaching the provider to every agent would
        // re-inject the same documents into every nested call and multiply the token cost.
        var (host, source) = WithRag(s => s.Returns("Bay 4 is closed.", 9f, "Warehouse"));
        await using var _ = host;

        host.Chat!.CallTool("route_to_specialist", new { request = "where is order 7" }).Say("done");
        await host.RunTurnAsync("where is order 7");

        source.Searches.Should().HaveCount(1, "the specialist must not trigger a second retrieval");
        host.Chat.Calls.Should().Contain(c => !PromptOf(c).Contains(Marker),
            "at least one nested call — the specialist's — carries no RAG block");
    }

    // ── Reporting: an enabled feature that silently does nothing ────────────────

    [Fact]
    public async Task Rag_enabled_with_no_source_registered_is_reported()
    {
        // Every other capability of this shape warns: tool filtering without an embedding
        // generator, hosted tools without a chat client, a team without members. RAG is the one
        // that does not — UseRag = true with no IMentorRagSource lands on the no-op default, and
        // the assistant answers from its own knowledge with nothing to show that the knowledge base
        // was never consulted. That is the whole failure mode the constraint matrix exists for.
        await using var host = MentorHostFixture.Create(o => o.UseRag = true);

        await host.RunTurnAsync("how long does shipping take");

        host.Logs.HasWarning("UseRag").Should().BeTrue(
            "an enabled feature with no backend has to say so. Warnings seen:{0}{1}",
            Environment.NewLine, host.Logs.WarningText);
    }

    [Fact]
    public async Task A_template_that_drops_every_document_is_reported()
    {
        // The template is hand-written configuration and the placeholder is a magic string. Get it
        // wrong — {docs}, {0}, or a translated {documenti} — and Replace matches nothing: the
        // retrieval runs, the threshold applies, the citation chips appear under the answer, and
        // the documents themselves are never sent. The user is shown sources the model never read.
        var (host, _) = WithRag(
            s => s.Returns("Bay 4 is closed.", 9f, "Warehouse"),
            o => o.RagSystemPromptTemplate = "Use the following documents:\n{docs}");
        await using var _ = host;

        await host.RunTurnAsync("what about the warehouse");

        host.Logs.HasWarning("RagSystemPromptTemplate").Should().BeTrue(
            "a template with no {{documents}} placeholder injects no documents at all. Warnings seen:{0}{1}",
            Environment.NewLine, host.Logs.WarningText);
    }

    // ── Path B ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PathB_searches_and_the_result_reaches_the_model()
    {
        // The core README's matrix ticks RAG ✅ for Path B. The retrieval genuinely runs there —
        // PrepareAsync is called by the orchestrator on both paths — but the injection is an
        // AIContextProvider, and those are attached only to the agent MentorAgent builds itself.
        // See BUG-015: on Path B the search is paid for and the documents are discarded.
        var inner = new FakeChatClient();
        var source = new ScriptedRagSource().Returns("Bay 4 is closed.", 9f, "Warehouse");

        await using var host = MentorHostFixture.Create(
            o =>
            {
                o.ChatClient = null;
                o.Agent = inner.AsAIAgent(instructions: "You are a test agent.");
                o.UseRag = true;
            },
            services => services.AddSingleton<IMentorRagSource>(source),
            withChatClient: false);

        await host.RunTurnAsync("what about the warehouse");

        source.Searches.Should().ContainSingle("the orchestrator prepares RAG on both paths");
        PromptOf(inner.Calls[0]).Should().Contain("Bay 4 is closed.",
            "a retrieval the host pays for must reach the model, or RAG is not ✅ on this path");
    }
}
