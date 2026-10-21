using FluentAssertions;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Novior.MentorAgent.Tests.Filtering;

/// <summary>
/// Relevance-based tool filtering with a generator present.
///
/// <para>
/// The degradation case — no <c>EmbeddingGenerator</c> — is covered in the constraint suite, and the
/// rule it enforces is absolute: send every tool and warn, <b>never</b> fall back to matching
/// keywords against the user's text. This suite is the other half: what the filter does when it
/// <i>can</i> run.
/// </para>
///
/// <para>
/// Two properties matter more than the trimming itself. A tool that is silently dropped becomes a
/// capability the assistant claims and cannot use — and the system prompt has already announced it,
/// so the model resolves the contradiction by inventing a result. And a selection that is not held
/// stable for the rest of the turn drops the very tool whose result is being handed back, which the
/// provider then rejects.
/// </para>
/// </summary>
public sealed class ToolFilterTests(ITestOutputHelper output)
{
    /// <summary>Axis 0 = the user's message and the tools that should match it.</summary>
    private const int Relevant = 0;

    /// <summary>Axis 1 = everything unrelated.</summary>
    private const int Unrelated = 1;

    private static MentorHostFixture WithFilter(
        Action<Novior.MentorAgent.Options.MentorOptions>? configure = null)
        => MentorHostFixture.Create(o =>
        {
            o.EnableToolFiltering = true;
            configure?.Invoke(o);
        }, withEmbeddings: true);

    /// <summary>
    /// Pins the query on the relevant axis and <b>every</b> tool on the unrelated one, then moves
    /// the named tools onto the relevant axis.
    ///
    /// <para>
    /// Seeding all of them matters. An unseeded input falls back to a hash-derived vector, which
    /// points in an arbitrary direction — and two of the fixture's tools happen to land close
    /// enough to the query axis to clear the default threshold. That makes "unrelated" a property
    /// of the hash rather than of the test, which is the sort of thing that passes today and fails
    /// when someone renames a method.
    /// </para>
    /// </summary>
    private static void SeedOnlyRelevant(MentorHostFixture host, string query, params string[] relevantTools)
    {
        var emb = host.Embeddings!;
        emb.OnAxis(query, Relevant);

        foreach (var text in ToolTexts) emb.OnAxis(text, Unrelated);
        foreach (var text in relevantTools) emb.OnAxis(text, Relevant);
    }

    // ── The selection ───────────────────────────────────────────────────────────

    [Fact]
    public async Task An_unrelated_tool_is_dropped_and_a_relevant_one_survives()
    {
        // Threshold alone (ToolFilterMinTools = 0): with the rank floor on, the next best-ranked
        // actions travel with a lone match on purpose — see the BUG-099 tests below.
        const string query = "cancel my order please";
        await using var host = WithFilter(o => o.ToolFilterMinTools = 0);
        SeedOnlyRelevant(host, query, "cancel_order: Cancels an existing order");
        host.Chat!.SayRepeatedly("ok", times: 2);

        await host.RunTurnAsync(query);

        output.WriteLine(string.Join(", ", host.FirstToolsSent));
        host.FirstToolsSent.Should().Contain("cancel_order");
        host.FirstToolsSent.Should().NotContain("get_weather",
            "an unrelated action is exactly what the filter exists to remove");
    }

    [Fact]
    public async Task Core_tools_are_never_filtered_away()
    {
        // navigate_to and the memory tools are not application actions — they are how the assistant
        // works at all. Scoring them against the user's message would remove navigation from any
        // turn that did not mention navigating.
        const string query = "something completely unrelated to everything";
        await using var host = WithFilter();
        SeedOnlyRelevant(host, query);
        host.Chat!.SayRepeatedly("ok", times: 2);

        await host.RunTurnAsync(query);

        host.FirstToolsSent.Should().Contain("navigate_to",
            "core tools are always-keep; only application actions are candidates");
    }

    [Fact]
    public async Task With_nothing_relevant_three_actions_that_need_no_confirmation_travel()
    {
        // BUG-106 reversed the rc.12 decision that "no relevant tool" is reliable and costs no tools. It is
        // not: the right read for "Quanto costa il Dell XPS 15?" scored 0.209, below "Ciao" at 0.280 — no
        // line separates them. So a message that matches nothing still carries the three best-ranked
        // actions that need no confirmation (about 80 input tokens), never a gated or role-gated one.
        const string query = "an utterly unrelated question about nothing";
        await using var host = WithFilter();
        SeedOnlyRelevant(host, query);
        host.Chat!.SayRepeatedly("ok", times: 2);

        await host.RunTurnAsync(query);

        host.FirstToolsSent.Where(t => ApplicationActions.Contains(t) || SkillActions.Contains(t))
            .Should().HaveCount(3, "which three is a tie here; that there are three is the rule");
        host.FirstToolsSent.Should().NotContain(["cancel_order", "issue_refund", "export_ledger"],
            "a zero-match turn never carries an action that needs confirmation or a role");
    }

