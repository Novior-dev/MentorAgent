using FluentAssertions;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Novior.MentorAgent.Tests.Federation;

/// <summary>
/// The second way a host can protect an action — and whether the MCP server honours it.
///
/// <para>
/// BUG-021 established the rule: an action the author gated is <b>not published</b> over MCP, because
/// an MCP client is a separate process with no signed-in principal to check <c>RequiredRoles</c>
/// against and nobody watching to answer a confirmation dialog. That fix reads the
/// <c>[MentorAction]</c> attribute.
/// </para>
///
/// <para>
/// But the attribute is not the only gate. <see cref="MentorOptions.RequiresApproval"/> is a
/// documented predicate that gates <b>by tool name</b>, and its whole reason to exist is covering
/// what you did not annotate — tools that come from a Level-2 agent, from an MCP client, or from a
/// class you do not own. The headless sample uses it exactly that way, and says so in a comment:
/// <em>"Nessuno dei due ha [MentorAction(RequiresConfirmation)], ed è esattamente il caso d'uso del
/// predicato: gatare per nome ciò che non hai annotato."</em>
/// </para>
///
/// <para>
/// Found in Part D by listing the tools of the running headless server over a real MCP session:
/// <c>update_stock</c> and <c>update_order_status</c> were both published, and both are gated by that
/// predicate. Same gap as BUG-021, reached through the other gate (BUG-035).
/// </para>
/// </summary>
public sealed class McpPredicateGateTests
{
    private static (MentorAgentMcpToolCollection Collection, MentorHostFixture Host) Build(
        Func<string, bool>? requiresApproval)
    {
        var host = MentorHostFixture.Create(o =>
        {
            o.McpServerEnabled = true;
            o.RequiresApproval = requiresApproval;
        });

        var collection = new MentorAgentMcpToolCollection(
            host.Get<MentorDiscoveryService>(),
            host.Scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>(),
            new TypedLogger<MentorAgentMcpToolCollection>(
                new TestLoggerProvider(host.Logs).CreateLogger("mcp-server")),
            host.Scope.ServiceProvider.GetRequiredService<
                Microsoft.Extensions.Options.IOptions<Novior.MentorAgent.Options.MentorOptions>>());

        return (collection, host);
    }

    private static IReadOnlyList<string> ToolNames(MentorAgentMcpToolCollection c)
        => c.GetTools().Select(t => t.ProtocolTool.Name).ToList();

    // ── The defect ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_action_gated_by_the_approval_predicate_is_not_published()
    {
        // `count_open_orders` carries no gate in its attribute, so BUG-021's check waves it through.
        // The host has nonetheless said it needs approval — and an MCP caller can no more answer a
        // confirmation than they could for an annotated action.
        var (collection, host) = Build(tool => tool == "count_open_orders");
        await using var _ = host;

        ToolNames(collection).Should().NotContain("count_open_orders",
            "the host gated it; how they gated it is not the remote caller's business");

        collection.Dispose();
    }

    [Fact]
    public async Task The_withheld_action_is_named_in_the_log()
    {
        // BUG-021's fix logs every withheld tool with its reason, because a silently missing tool is
        // indistinguishable from one that failed to build. A predicate-gated tool must appear there
        // too, or the operator sees a shorter list and no explanation.
        var (collection, host) = Build(tool => tool == "count_open_orders");
        await using var _ = host;

        ToolNames(collection).Should().NotContain("count_open_orders");

        var log = string.Join(" | ", host.Logs.Records.Select(r => r.Message));
        log.Should().Contain("count_open_orders");
        log.Should().Contain("withheld");

        collection.Dispose();
    }

    // ── What must not change ────────────────────────────────────────────────────

    [Fact]
    public async Task Without_a_predicate_the_ungated_actions_are_still_published()
    {
        // The control. A guard that withheld everything would pass the test above and destroy the
        // feature: the point of the MCP server is that a remote agent *can* drive the application.
        var (collection, host) = Build(requiresApproval: null);
        await using var _ = host;

        ToolNames(collection).Should().Contain("count_open_orders");

        collection.Dispose();
    }

    [Fact]
    public async Task A_predicate_that_gates_nothing_withholds_nothing()
    {
        // A host may register a predicate that returns false for everything — a feature flag turned
        // off, say. That must behave exactly like no predicate at all.
        var (collection, host) = Build(_ => false);
        await using var _ = host;

        ToolNames(collection).Should().Contain("count_open_orders");

        collection.Dispose();
    }

    [Fact]
    public async Task The_attribute_gate_still_withholds_what_it_always_did()
    {
        // BUG-021 must not regress while its neighbour is being fixed.
        var (collection, host) = Build(_ => false);
        await using var _ = host;

        var names = ToolNames(collection);
        names.Should().NotContain("cancel_order");
        names.Should().NotContain("issue_refund");
        names.Should().NotContain("export_ledger");

        collection.Dispose();
    }
}
