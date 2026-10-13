using FluentAssertions;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Rag;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Novior.MentorAgent.Tests.Scenarios;

/// <summary>
/// Part C2.6 — named pairs of features meeting on the same turn.
///
/// <para>
/// Every feature in this package has its own suite and every one of them passes. That is exactly the
/// condition under which interaction defects survive: each half behaves correctly in isolation, and
/// the damage happens where they overlap — two features trimming the same prompt, two deciding what
/// the model may call, one running inside a loop the other opened.
/// </para>
///
/// <para>
/// These are scripted end to end through the fixture rather than driven at a class, because the
/// interaction *is* the turn. Anything asserted at a single component here would be asserting the
/// thing that already works.
/// </para>
/// </summary>
public sealed class CrossFeatureTests
{
    /// <summary>
    /// The coordinator's own last request.
    ///
    /// <para>
    /// <c>MemoryAutoCapture</c> is <b>on by default</b>, and it ends every turn with a second,
    /// tool-less model call that extracts durable facts. That call is the one <c>LastCall</c>
    /// returns, and it carries neither the coordinator's instructions nor its tools — so asserting
    /// on <c>LastCall</c> with memory enabled measures the wrong request entirely. Three tests in
    /// this file failed that way before this helper existed.
    /// </para>
    /// </summary>
    private static FakeChatClient.RecordedCall Coordinator(MentorHostFixture host)
        => host.Chat!.Calls.Last(c => c.Tools.Count > 0);

    private static string Log(MentorHostFixture host)
        => string.Join("\n", host.Logs.Records.Select(r => $"{r.Level}:{r.Message}"));

    // ── RAG + tool filtering ────────────────────────────────────────────────────

    [Fact]
    public async Task Retrieved_documents_and_a_trimmed_tool_list_reach_the_same_call()
    {
        // The two prompt-shrinking features. They work on different halves of the request —
        // filtering on ChatOptions.Tools, RAG on Instructions — so the failure to look for is one
        // quietly undoing the other: a cloned ChatOptions that keeps the tools and loses the
        // instructions, or the reverse.
        await using var host = MentorHostFixture.Create(withEmbeddings: true, configure: o =>
        {
            o.UseRag = true;
            o.RagMinScore = 0.5f;
            o.EnableToolFiltering = true;
            o.ToolFilterMaxTools = 3;
        }, configureServices: s => s.AddSingleton<IMentorRagSource>(
            new ScriptedRagSource().Returns("Refunds take 14 days.", 0.9f, "Refund policy")));

        host.Embeddings!.OnAxis("count_open_orders", 0).OnAxis("how many open orders", 0);
        host.Chat!.Say("ok");

        await host.RunTurnAsync("how many open orders");

        var call = host.Chat!.LastCall!;
        (call.Instructions ?? "").Should().Contain("Refunds take 14 days.",
            "the retrieved document must survive the tool filter's options rebuild");
        call.ToolNames.Should().NotBeEmpty("and the filtered tool list must survive RAG's context injection");
    }

    // ── Skills + tool filtering ─────────────────────────────────────────────────

    [Fact]
    public async Task Tool_filtering_never_removes_the_skill_loader_itself()
    {
        // The catalogue is injected into the prompt on every turn and tells the model to call
        // load_skill. If the filter could remove it, the model would be instructed to call a tool
        // it cannot see — and would either invent an answer or apologise, both silently.
        await using var host = MentorHostFixture.Create(withEmbeddings: true, configure: o =>
        {
            o.EnableSkills = true;
            o.EnableToolFiltering = true;
            o.ToolFilterMaxTools = 1;
        });

        // Nothing is seeded on the query's axis, so every filterable tool scores below the
        // threshold: the always-keep set survives with at most ToolFilterMaxTools (1) action from the
        // zero-match fallback (BUG-106) — the skill loader is among the always-keep, which is the point.
        host.Chat!.Say("ok");
        await host.RunTurnAsync("tell me about your policies");

        Coordinator(host).ToolNames.Should().Contain("load_skill",
            "the skill loader is core — the catalogue in the prompt is useless without it");
    }

    [Fact]
    public async Task The_navigation_and_memory_tools_are_core_too()
    {
        // Same rule, same reason: each is named by an instruction block the prompt always carries.
        await using var host = MentorHostFixture.Create(withEmbeddings: true, configure: o =>
        {
            o.UseMemoryContext = true;
            o.MemoryAutoCapture = false;   // otherwise there is no `remember` tool to keep
            o.EnableToolFiltering = true;
            o.ToolFilterMaxTools = 1;
        });

        host.Chat!.Say("ok");
        await host.RunTurnAsync("something entirely unrelated to any business tool");

        var sent = Coordinator(host).ToolNames;
        sent.Should().Contain("navigate_to");
        sent.Should().Contain("remember");
    }