    [Fact]
    public async Task An_empty_message_sends_core_tools_only_without_calling_the_embedder()
    {
        // Reachable from an image-only turn. Embedding an empty string costs a round trip and
        // returns a meaningless vector, so the filter short-circuits.
        await using var host = WithFilter();
        host.Chat!.SayRepeatedly("ok", times: 2);

        await host.RunTurnAsync("   ");

        host.FirstToolsSent.Should().NotContain("cancel_order");
    }

    // ── MinScore ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_tool_scoring_just_above_min_score_is_kept()
    {
        const string query = "borderline request";
        await using var host = WithFilter(o => o.ToolFilterMinScore = 0.30f);
        host.Embeddings!
            .OnAxis(query, Relevant)
            .AtSimilarity("cancel_order: Cancels an existing order", Relevant, 0.35f);
        host.Chat!.SayRepeatedly("ok", times: 2);

        await host.RunTurnAsync(query);

        host.FirstToolsSent.Should().Contain("cancel_order");
    }

    [Fact]
    public async Task A_tool_scoring_just_below_min_score_is_dropped()
    {
        // The pair above and below is the point: a threshold tested only from one side passes for
        // any threshold at all.
        const string query = "borderline request";
        await using var host = WithFilter(o => o.ToolFilterMinScore = 0.30f);
        host.Embeddings!
            .OnAxis(query, Relevant)
            .AtSimilarity("cancel_order: Cancels an existing order", Relevant, 0.25f);
        host.Chat!.SayRepeatedly("ok", times: 2);

        await host.RunTurnAsync(query);

        host.FirstToolsSent.Should().NotContain("cancel_order");
    }

    [Fact]
    public async Task A_min_score_of_zero_keeps_everything_that_scores_at_all()
    {
        // The documented "disable the threshold" value. It must not mean "drop everything".
        const string query = "anything";
        await using var host = WithFilter(o => o.ToolFilterMinScore = 0f);
        host.Embeddings!.OnAxis(query, Relevant);
        foreach (var tool in new[] { "cancel_order: Cancels an existing order", "get_weather: Returns the weather for a city" })
            host.Embeddings.AtSimilarity(tool, Relevant, 0.05f);
        host.Chat!.SayRepeatedly("ok", times: 2);

        await host.RunTurnAsync(query);

        host.FirstToolsSent.Should().Contain("cancel_order");
    }

    // ── MinTools: the ranking has a say (BUG-099) ───────────────────────────────

    /// <summary>
    /// The shape found live on the published rc.11: asked how many units of a product were in stock,
    /// the only action above the line was <c>update_stock</c> (0.351); the actions that read stock
    /// ranked second to fourth at 0.29–0.32. Sent alone, the write action was called with a quantity
    /// of 0 and the stock was set to 0. Here <c>cancel_order</c> plays the write action and the two
    /// order readers sit just under the line.
    /// </summary>
    [Fact]
    public async Task A_lone_match_travels_with_the_next_best_ranked_actions()
    {
        const string query = "how many open orders are there";
        await using var host = WithFilter(o => { o.ToolFilterMinScore = 0.35f; o.ToolFilterMinTools = 3; });
        SeedOnlyRelevant(host, query);
        host.Embeddings!
            .AtSimilarity("cancel_order: Cancels an existing order", Relevant, 0.40f)
            .AtSimilarity("count_open_orders: Counts open orders", Relevant, 0.32f)
            .AtSimilarity("get_active_orders: Returns the list of active orders", Relevant, 0.30f)
            .AtSimilarity("get_weather: Returns the weather for a city", Relevant, 0.05f);
        host.Chat!.SayRepeatedly("ok", times: 2);

        await host.RunTurnAsync(query);

        output.WriteLine(string.Join(", ", host.FirstToolsSent));
        host.FirstToolsSent.Should().Contain(["cancel_order", "count_open_orders", "get_active_orders"],
            "the two readers ranked right behind the only action above the line");
        host.FirstToolsSent.Should().NotContain("get_weather", "the floor is three, and it ranked fourth");
    }

