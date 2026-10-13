using FluentAssertions;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Rag;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Novior.MentorAgent.Tests.Levels;

/// <summary>
/// The core README's <b>Path A vs Path B feature matrix</b>, row by row, asserted on both paths.
///
/// <para>
/// That table is the most load-bearing piece of documentation in the package: it is how a developer
/// decides whether to hand MentorAgent a <c>ChatClient</c> or a pre-built <c>AIAgent</c>, and every
/// ✅ in the Path B column is a promise that a feature survives that choice. Until now nothing
/// checked any of it, and two of the ticks were false — the whole tool list and the whole per-turn
/// context were built and discarded on Path B (BUG-013, BUG-015).
/// </para>
///
/// <para>
/// The ❌ rows matter just as much. A capability that is documented as unavailable but silently
/// half-works is a worse outcome than one that is honestly absent, because it will be relied on.
/// The degradation warnings for those live in <c>PathBConstraintTests</c>; what is asserted here is
/// the observable consequence — what the model is and is not given.
/// </para>
/// </summary>
public sealed class PathParityTests
{
    /// <summary>Path B: a pre-built agent, with a handle on the client behind it.</summary>
    private static (MentorHostFixture Host, FakeChatClient Inner) PathB(
        Action<MentorOptions>? configure = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var inner = new FakeChatClient();
        var host = MentorHostFixture.Create(
            o =>
            {
                o.ChatClient = null;
                o.Agent = inner.AsAIAgent(instructions: "You are a test agent.");
                configure?.Invoke(o);
            },
            configureServices,
            withChatClient: false);

        return (host, inner);
    }

    private static string ToolsOf(FakeChatClient chat)
        => string.Join(",", chat.Calls.SelectMany(c => c.ToolNames).Distinct());

    private static string PromptOf(FakeChatClient chat)
        => string.Join("\n", chat.Calls.Select(c => (c.Instructions ?? "") + c.SystemText));

    // ── Row 1 · L1 actions — ✅ on both ─────────────────────────────────────────

    [Fact]
    public async Task L1_actions_reach_the_model_on_path_A()
    {
        await using var host = MentorHostFixture.Create();

        await host.RunTurnAsync("hello");

        ToolsOf(host.Chat!).Should().Contain("count_open_orders").And.Contain("cancel_order");
    }

    [Fact]
    public async Task L1_actions_reach_the_model_on_path_B()
    {
        var (host, inner) = PathB();
        await using var _ = host;

        await host.RunTurnAsync("hello");

        ToolsOf(inner).Should().Contain("count_open_orders").And.Contain("cancel_order");
    }

    // ── Rows 2–4 · L2 handoff, L3 teams, A2A — ✅ on A, ❌ on B ──────────────────

    [Fact]
    public async Task Level_2_handoff_is_offered_on_path_A()
    {
        await using var host = MentorHostFixture.Create();

        await host.RunTurnAsync("where is my parcel");

        ToolsOf(host.Chat!).Should().Contain("route_to_specialist");
    }

    [Fact]
    public async Task Level_2_handoff_is_absent_on_path_B()
    {
        // Not a degradation to be worked around: a handoff workflow needs a chat client MentorAgent
        // can compose specialists from, and a pre-built agent does not offer one. The matrix says
        // ❌ and the tool list must agree — offering a route_to_specialist that cannot route is
        // worse than not offering it.
        var (host, inner) = PathB();
        await using var _ = host;

        await host.RunTurnAsync("where is my parcel");

        ToolsOf(inner).Should().NotContain("route_to_specialist");
    }

    [Fact]
    public async Task Level_3_teams_are_offered_on_path_A()
    {
        await using var host = MentorHostFixture.Create();

        await host.RunTurnAsync("analyse last quarter");

        ToolsOf(host.Chat!).Should().Contain("business_analysis_team");
    }

    [Fact]
    public async Task Level_3_teams_are_absent_on_path_B()
    {
        var (host, inner) = PathB();
        await using var _ = host;

        await host.RunTurnAsync("analyse last quarter");

        ToolsOf(inner).Should().NotContain("business_analysis_team");
    }

    // ── Row 7 · Agent Skills — ✅ on both ───────────────────────────────────────

