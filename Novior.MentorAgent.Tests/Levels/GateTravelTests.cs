using System.Reflection;
using FluentAssertions;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Middleware;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.App;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.AI;
using Xunit;

namespace Novior.MentorAgent.Tests.Levels;

/// <summary>
/// Whether MentorAgent's tool gate reaches the tools that run <em>inside</em> a workflow.
///
/// <para>
/// The coordinator's tools are gated by middleware on the agent. Level-2 specialists and Level-3
/// team members are not the coordinator: they are built as their own <c>ChatClientAgent</c> and
/// handed to <c>AgentWorkflowBuilder</c>, which runs their function-calling loop itself. There is no
/// seam there for agent middleware, so the gate is attached to the <see cref="AIFunction"/> instead
/// and travels with the tool. Before that,
/// <c>[MentorAction(RequiresConfirmation = true, RequiredRoles = [...])]</c> on a specialist method
/// was silently ignored — the action ran with no role check and no banner.
/// </para>
///
/// <para>
/// That makes this a security boundary rather than a UX one, and it is the kind that fails quietly:
/// the tool works, the answer is correct, and the only thing missing is the check. Nothing in the
/// output distinguishes a gated call from an ungated one.
/// </para>
/// </summary>
public sealed class GateTravelTests
{
    /// <summary>Gate, host and the service whose methods the gated functions call.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private Rig(MentorHostFixture host) => Host = host;

        internal MentorHostFixture Host { get; }
        internal MentorAgentUIMiddleware Gate => Host.Get<MentorAgentUIMiddleware>();
        internal TestOrderService Orders => Host.Get<TestOrderService>();

        internal static Rig Create(Action<MentorOptions>? configure = null)
            => new(MentorHostFixture.Create(configure));