    [Fact]
    public async Task The_floor_does_not_widen_the_zero_match_fallback()
    {
        // ToolFilterMinTools applies once an action cleared the line; the zero-match fallback is three,
        // whatever the floor says (BUG-106).
        const string query = "ciao, come stai?";
        await using var host = WithFilter(o => { o.ToolFilterMinScore = 0.35f; o.ToolFilterMinTools = 5; });
        SeedOnlyRelevant(host, query);
        host.Embeddings!
            .AtSimilarity("count_open_orders: Counts open orders", Relevant, 0.30f)
            .AtSimilarity("get_active_orders: Returns the list of active orders", Relevant, 0.28f)
            .AtSimilarity("show_order_card: Shows an order as a card", Relevant, 0.26f)
            .AtSimilarity("mark_order_reviewed: Marks an order as reviewed", Relevant, 0.24f);
        host.Chat!.SayRepeatedly("ok", times: 2);

        await host.RunTurnAsync(query);

        host.FirstToolsSent.Where(t => ApplicationActions.Contains(t))
            .Should().BeEquivalentTo(["count_open_orders", "get_active_orders", "show_order_card"]);
    }

    // ── BUG-106: nothing clears the line ────────────────────────────────────────

    /// <summary>
    /// The shape measured live for "Quanto costa il Dell XPS 15?": the best-ranked action is a gated write
    /// just under the line (update_price 0.319, played by cancel_order), the reads that could answer are
    /// lower (0.209, 0.198, 0.174), and nothing clears 0.35.
    /// </summary>
    private static void SeedTheDellShape(MentorHostFixture host, string query)
    {
        SeedOnlyRelevant(host, query);
        host.Embeddings!
            .AtSimilarity("cancel_order: Cancels an existing order", Relevant, 0.319f)
            .AtSimilarity("count_open_orders: Counts open orders", Relevant, 0.209f)
            .AtSimilarity("get_active_orders: Returns the list of active orders", Relevant, 0.198f)
            .AtSimilarity("show_order_card: Shows an order as a card", Relevant, 0.174f)
            .AtSimilarity("get_weather: Returns the weather for a city", Relevant, 0.05f);
    }

    [Fact]
    public async Task With_nothing_above_the_line_the_best_ranked_actions_that_need_no_confirmation_travel()
    {
        const string query = "Quanto costa il Dell XPS 15?";
        await using var host = WithFilter(o => o.ToolFilterMinScore = 0.35f);
        SeedTheDellShape(host, query);
        host.Chat!.SayRepeatedly("ok", times: 2);

        await host.RunTurnAsync(query);

        output.WriteLine(string.Join(", ", host.FirstToolsSent));
        host.FirstToolsSent.Should().Contain(["count_open_orders", "get_active_orders", "show_order_card"],
            "the reads that could answer travel although none cleared the line — ranking, not a threshold");
        host.FirstToolsSent.Should().NotContain("cancel_order", "the best-ranked action needs confirmation");
        host.FirstToolsSent.Should().NotContain("get_weather", "the fallback is three");
    }

    [Fact]
    public async Task The_zero_match_fallback_never_sends_a_gated_action()
    {
        const string query = "Quanto costa il Dell XPS 15?";
        await using var host = WithFilter(o => o.ToolFilterMinScore = 0.35f);
        SeedOnlyRelevant(host, query);
        host.Embeddings!
            .AtSimilarity("cancel_order: Cancels an existing order", Relevant, 0.33f)
            .AtSimilarity("issue_refund: Issues a refund", Relevant, 0.32f)
            .AtSimilarity("export_ledger: Exports the order ledger", Relevant, 0.31f)
            .AtSimilarity("count_open_orders: Counts open orders", Relevant, 0.20f)
            .AtSimilarity("get_active_orders: Returns the list of active orders", Relevant, 0.19f)
            .AtSimilarity("show_order_card: Shows an order as a card", Relevant, 0.18f);
        host.Chat!.SayRepeatedly("ok", times: 2);

        await host.RunTurnAsync(query);

        host.FirstToolsSent.Should().Contain(["count_open_orders", "get_active_orders", "show_order_card"]);
        host.FirstToolsSent.Should().NotContain(["cancel_order", "issue_refund", "export_ledger"],
            "a confirmation-gated or role-gated action never travels on a zero-match turn (BUG-099's shape)");
    }

    [Fact]
    public async Task The_zero_match_fallback_respects_max_tools()
    {
        const string query = "Quanto costa il Dell XPS 15?";
        await using var host = WithFilter(o => { o.ToolFilterMinScore = 0.35f; o.ToolFilterMaxTools = 2; });
        SeedTheDellShape(host, query);
        host.Chat!.SayRepeatedly("ok", times: 2);

        await host.RunTurnAsync(query);

        host.FirstToolsSent.Where(t => ApplicationActions.Contains(t))
            .Should().BeEquivalentTo(["count_open_orders", "get_active_orders"]);
    }