    // ── Memory + the per-turn prompt ────────────────────────────────────────────

    [Fact]
    public async Task A_remembered_fact_and_a_retrieved_document_both_reach_the_prompt()
    {
        // Two independent context providers writing into the same instructions block. The failure
        // mode is one overwriting the other's contribution rather than appending.
        await using var host = MentorHostFixture.Create(configure: o =>
        {
            o.UseMemoryContext = true;
            // The tool-based path. With the default MemoryAutoCapture the `remember` tool is
            // deliberately not registered at all — facts are extracted after the turn instead.
            o.MemoryAutoCapture = false;
            o.UseRag = true;
            o.RagMinScore = 0.5f;
        }, configureServices: s => s.AddSingleton<IMentorRagSource>(
            new ScriptedRagSource().Returns("Returns are free within 30 days.", 0.9f, "Returns")));

        host.Chat!.CallTool("remember", new { key = "shipping_preference", value = "The user prefers express shipping." }).Say("noted");
        await host.RunTurnAsync("remember that I prefer express shipping");

        host.Chat!.Say("ok");
        await host.RunTurnAsync("what is the returns policy");

        var instructions = Coordinator(host).Instructions ?? "";
        instructions.Should().Contain("express shipping", "the memory block is still there");
        instructions.Should().Contain("Returns are free within 30 days.", "and so is the document");
    }

    // ── Routing + metrics ───────────────────────────────────────────────────────

    [Fact]
    public async Task The_cost_is_attributed_to_the_model_that_actually_served_the_turn()
    {
        // Routing picks a model per turn; metrics record per model. Attributing a strong-model turn
        // to the cheap model would make the dashboard understate spend precisely when it matters —
        // the expensive turns are the ones anybody is watching for.
        await using var host = MentorHostFixture.Create(configure: o =>
        {
            o.RoutingStrategy = MentorRoutingStrategy.Custom;
            o.UseStrongModel = _ => true;
            o.StrongChatClient = new FakeChatClient { ModelId = "strong-model" }.SayWithUsage("ok", 100, 50);
        });

        await host.RunTurnAsync("anything at all");

        var metrics = host.Get<Novior.MentorAgent.Abstractions.Interfaces.IMentorMetrics>().GetSnapshot();
        var perModel = string.Join(", ", metrics.Models.Select(m => $"{m.ModelId}:{m.TotalTokens}"));

        perModel.Should().Contain("strong-model",
            $"the strong model served the turn; recorded instead: {perModel}");
    }

    // ── Cards + rich responses ──────────────────────────────────────────────────

    [Fact]
    public async Task A_card_does_not_also_arrive_as_a_markdown_table()
    {
        // Both features render structure. The card carries the fields for the widget and hands the
        // model a compact notice instead — if the notice still contained a table, the user would see
        // the same data twice, once as a card and once as prose underneath it.
        await using var host = MentorHostFixture.Create(withRecorder: true, configure: o =>
        {
            o.EnableGenerativeCards = true;
            o.EnableRichResponses = true;
        });

        host.Chat!.CallTool("show_order_card", new { orderId = 7 }).Say("There it is.");
        await host.RunTurnAsync("show me order 7");

        var toolResultSeenByModel = string.Join("\n", host.Chat!.Calls
            .SelectMany(c => c.Messages)
            .SelectMany(m => m.Contents.OfType<Microsoft.Extensions.AI.FunctionResultContent>())
            .Select(r => r.Result?.ToString() ?? ""));

        toolResultSeenByModel.Should().NotContain("|---",
            "the model is given a notice, not a table to echo back");
    }

    // ── HITL + streaming ────────────────────────────────────────────────────────

    [Fact]
    public async Task An_approval_raised_mid_turn_does_not_strand_the_stream()
    {
        // The documented deadlock case, from the orchestrator's side: a confirmation is raised while
        // a turn is in flight, and the turn is parked inside SendMessage. Any test here must carry a
        // deadline — a test for a hang that cannot stop the hang is not a test.
        await using var host = MentorHostFixture.Create();
        var state = host.Get<Novior.MentorAgent.Abstractions.Interfaces.IMentorStateService>();
        state.OnConfirmationRequired += req => _ = state.ConfirmAsync(req.ActionId);

        host.Chat!.CallTool("issue_refund", new { orderId = 7 }).Say("Refunded.");

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var error = await host.RunTurnAsync("refund order 7", deadline.Token);

        error.Should().BeNull();
        host.Get<Novior.MentorAgent.Tests.App.TestOrderService>().Invoked
            .Should().Contain(nameof(Novior.MentorAgent.Tests.App.TestOrderService.IssueRefundAsync));
    }