    // EnableSkills is off by default, so the skill tools only exist once it is on. A [MentorSkill]
    // class whose methods became L1 tools is *not* the same thing as the skill being loadable — the
    // fixture has both, which is why the distinction is worth stating.
    //
    // Only load_skill is asserted: read_skill_resource is registered exclusively when at least one
    // skill has side-car files, and the fixture's skills are class-based with none. That is the
    // right call — a tool whose every invocation could only return "no resources" is worse than an
    // absent one — and it belongs to B16 rather than to a parity assertion.

    [Fact]
    public async Task Skill_tools_reach_the_model_on_path_A()
    {
        await using var host = MentorHostFixture.Create(o => o.EnableSkills = true);

        await host.RunTurnAsync("how do I file an expense");

        ToolsOf(host.Chat!).Should().Contain("load_skill");
    }

    [Fact]
    public async Task Skill_tools_reach_the_model_on_path_B()
    {
        var (host, inner) = PathB(o => o.EnableSkills = true);
        await using var _ = host;

        await host.RunTurnAsync("how do I file an expense");

        ToolsOf(inner).Should().Contain("load_skill");
    }

    [Fact]
    public async Task Skills_that_are_switched_off_register_no_skill_tools_on_either_path()
    {
        await using var pathA = MentorHostFixture.Create();
        var (host, inner) = PathB();
        await using var _ = host;

        await pathA.RunTurnAsync("how do I file an expense");
        await host.RunTurnAsync("how do I file an expense");

        ToolsOf(pathA.Chat!).Should().NotContain("load_skill");
        ToolsOf(inner).Should().NotContain("load_skill");
    }

    // ── Row 8 · UI actions — per-action tools on A, one dispatcher on B ─────────

    [Fact]
    public async Task Path_A_does_not_add_the_generic_dispatcher()
    {
        // On Path A each registered action becomes its own named tool with its own schema, injected
        // per call by UIActionsMiddleware. Adding the dispatcher as well would offer the model two
        // ways to do the same thing, and the generic one hallucinates action names.
        await using var host = MentorHostFixture.Create();

        await host.RunTurnAsync("highlight row 5");

        ToolsOf(host.Chat!).Should().NotContain("invoke_ui_action");
    }

    [Fact]
    public async Task Path_B_gets_the_generic_dispatcher()
    {
        // There is no pipeline to inject per-action tools into, so the legacy dispatcher is the
        // only way a pre-built agent can reach a UI action at all.
        var (host, inner) = PathB();
        await using var _ = host;

        await host.RunTurnAsync("highlight row 5");

        ToolsOf(inner).Should().Contain("invoke_ui_action");
    }

    // ── Navigation and memory — ✅ on both ──────────────────────────────────────

    [Fact]
    public async Task Navigation_is_offered_on_both_paths()
    {
        await using var pathA = MentorHostFixture.Create();
        var (host, inner) = PathB();
        await using var _ = host;

        await pathA.RunTurnAsync("take me to orders");
        await host.RunTurnAsync("take me to orders");

        ToolsOf(pathA.Chat!).Should().Contain("navigate_to");
        ToolsOf(inner).Should().Contain("navigate_to");
    }

    // ── Row 6 · RAG — ✅ on both ────────────────────────────────────────────────

    [Fact]
    public async Task Retrieved_documents_reach_the_model_on_both_paths()
    {
        var sourceA = new ScriptedRagSource().Returns("Bay 4 is closed.", 9f, "Warehouse");
        var sourceB = new ScriptedRagSource().Returns("Bay 4 is closed.", 9f, "Warehouse");

        await using var pathA = MentorHostFixture.Create(
            o => o.UseRag = true,
            services => services.AddSingleton<IMentorRagSource>(sourceA));

        var (host, inner) = PathB(
            o => o.UseRag = true,
            services => services.AddSingleton<IMentorRagSource>(sourceB));
        await using var _ = host;

        await pathA.RunTurnAsync("what about the warehouse");
        await host.RunTurnAsync("what about the warehouse");

        PromptOf(pathA.Chat!).Should().Contain("Bay 4 is closed.");
        PromptOf(inner).Should().Contain("Bay 4 is closed.");
    }

    // ── Page context — the per-turn block, on both paths ────────────────────────