    [Fact]
    public async Task The_zero_match_fallback_stays_declared_for_the_rest_of_the_turn()
    {
        const string query = "Quanto costa il Dell XPS 15?";
        await using var host = WithFilter(o => o.ToolFilterMinScore = 0.35f);
        SeedTheDellShape(host, query);
        host.Chat!.CallTool("count_open_orders").Say("Sono 2.");

        await host.RunTurnAsync(query);

        host.LastToolsSent.Should().Contain("count_open_orders",
            "the follow-up request carries the tool whose result it hands back");
    }

    [Fact]
    public async Task A_zero_match_turn_logs_the_best_score_and_what_was_sent_instead()
    {
        // The near miss (0.319) used to be invisible: only counts were logged.
        const string query = "Quanto costa il Dell XPS 15?";
        await using var host = WithFilter(o => o.ToolFilterMinScore = 0.35f);
        SeedTheDellShape(host, query);
        host.Chat!.SayRepeatedly("ok", times: 2);

        await host.RunTurnAsync(query);

        host.Logs.Records.Should().Contain(r =>
            r.Message.Contains("cancel_order 0.319") && r.Message.Contains("count_open_orders 0.209"));
    }

    [Fact]
    public async Task With_every_action_gated_a_zero_match_turn_sends_the_core_tools_only()
    {
        const string query = "Quanto costa il Dell XPS 15?";
        await using var host = WithFilter(o =>
        {
            o.ToolFilterMinScore = 0.35f;
            o.RequiresApproval = _ => true;
        });
        SeedTheDellShape(host, query);
        host.Chat!.SayRepeatedly("ok", times: 2);

        await host.RunTurnAsync(query);

        host.FirstToolsSent.Where(t => ApplicationActions.Contains(t)).Should().BeEmpty();
        host.Logs.Records.Should().Contain(r => r.Message.Contains("none needs no confirmation"));
    }

    [Fact]
    public async Task An_mcp_tool_joins_the_zero_match_fallback_only_when_its_server_marks_it_read_only()
    {
        // readOnlyHint is the server's own word; without it an MCP tool may write, and a write never
        // travels on a turn that matched nothing.
        const string query = "Quanto costa il Dell XPS 15?";
        var factory = new Novior.MentorAgent.Tests.Fakes.FakeMcpSessionFactory();
        factory.Servers["docs"] = (
            [Microsoft.Extensions.AI.AIFunctionFactory.Create(() => "ok", "docs_write", "Writes a document"),
             Microsoft.Extensions.AI.AIFunctionFactory.Create(() => "ok", "docs_read", "Reads a document")],
            ["docs_read"]);
        await using var host = MentorHostFixture.Create(o =>
        {
            o.EnableToolFiltering = true;
            o.ToolFilterMinScore = 0.35f;
            o.McpServers = [new Novior.MentorAgent.Models.MentorMcpServer { Name = "docs", ServerUrl = "http://127.0.0.1:59999/mcp" }];
        }, s => s.AddSingleton<Novior.MentorAgent.Core.Mcp.IMcpSessionFactory>(factory), withEmbeddings: true);
        SeedOnlyRelevant(host, query);
        host.Embeddings!
            .AtSimilarity("docs_write: Writes a document", Relevant, 0.30f)
            .AtSimilarity("docs_read: Reads a document", Relevant, 0.25f)
            .AtSimilarity("count_open_orders: Counts open orders", Relevant, 0.20f)
            .AtSimilarity("get_active_orders: Returns the list of active orders", Relevant, 0.18f);
        host.Chat!.SayRepeatedly("ok", times: 2);

        await host.RunTurnAsync(query);

        host.FirstToolsSent.Should().Contain(["docs_read", "count_open_orders", "get_active_orders"]);
        host.FirstToolsSent.Should().NotContain("docs_write");
    }

    [Fact]
    public async Task By_default_the_threshold_alone_decides()
    {
        // BUG-099's decision (23 Sep 2026): the floor is opt-in. With 3 as the default, a lone RIGHT
        // match ("Quanto ha speso Anna Ferrari?" -> search_customers) travelled with a by-email lookup
        // the model then picked: 0 answers of 4 against 4 of 4 with the threshold alone. The lone match
        // here needs no confirmation, like that search; a gated one is the next section's case.
        const string query = "show me order 1002";
        await using var host = WithFilter(o => o.ToolFilterMinScore = 0.35f);
        SeedOnlyRelevant(host, query);
        host.Embeddings!
            .AtSimilarity("show_order_card: Shows an order as a card", Relevant, 0.40f)
            .AtSimilarity("count_open_orders: Counts open orders", Relevant, 0.32f);
        host.Chat!.SayRepeatedly("ok", times: 2);

        await host.RunTurnAsync(query);

        host.FirstToolsSent.Should().Contain("show_order_card");
        host.FirstToolsSent.Should().NotContain("count_open_orders", "the floor is off unless the host sets it");
    }