    // ── Guardrails + everything downstream ──────────────────────────────────────

    [Fact]
    public async Task A_refused_message_costs_nothing_downstream()
    {
        // The guardrail runs outermost. If a refusal still reached the model, the rate limiter, the
        // metrics and the RAG search, then a blocked message would cost a provider call, a vector
        // search and a slot in the user's quota — which turns a guardrail into an amplifier.
        var rag = new ScriptedRagSource().Returns("Anything.", 9f);

        await using var host = MentorHostFixture.Create(configure: o =>
        {
            o.UseRag = true;
            o.InputGuardrail = (_, _) => Task.FromResult(false);
        }, configureServices: s => s.AddSingleton<IMentorRagSource>(rag));

        host.Chat!.Say("should never be reached");
        await host.RunTurnAsync("something forbidden");

        host.Chat!.CallCount.Should().Be(0, "the model must not be called for a refused message");
        rag.Searches.Should().BeEmpty("nor must the knowledge base be searched and billed for");
    }

    // ── Compaction + memory ─────────────────────────────────────────────────────

    [Fact]
    public async Task Compaction_being_on_does_not_remove_the_memory_block()
    {
        // Memory rides in Instructions, which compaction does not touch — history is what it
        // reduces. Worth an explicit test because "the facts disappeared after a long conversation"
        // is the report this combination would produce, and it would be blamed on memory.
        await using var host = MentorHostFixture.Create(configure: o =>
        {
            o.UseMemoryContext = true;
            o.MemoryAutoCapture = false;
            o.EnableCompaction = true;
            o.CompactionTokenThreshold = 1;
        });

        host.Chat!.CallTool("remember", new { key = "account_number", value = "The user's account number is 4471." }).Say("noted");
        await host.RunTurnAsync("my account number is 4471");

        for (var i = 0; i < 3; i++)
        {
            host.Chat!.Say($"reply {i}");
            await host.RunTurnAsync($"message {i}");
        }

        (Coordinator(host).Instructions ?? "").Should().Contain("4471",
            "the memory block is rebuilt every turn and compaction never sees it");

        // BUG-104: the first turn called a tool (remember), so compaction ran twice in it and marked the
        // question as history in place — and the provider then declined to store it. Asserted on the
        // STORED conversation: with a threshold of 1, truncation may legitimately drop old groups from
        // what is sent, never from what is kept.
        (await host.StoredHistoryAsync())
            .Count(m => m.Role == Microsoft.Extensions.AI.ChatRole.User && m.Text == "my account number is 4471")
            .Should().Be(1, "the question of a turn that called a tool is stored like any other");
    }

    // ── Hosted tools + service-managed history ──────────────────────────────────

    [Fact]
    public async Task The_induced_history_conflict_is_reported_rather_than_thrown_every_turn()
    {
        // The documented trap: hosted tools force the Responses client, which forces service-managed
        // history, which excludes a local ChatHistoryProvider. Left unguarded it throws "Only
        // ConversationId or ChatHistoryProvider may be used, but not both" on *every* turn, which
        // reads as a broken assistant rather than a configuration mistake.
        await using var host = MentorHostFixture.Create(configure: o =>
        {
            o.UseServiceManagedHistory = true;
            o.ChatHistoryProvider = new Microsoft.Agents.AI.InMemoryChatHistoryProvider();
        });

        host.Chat!.Say("ok");
        var error = await host.RunTurnAsync("hello");

        error.Should().BeNull("the conflict is resolved at build time, not raised at turn time");
        Log(host).Should().Contain("ChatHistoryProvider",
            "and the host is told which of the two settings was ignored");
    }

    // ── Gating + metrics ────────────────────────────────────────────────────────

    [Fact]
    public async Task A_refused_tool_call_is_not_recorded_as_a_successful_one()
    {
        // The dashboard's tool counters are what an operator reads to decide whether a feature is
        // used. Counting a call the role gate refused would report adoption of an action nobody was
        // ever allowed to run.
        await using var host = MentorHostFixture.Create();
        host.Auth.SignOut();

        host.Chat!.CallTool("export_ledger").Say("done");
        await host.RunTurnAsync("export the ledger");

        host.Get<Novior.MentorAgent.Tests.App.TestOrderService>().Invoked
            .Should().NotContain(nameof(Novior.MentorAgent.Tests.App.TestOrderService.ExportLedger),
                "an anonymous caller must not reach an Admin-only action");
    }
}