    [Fact]
    public async Task The_current_page_reaches_the_model_on_both_paths()
    {
        await using var pathA = MentorHostFixture.Create();
        pathA.Navigation.StartAt("/orders");

        var (host, inner) = PathB();
        await using var _ = host;
        host.Navigation.StartAt("/orders");

        await pathA.RunTurnAsync("what can I do here");
        await host.RunTurnAsync("what can I do here");

        // Asserted on the label, not the value: "Orders" is legitimately in the static page
        // catalogue too, so finding it proves nothing about the per-turn block.
        PromptOf(pathA.Chat!).Should().Contain("Current URL");
        PromptOf(inner).Should().Contain("Current URL");
    }

    // ── Row 9 · Safety check — ✅ on A, ❌ on B ─────────────────────────────────

    [Fact]
    public async Task The_safety_check_runs_on_path_A()
    {
        // It is an auxiliary model call MentorAgent makes on its own, so it needs a chat client of
        // its own. On Path A it is available; the Path B counterpart is asserted below.
        await using var host = MentorHostFixture.Create(o => o.EnableSafetyCheck = true);

        await host.RunTurnAsync("hello");

        host.Chat!.Calls.Count.Should().BeGreaterThan(1,
            "the safety check is a separate call before the turn");
    }

    [Fact]
    public async Task The_safety_check_is_skipped_on_path_B()
    {
        // The security-sensitive half: a developer who enables the check and receives nothing has a
        // false sense of protection. The warning is asserted in PathBConstraintTests; what matters
        // here is that no phantom check silently half-runs.
        var (host, inner) = PathB(o => o.EnableSafetyCheck = true);
        await using var _ = host;

        await host.RunTurnAsync("hello");

        var everySystemMessage = string.Join("\n", inner.Calls.Select(c => c.SystemText));
        everySystemMessage.ToLowerInvariant().Should().NotContain("safety");
    }

    // ── Row 11 · ChatHistoryProvider — via options on A, in the agent on B ──────

    [Fact]
    public async Task A_history_provider_configured_through_options_never_reaches_a_pre_built_agent()
    {
        // The documented caveat under the matrix: "ChatHistoryProvider cannot be added after
        // construction — configure it directly in the agent you pass to options.Agent". The agent
        // already exists by the time MentorAgent sees the option, and AF exposes the provider it
        // is actually using, so the claim is checkable rather than merely stated. Pinning it stops
        // a future change from appearing to honour the option while quietly doing nothing.
        var inner = new FakeChatClient();
        var agent = (ChatClientAgent)inner.AsAIAgent(instructions: "You are a test agent.");
        var mine = new InMemoryChatHistoryProvider();

        await using var host = MentorHostFixture.Create(o =>
        {
            o.ChatClient = null;
            o.Agent = agent;
            o.ChatHistoryProvider = mine;
        }, withChatClient: false);

        await host.RunTurnAsync("hello");

        // Not "is null": AF gives a ChatClientAgent its own in-memory provider by default, so the
        // question is whose provider is in place, not whether one is.
        agent.ChatHistoryProvider.Should().NotBeSameAs(mine,
            "the option is a Path A seam — on Path B the agent's own configuration is the only one");
    }

    // ── The host's own agent is never overwritten ───────────────────────────────

    [Fact]
    public async Task Everything_MentorAgent_adds_on_path_B_is_additive()
    {
        // The single property that makes the Path B fix safe. MentorAgent contributes tools and
        // per-turn context; the agent's own instructions and its own tools are the host's and must
        // both survive intact.
        var inner = new FakeChatClient();
        var ownTool = AIFunctionFactory.Create(() => "42", name: "host_own_tool");

        await using var host = MentorHostFixture.Create(o =>
        {
            o.ChatClient = null;
            o.Agent = inner.AsAIAgent(new ChatClientAgentOptions
            {
                Name = "host_agent",
                ChatOptions = new ChatOptions
                {
                    Instructions = "You are the host's own assistant.",
                    Tools = [ownTool],
                },
            });
        }, withChatClient: false);

        await host.RunTurnAsync("hello");

        ToolsOf(inner).Should().Contain("host_own_tool").And.Contain("count_open_orders");
        PromptOf(inner).Should().Contain("You are the host's own assistant.");
    }
}