    [Fact]
    public async Task A_floor_of_zero_is_the_threshold_alone()
    {
        const string query = "show me order 1002";
        await using var host = WithFilter(o => { o.ToolFilterMinScore = 0.35f; o.ToolFilterMinTools = 0; });
        SeedOnlyRelevant(host, query);
        host.Embeddings!
            .AtSimilarity("show_order_card: Shows an order as a card", Relevant, 0.40f)
            .AtSimilarity("count_open_orders: Counts open orders", Relevant, 0.32f);
        host.Chat!.SayRepeatedly("ok", times: 2);

        await host.RunTurnAsync(query);

        host.FirstToolsSent.Should().Contain("show_order_card");
        host.FirstToolsSent.Should().NotContain("count_open_orders");
    }

    [Fact]
    public async Task The_floor_never_exceeds_max_tools()
    {
        const string query = "how many open orders are there";
        await using var host = WithFilter(o =>
        {
            o.ToolFilterMinScore = 0.35f; o.ToolFilterMinTools = 5; o.ToolFilterMaxTools = 2;
        });
        SeedOnlyRelevant(host, query);
        host.Embeddings!
            .AtSimilarity("cancel_order: Cancels an existing order", Relevant, 0.40f)
            .AtSimilarity("count_open_orders: Counts open orders", Relevant, 0.32f)
            .AtSimilarity("get_active_orders: Returns the list of active orders", Relevant, 0.30f);
        host.Chat!.SayRepeatedly("ok", times: 2);

        await host.RunTurnAsync(query);

        host.FirstToolsSent.Where(t => ApplicationActions.Contains(t)).Should().HaveCount(2);
    }

    // ── A match that needs confirmation never travels alone (BUG-099) ───────────

    /// <summary>
    /// The structural answer to BUG-099. Confirmation is how an application marks an action that changes
    /// data, and the rc.12 notes told every host with filtering on to gate such actions. When every action
    /// above the line is gated, the best-ranked ungated ones go with it: asked how many units were in
    /// stock, the model had only update_stock and called it with 0. Here <c>cancel_order</c> is the gated
    /// write and <c>issue_refund</c> a second gated action ranked just under the line.
    /// </summary>
    [Fact]
    public async Task A_lone_gated_match_travels_with_the_best_ranked_ungated_actions()
    {
        const string query = "how many open orders are there";
        await using var host = WithFilter(o => o.ToolFilterMinScore = 0.35f);
        SeedOnlyRelevant(host, query);
        host.Embeddings!
            .AtSimilarity("cancel_order: Cancels an existing order", Relevant, 0.40f)
            .AtSimilarity("issue_refund: Issues a refund", Relevant, 0.33f)
            .AtSimilarity("count_open_orders: Counts open orders", Relevant, 0.32f)
            .AtSimilarity("get_active_orders: Returns the list of active orders", Relevant, 0.30f)
            .AtSimilarity("show_order_card: Shows an order as a card", Relevant, 0.28f)
            .AtSimilarity("get_weather: Returns the weather for a city", Relevant, 0.05f);
        host.Chat!.SayRepeatedly("ok", times: 2);

        await host.RunTurnAsync(query);

        output.WriteLine(string.Join(", ", host.FirstToolsSent));
        host.FirstToolsSent.Should().Contain(["cancel_order", "count_open_orders", "get_active_orders", "show_order_card"],
            "the three ungated actions ranked right behind the only match go with it");
        host.FirstToolsSent.Should().NotContain("issue_refund", "a second gated action is no help to a read question");
        host.FirstToolsSent.Should().NotContain("get_weather", "three companions travel, and it ranked fourth");
    }