        /// <summary>A gated copy of one of the order service's methods, by tool name.</summary>
        internal AIFunction Tool(string methodName, string toolName)
        {
            var fn = AIFunctionFactory.Create(
                method: typeof(TestOrderService).GetMethod(methodName)!,
                target: Orders,
                name: toolName,
                description: toolName);

            return (AIFunction)GatedAIFunction.Wrap(fn, Gate);
        }

        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }

    private static AIFunctionArguments Args(params (string Key, object? Value)[] pairs)
        => new(pairs.ToDictionary(p => p.Key, p => p.Value));

    // ── The gate itself ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_role_gated_tool_refuses_an_anonymous_caller_and_never_runs()
    {
        // Fail closed. The refusal must happen before the method body, not after — a check that
        // reports afterwards has already had the side effect.
        await using var rig = Rig.Create();

        var result = await rig.Tool(nameof(TestOrderService.ExportLedger), "export_ledger")
                              .InvokeAsync(Args(), CancellationToken.None);

        rig.Orders.Invoked.Should().BeEmpty("the method must not have been reached");
        (result?.ToString() ?? "").ToLowerInvariant().Should().Contain("error");
    }

    [Fact]
    public async Task A_role_gated_tool_runs_for_a_caller_who_holds_the_role()
    {
        await using var rig = Rig.Create();
        rig.Host.Auth.SignIn("u-1", "Admin");

        await rig.Tool(nameof(TestOrderService.ExportLedger), "export_ledger")
                 .InvokeAsync(Args(), CancellationToken.None);

        rig.Orders.Invoked.Should().Contain(nameof(TestOrderService.ExportLedger));
    }

    [Fact]
    public async Task Holding_a_different_role_is_not_enough()
    {
        await using var rig = Rig.Create();
        rig.Host.Auth.SignIn("u-1", "Viewer");

        await rig.Tool(nameof(TestOrderService.ExportLedger), "export_ledger")
                 .InvokeAsync(Args(), CancellationToken.None);

        rig.Orders.Invoked.Should().BeEmpty();
    }

    [Fact]
    public async Task An_ungated_neighbour_is_unaffected()
    {
        // The gate is per tool name. A wrapper that consulted the wrong descriptor would either
        // block everything or nothing, and both look plausible in a single-tool test.
        await using var rig = Rig.Create();

        await rig.Tool(nameof(TestOrderService.CountOpenOrders), "count_open_orders")
                 .InvokeAsync(Args(), CancellationToken.None);

        rig.Orders.Invoked.Should().Contain(nameof(TestOrderService.CountOpenOrders));
    }

    [Fact]
    public async Task A_confirmation_gated_tool_asks_even_in_native_hitl_mode()
    {
        // The reason GatedAIFunction passes alwaysBlockForApproval: a nested agent's function loop
        // is internal to the workflow, so an Agent Framework ToolApprovalRequestContent raised
        // there never surfaces to the orchestrator. Blocking is the only flow that can reach the
        // user from inside, and Native mode must not silently skip the question.
        await using var rig = Rig.Create(o => o.HitlMode = MentorHitlMode.Native);
        var state = rig.Host.Get<Novior.MentorAgent.Abstractions.Interfaces.IMentorStateService>();

        ConfirmationRequestSeen? seen = null;
        state.OnConfirmationRequired += req =>
        {
            seen = new ConfirmationRequestSeen(req.ActionId, req.ToolName);
            _ = state.ConfirmAsync(req.ActionId);
        };

        await rig.Tool(nameof(TestOrderService.IssueRefundAsync), "issue_refund")
                 .InvokeAsync(Args(("orderId", 7)), CancellationToken.None);

        seen.Should().NotBeNull("a nested gated tool must still ask");
        seen!.ToolName.Should().Be("issue_refund");
        rig.Orders.Invoked.Should().Contain(nameof(TestOrderService.IssueRefundAsync));
    }

    private sealed record ConfirmationRequestSeen(Guid ActionId, string ToolName);

    [Fact]
    public async Task A_denied_confirmation_stops_a_nested_tool()
    {
        await using var rig = Rig.Create();
        var state = rig.Host.Get<Novior.MentorAgent.Abstractions.Interfaces.IMentorStateService>();
        state.OnConfirmationRequired += req => state.Cancel(req.ActionId);

        var result = await rig.Tool(nameof(TestOrderService.IssueRefundAsync), "issue_refund")
                              .InvokeAsync(Args(("orderId", 7)), CancellationToken.None);

        rig.Orders.Invoked.Should().BeEmpty();

        // Was `Contain("error")`. A refusal is no longer reported as one — it is marked as the
        // user's decision, because the two used to be indistinguishable and the coordinator prompt
        // gives them opposite instructions (BUG-031). The property this test owns is unchanged: the
        // gate travelled into the nested tool and the refusal came back through it.
        (result?.ToString() ?? "").Should().Contain("declined_by_user");
    }

    [Fact]
    public async Task The_role_gate_runs_before_the_confirmation_gate()
    {
        // Established in B19 for the coordinator; it must hold here too. Asking somebody to approve
        // something they are not allowed to do leaks the existence of the action and produces a
        // dialog that can only end in a refusal.
        await using var rig = Rig.Create();
        var state = rig.Host.Get<Novior.MentorAgent.Abstractions.Interfaces.IMentorStateService>();
        var asked = false;
        state.OnConfirmationRequired += _ => asked = true;

        // cancel_order carries RequiredRoles AND RequiresConfirmation, and the caller is anonymous.
        await rig.Tool(nameof(TestOrderService.CancelOrderAsync), "cancel_order")
                 .InvokeAsync(Args(("orderId", 7), ("reason", "test")), CancellationToken.None);

        asked.Should().BeFalse("a caller without the role is refused outright");
        rig.Orders.Invoked.Should().BeEmpty();
    }

    // ── Wrapping ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Wrapping_is_idempotent()
    {
        // Both builders wrap, and the coordinator's Native-HITL marker pass runs over the same
        // list. A second wrap would double every gate: two confirmations for one call.
        await using var rig = Rig.Create();
        var once = rig.Tool(nameof(TestOrderService.CountOpenOrders), "count_open_orders");

        GatedAIFunction.Wrap(once, rig.Gate).Should().BeSameAs(once);
    }

    [Fact]
    public async Task A_declaration_that_cannot_be_invoked_is_left_alone()
    {
        // Hosted tools are declarations the provider executes; there is nothing local to gate, and
        // casting one to AIFunction would throw at startup.
        await using var rig = Rig.Create();
        var hosted = new HostedWebSearchTool();

        GatedAIFunction.Wrap(hosted, rig.Gate).Should().BeSameAs(hosted);
    }

    [Fact]
    public async Task The_wrapper_keeps_the_tools_name_and_schema()
    {
        // The model is shown the wrapper. A wrapper that renamed the function, or lost its
        // parameter schema, would make the tool uncallable — and DelegatingAIFunction only
        // forwards what it is told to.
        await using var rig = Rig.Create();
        var inner = AIFunctionFactory.Create(
            method: typeof(TestOrderService).GetMethod(nameof(TestOrderService.CancelOrderAsync))!,
            target: rig.Orders,
            name: "cancel_order",
            description: "Cancels an existing order");

        var gated = (AIFunction)GatedAIFunction.Wrap(inner, rig.Gate);

        gated.Name.Should().Be(inner.Name);
        gated.Description.Should().Be(inner.Description);
        gated.JsonSchema.ToString().Should().Be(inner.JsonSchema.ToString());
    }

    // ── The builders actually wrap ──────────────────────────────────────────────

    [Fact]
    public void Every_tool_built_for_a_level_2_specialist_carries_the_gate()
    {
        // Structural on purpose. Driving a specialist's tool call end to end means scripting the
        // Agent Framework's own handoff protocol, which would make this test about AF's workflow
        // machinery rather than about whether MentorAgent wrapped its functions. The behaviour of
        // the wrapper is pinned above; what is left to prove is that every tool gets one.
        // MentorHostFixture is async-disposable only (the orchestrator is).
        var host = MentorHostFixture.Create();
        var discovery = host.Get<MentorDiscoveryService>().Discover();
        var builder = host.Get<HandoffBuilder>();

        var specialist = discovery.Level2Agents.Single();
        var tools = InvokePrivate<List<AITool>>(builder, "BuildToolsForAgent", specialist);

        tools.Should().NotBeEmpty();
        string.Join(", ", tools.Where(t => t is not GatedAIFunction).Select(t => t.Name))
              .Should().BeEmpty("every specialist tool must carry the gate");
    }

    [Fact]
    public void Every_tool_built_for_a_level_3_team_member_carries_the_gate()
    {
        // MentorHostFixture is async-disposable only (the orchestrator is).
        var host = MentorHostFixture.Create();
        var discovery = host.Get<MentorDiscoveryService>().Discover();
        var builder = host.Get<GroupChatBuilder>();

        var members = discovery.Level3Teams.Single().Members.Where(m => m.Tools.Count > 0).ToList();
        members.Should().NotBeEmpty("the fixture team must have at least one member with tools");

        foreach (var member in members)
        {
            var tools = InvokePrivate<List<AITool>>(builder, "BuildMemberTools", member);
            string.Join(", ", tools.Where(t => t is not GatedAIFunction).Select(t => t.Name))
                  .Should().BeEmpty("team member '{0}' has an ungated tool", member.Attribute.Role);
        }
    }

    /// <summary>
    /// Calls a private builder method. The coupling is deliberate and narrow: these two methods are
    /// where the wrap happens, and a rename should fail loudly here rather than quietly remove a
    /// security check.
    /// </summary>
    private static T InvokePrivate<T>(object target, string methodName, params object[] args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull(
            "{0}.{1} is where the gate is attached — if it was renamed, update this test rather " +
            "than deleting it", target.GetType().Name, methodName);

        return (T)method!.Invoke(target, args)!;
    }
}