    [Fact]
    public async Task When_an_ungated_action_also_cleared_the_line_nothing_is_added()
    {
        const string query = "how many open orders are there";
        await using var host = WithFilter(o => o.ToolFilterMinScore = 0.35f);
        SeedOnlyRelevant(host, query);
        host.Embeddings!
            .AtSimilarity("cancel_order: Cancels an existing order", Relevant, 0.40f)
            .AtSimilarity("count_open_orders: Counts open orders", Relevant, 0.36f)
            .AtSimilarity("get_active_orders: Returns the list of active orders", Relevant, 0.30f);
        host.Chat!.SayRepeatedly("ok", times: 2);

        await host.RunTurnAsync(query);

        host.FirstToolsSent.Should().Contain(["cancel_order", "count_open_orders"]);
        host.FirstToolsSent.Should().NotContain("get_active_orders", "the model already has a way to answer without writing");
    }

    [Fact]
    public async Task The_declaration_counts_even_with_RequireConfirmation_off()
    {
        // The switch hides the banner; it does not make the action stop changing data. With it off the
        // write runs unasked, which makes a lone gated match more dangerous, not less.
        const string query = "how many open orders are there";
        await using var host = WithFilter(o => { o.ToolFilterMinScore = 0.35f; o.RequireConfirmation = false; });
        SeedOnlyRelevant(host, query);
        host.Embeddings!
            .AtSimilarity("cancel_order: Cancels an existing order", Relevant, 0.40f)
            .AtSimilarity("count_open_orders: Counts open orders", Relevant, 0.32f);
        host.Chat!.SayRepeatedly("ok", times: 2);

        await host.RunTurnAsync(query);

        host.FirstToolsSent.Should().Contain(["cancel_order", "count_open_orders"]);
    }

    [Fact]
    public async Task The_RequiresApproval_predicate_marks_an_action_too()
    {
        const string query = "how many open orders are there";
        await using var host = WithFilter(o =>
        {
            o.ToolFilterMinScore = 0.35f;
            o.RequiresApproval = name => name == "mark_order_reviewed";
        });
        SeedOnlyRelevant(host, query);
        host.Embeddings!
            .AtSimilarity("mark_order_reviewed: Marks an order as reviewed", Relevant, 0.40f)
            .AtSimilarity("count_open_orders: Counts open orders", Relevant, 0.32f);
        host.Chat!.SayRepeatedly("ok", times: 2);

        await host.RunTurnAsync(query);

        host.FirstToolsSent.Should().Contain(["mark_order_reviewed", "count_open_orders"]);
    }

    [Fact]
    public async Task The_RequiresApproval_predicate_is_asked_again_on_every_turn()
    {
        // Documented as "evaluated once per tool while the coordinator is built". The filter asks it on
        // every turn that matches a tool, so a predicate that reads a file or a database pays for that
        // read on every message.
        const string query = "how many open orders are there";
        var asked = 0;
        await using var host = WithFilter(o =>
        {
            o.ToolFilterMinScore = 0.35f;
            // A read, so no declared confirmation answers first: only the predicate gates it.
            o.RequiresApproval = name =>
            {
                if (name != "count_open_orders") return false;
                Interlocked.Increment(ref asked);
                return true;
            };
        });
        SeedOnlyRelevant(host, query);
        host.Embeddings!.AtSimilarity("count_open_orders: Counts open orders", Relevant, 0.40f);
        host.Chat!.SayRepeatedly("ok", times: 4);

        await host.RunTurnAsync(query);   // builds the coordinator
        var afterFirstTurn = asked;
        await host.RunTurnAsync(query);

        asked.Should().BeGreaterThan(afterFirstTurn, "the filter asks the predicate again on the second turn");
    }

    [Fact]
    public async Task The_companions_never_push_the_list_past_max_tools()
    {
        const string query = "how many open orders are there";
        await using var host = WithFilter(o => { o.ToolFilterMinScore = 0.35f; o.ToolFilterMaxTools = 2; });
        SeedOnlyRelevant(host, query);
        host.Embeddings!
            .AtSimilarity("cancel_order: Cancels an existing order", Relevant, 0.40f)
            .AtSimilarity("count_open_orders: Counts open orders", Relevant, 0.32f)
            .AtSimilarity("get_active_orders: Returns the list of active orders", Relevant, 0.30f);
        host.Chat!.SayRepeatedly("ok", times: 2);

        await host.RunTurnAsync(query);

        host.FirstToolsSent.Where(t => ApplicationActions.Contains(t))
            .Should().BeEquivalentTo(["cancel_order", "count_open_orders"]);
    }

    [Fact]
    public async Task The_companions_stay_declared_for_the_rest_of_the_turn()
    {
        // The model calls a companion; its result comes back on a follow-up request with no user message.
        // Dropping the companion there would make the provider reject that result.
        const string query = "how many open orders are there";
        await using var host = WithFilter(o => o.ToolFilterMinScore = 0.35f);
        SeedOnlyRelevant(host, query);
        host.Embeddings!
            .AtSimilarity("cancel_order: Cancels an existing order", Relevant, 0.40f)
            .AtSimilarity("count_open_orders: Counts open orders", Relevant, 0.32f);
        host.Chat!.CallTool("count_open_orders").Say("There are 3 open orders.");

        await host.RunTurnAsync(query);

        host.LastToolsSent.Should().Contain("count_open_orders");
    }

    // ── MaxTools ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_tool_count_is_capped_at_max_tools()
    {
        // Everything is maximally relevant, so only the cap can limit the list.
        const string query = "do everything at once";
        await using var host = WithFilter(o =>
        {
            o.ToolFilterMaxTools = 2;
            o.ToolFilterMinScore = 0f;
        });
        host.Embeddings!.OnAxis(query, Relevant);
        host.Chat!.SayRepeatedly("ok", times: 2);

        // Every application action on the same axis as the query.
        foreach (var text in ToolTexts) host.Embeddings.OnAxis(text, Relevant);

        await host.RunTurnAsync(query);

        var applicationTools = host.FirstToolsSent.Where(t => ApplicationActions.Contains(t)).ToList();
        output.WriteLine(string.Join(", ", host.FirstToolsSent));
        applicationTools.Should().HaveCountLessThanOrEqualTo(2,
            "ToolFilterMaxTools caps the application actions, not the core tools");
    }

    [Fact]
    public async Task The_cap_keeps_the_highest_scoring_tools_not_an_arbitrary_two()
    {
        const string query = "cancel my order";
        await using var host = WithFilter(o =>
        {
            o.ToolFilterMaxTools = 1;
            o.ToolFilterMinScore = 0f;
        });
        host.Embeddings!
            .OnAxis(query, Relevant)
            .AtSimilarity("cancel_order: Cancels an existing order", Relevant, 0.95f)
            .AtSimilarity("get_weather: Returns the weather for a city", Relevant, 0.20f);
        host.Chat!.SayRepeatedly("ok", times: 2);

        await host.RunTurnAsync(query);

        host.FirstToolsSent.Should().Contain("cancel_order");
        host.FirstToolsSent.Should().NotContain("get_weather");
    }

    // ── Turn continuation ───────────────────────────────────────────────────────

    [Fact]
    public async Task The_selection_is_replayed_on_the_follow_up_request()
    {
        // Mid-turn the model is handed the result of a tool it just called. Re-filtering there
        // would drop that tool — the follow-up carries no user message to score against — and the
        // provider rejects a tool result for a tool that was not declared.
        const string query = "how many open orders are there";
        await using var host = WithFilter();
        SeedOnlyRelevant(host, query, "count_open_orders: Counts open orders");
        host.Chat!
            .CallTool("count_open_orders")
            .Say("There are 4 open orders.");

        await host.RunTurnAsync(query);

        host.Chat.Calls.Should().HaveCountGreaterThan(1, "the tool call produces a follow-up request");
        host.Chat.Calls[^1].ToolNames.Should().Contain("count_open_orders",
            "the tool whose result is being returned must still be declared");
    }

    [Fact]
    public async Task The_follow_up_does_not_re_embed_the_message()
    {
        const string query = "how many open orders are there";
        await using var host = WithFilter();
        SeedOnlyRelevant(host, query, "count_open_orders: Counts open orders");
        host.Chat!
            .CallTool("count_open_orders")
            .Say("There are 4 open orders.");

        await host.RunTurnAsync(query);

        // One query embedding for the turn. A second would mean the follow-up went through the
        // full scoring path, which is both a wasted call and the drop described above.
        host.Embeddings!.CallCount.Should().BeLessThanOrEqualTo(2,
            "the tool-embedding pass plus one query embedding is the whole budget for a turn");
    }

    // ── Fail-open ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_embedding_failure_sends_every_tool_and_warns()
    {
        // Fail-open, deliberately: a transient embedding outage must not silently strip
        // capabilities. The opposite choice would turn a rate limit into "the assistant forgot how
        // to do its job", with nothing in the log connecting the two.
        await using var host = WithFilter();
        host.Embeddings!.ThrowOnNextCall = new InvalidOperationException("429 Too Many Requests");
        host.Chat!.SayRepeatedly("ok", times: 2);

        await host.RunTurnAsync("cancel my order");

        host.FirstToolsSent.Should().Contain("cancel_order");
        host.Logs.HasWarning("failed to embed").Should().BeTrue(
            "warnings seen:{0}{1}", Environment.NewLine, host.Logs.WarningText);
    }

    [Fact]
    public async Task An_embedding_failure_emits_no_withheld_notice()
    {
        // Nothing was withheld, so telling the model a capability is gone would be a lie that
        // makes it refuse things it can actually do.
        await using var host = WithFilter();
        host.Embeddings!.ThrowOnNextCall = new InvalidOperationException("boom");
        host.Chat!.SayRepeatedly("ok", times: 2);

        await host.RunTurnAsync("cancel my order");

        (host.Chat.Calls[0].Instructions ?? "").Should().NotContain("Capability status for THIS request");
    }

    // ── Filtering off ───────────────────────────────────────────────────────────

    [Fact]
    public async Task With_filtering_off_every_tool_is_sent_even_with_a_generator_present()
    {
        await using var host = MentorHostFixture.Create(
            o => o.EnableToolFiltering = false, withEmbeddings: true);
        host.Chat!.SayRepeatedly("ok", times: 2);

        await host.RunTurnAsync("something unrelated");

        host.FirstToolsSent.Should().Contain("cancel_order").And.Contain("get_weather");
    }

    [Fact]
    public async Task With_filtering_off_the_embedder_is_not_called_for_tools()
    {
        // The generator may be configured for RAG or memory. Filtering being off must mean it is
        // not billed for tool scoring on every turn.
        await using var host = MentorHostFixture.Create(
            o => o.EnableToolFiltering = false, withEmbeddings: true);
        host.Chat!.SayRepeatedly("ok", times: 2);

        await host.RunTurnAsync("something unrelated");

        host.Embeddings!.CallCount.Should().Be(0);
    }

    private static readonly string[] ApplicationActions =
    [
        "cancel_order", "issue_refund", "export_ledger", "count_open_orders",
        "show_order_card", "failing_action", "get_weather", "get_active_orders",
        "mark_order_reviewed",
    ];

    private static readonly string[] ToolTexts =
    [
        "cancel_order: Cancels an existing order",
        "issue_refund: Issues a refund",
        "export_ledger: Exports the order ledger",
        "count_open_orders: Counts open orders",
        "show_order_card: Shows an order as a card",
        "get_weather: Returns the weather for a city",
        "get_active_orders: Returns the list of active orders",
        "failing_action: Always fails, for exception mapping",
        "mark_order_reviewed: Marks an order as reviewed",
        // The skill class's methods are filter candidates too; unseeded, their hash-derived vectors
        // outranked seeded readers in the rank-floor tests.
        "get_policy_for_category: Returns the reimbursement policy for a category",
        "submit_expense_report: Submits a new expense report",
    ];

    /// <summary>The skill class's actions — filter candidates like the application's own.</summary>
    private static readonly string[] SkillActions = ["get_policy_for_category", "submit_expense_report"];

    [Fact]
    public async Task A_name_two_mcp_servers_share_stays_out_of_the_zero_match_fallback_unless_both_mark_it_read_only()
    {
        // Review of the rc.14 fix: eligibility is decided by name, so the read-only copy used to carry an
        // unmarked — possibly writing — copy onto a turn that matched nothing.
        const string query = "Quanto costa il Dell XPS 15?";
        var factory = new Novior.MentorAgent.Tests.Fakes.FakeMcpSessionFactory();
        factory.Servers["docs"] = ([Microsoft.Extensions.AI.AIFunctionFactory.Create(() => "ok", "search", "Searches")], ["search"]);
        factory.Servers["files"] = ([Microsoft.Extensions.AI.AIFunctionFactory.Create(() => "ok", "search", "Searches")], []);
        await using var host = MentorHostFixture.Create(o =>
        {
            o.EnableToolFiltering = true;
            o.ToolFilterMinScore = 0.35f;
            o.McpServers =
            [
                new Novior.MentorAgent.Models.MentorMcpServer { Name = "docs", ServerUrl = "http://127.0.0.1:59999/docs" },
                new Novior.MentorAgent.Models.MentorMcpServer { Name = "files", ServerUrl = "http://127.0.0.1:59999/files" },
            ];
        }, s => s.AddSingleton<Novior.MentorAgent.Core.Mcp.IMcpSessionFactory>(factory), withEmbeddings: true);
        SeedOnlyRelevant(host, query);
        host.Embeddings!
            .AtSimilarity("search: Searches", Relevant, 0.30f)
            .AtSimilarity("count_open_orders: Counts open orders", Relevant, 0.20f);
        host.Chat!.SayRepeatedly("ok", times: 2);

        await host.RunTurnAsync(query);

        host.FirstToolsSent.Should().NotContain("search");
        host.FirstToolsSent.Should().Contain("count_open_orders");
    }
}
