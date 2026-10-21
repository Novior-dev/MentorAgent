using FluentAssertions;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Tests.App;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Novior.MentorAgent.Tests.Federation;

/// <summary>
/// The MCP surface MentorAgent <b>publishes</b> — its own <c>[MentorAction]</c> methods, exposed to
/// any MCP client that can reach the endpoint.
///
/// <para>
/// This is the only place in the library where a tool is invoked by a caller who is not the
/// application's own user. Everything else — the coordinator, a Level-2 specialist, a Level-3 team
/// member — runs a tool on behalf of somebody who is signed in to the host. An MCP client is a
/// separate process reaching in over HTTP, so whatever protection an action carries has to be either
/// enforced here or deliberately, visibly withdrawn.
/// </para>
/// </summary>
public sealed class McpServerSurfaceTests
{
    private static (MentorAgentMcpToolCollection Collection, MentorHostFixture Host) Build()
    {
        var host = MentorHostFixture.Create(o => o.McpServerEnabled = true);

        var collection = new MentorAgentMcpToolCollection(
            host.Get<MentorDiscoveryService>(),
            host.Scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>(),
            new TypedLogger<MentorAgentMcpToolCollection>(
                new TestLoggerProvider(host.Logs).CreateLogger("mcp-server")),
            host.Scope.ServiceProvider.GetRequiredService<
                Microsoft.Extensions.Options.IOptions<Novior.MentorAgent.Options.MentorOptions>>());

        return (collection, host);
    }

    private static IReadOnlyList<string> ToolNames(MentorAgentMcpToolCollection collection)
        => collection.GetTools().Select(t => t.ProtocolTool.Name).ToList();

    // ── What is published ───────────────────────────────────────────────────────

    [Fact]
    public async Task Ungated_actions_are_published()
    {
        // The point of the feature: an external agent can drive the application's own actions.
        var (collection, host) = Build();
        await using var _ = host;

        ToolNames(collection).Should().Contain("count_open_orders").And.Contain("get_active_orders");

        collection.Dispose();
    }

    [Fact]
    public async Task An_action_declared_read_only_is_published_with_readOnlyHint()
    {
        // [MentorAction(ReadOnly = true)] is the MCP readOnlyHint under another name: an MCP client deciding
        // whether it may call a tool without asking reads exactly that. Undeclared actions carry no hint.
        var (collection, host) = Build();
        await using var _ = host;

        var tools = collection.GetTools().ToDictionary(t => t.ProtocolTool.Name);
        tools["count_open_orders"].ProtocolTool.Annotations?.ReadOnlyHint.Should().BeTrue();
        tools["count_open_orders"].ProtocolTool.Name.Should().Be("count_open_orders");
        tools["count_open_orders"].ProtocolTool.Description.Should().Be("Counts open orders");
        // show_order_card: not ReadOnly, and published because it declares RequiresConfirmation = false (an
        // undeclared write is withheld since rc.15).
        (tools["show_order_card"].ProtocolTool.Annotations?.ReadOnlyHint ?? false).Should().BeFalse();

        collection.Dispose();
    }

    [Fact]
    public async Task A_published_tool_keeps_its_name_and_description()
    {
        // The MCP client sees only these. A tool whose description was lost is a tool no remote
        // model will choose correctly.
        var (collection, host) = Build();
        await using var _ = host;

        var tool = collection.GetTools().Single(t => t.ProtocolTool.Name == "count_open_orders");
        tool.ProtocolTool.Description.Should().Be("Counts open orders");

        collection.Dispose();
    }

    [Fact]
    public async Task Methods_with_no_attribute_are_not_published()
    {
        var (collection, host) = Build();
        await using var _ = host;

        ToolNames(collection).Should().NotContain("not_a_tool");

        collection.Dispose();
    }

    [Fact]
    public async Task The_tool_list_is_built_once_and_reused()
    {
        // The list is rebuilt on every ListTools request otherwise, and building it resolves every
        // tool class from DI.
        var (collection, host) = Build();
        await using var _ = host;

        collection.GetTools().Should().BeSameAs(collection.GetTools());

        collection.Dispose();
    }

    [Fact]
    public async Task A_tool_class_missing_from_DI_is_skipped_rather_than_taking_the_server_down()
    {
        // The collection is a singleton built on the first MCP request. Throwing would make the
        // endpoint permanently return an error for every tool because one class was unregistered.
        var host = MentorHostFixture.Create(o => o.McpServerEnabled = true);
        await using var _ = host;

        var collection = new MentorAgentMcpToolCollection(
            host.Get<MentorDiscoveryService>(),
            // An empty container: nothing the discovery found can be resolved.
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new TypedLogger<MentorAgentMcpToolCollection>(
                new TestLoggerProvider(host.Logs).CreateLogger("mcp-server")),
            host.Scope.ServiceProvider.GetRequiredService<
                Microsoft.Extensions.Options.IOptions<Novior.MentorAgent.Options.MentorOptions>>());

        var act = () => collection.GetTools();

        act.Should().NotThrow();
        collection.GetTools().Should().BeEmpty();
        host.Logs.HasWarning("Could not build MCP server tool").Should().BeTrue(host.Logs.WarningText);

        collection.Dispose();
    }

    // ── What must not be published ungated ──────────────────────────────────────

    [Fact]
    public async Task A_role_gated_action_is_not_handed_to_an_unauthenticated_mcp_client()
    {
        // BUG-021. Every other place a tool can be invoked wraps it in GatedAIFunction so
        // RequiredRoles travels with it — that wrapper exists because the attribute being "silently
        // ignored" on a specialist was a security gap, not a UX one. The MCP server built its tools
        // with a bare AIFunctionFactory.Create, so export_ledger (Admin) and cancel_order (Manager)
        // were callable by any client that could reach /mcp, with no principal to check.
        var (collection, host) = Build();
        await using var _ = host;

        var published = ToolNames(collection);
        published.Should().NotContain("export_ledger", "it is [MentorAction(RequiredRoles = [\"Admin\"])]");
        published.Should().NotContain("cancel_order", "it is Manager-only and irreversible");

        collection.Dispose();
    }

    [Fact]
    public async Task An_action_that_needs_human_approval_is_not_published_either()
    {
        // MCP has no user to ask. An action marked RequiresConfirmation is one the author decided a
        // human must see before it runs, and a protocol with nobody watching cannot honour that —
        // so it is withheld rather than run unattended.
        var (collection, host) = Build();
        await using var _ = host;

        ToolNames(collection).Should().NotContain("issue_refund");

        collection.Dispose();
    }

    [Fact]
    public async Task Withholding_a_tool_is_said_out_loud()
    {
        // Silence here would be the same failure in a different place: a developer who exposed
        // their actions over MCP and cannot find one has no way to learn why.
        var (collection, host) = Build();
        await using var _ = host;

        collection.GetTools();

        var log = string.Join("\n", host.Logs.Records.Select(r => r.Message));
        log.Should().Contain("withheld").And.Contain("export_ledger");

        collection.Dispose();
    }

    [Fact]
    public async Task The_withheld_actions_are_named_at_startup_before_any_mcp_request()
    {
        // BUG-178. The Server README promises a startup warning, and it came with the first tools/list or
        // tools/call: a host whose endpoint nobody had called yet did not know which actions were missing.
        // MapMentorAgentMcp names them now. The catalogue is still built by the first request, which does
        // not say it again.
        await using var app = await ServerAppFactory.CreateAsync(
            configure: o => o.McpServerEnabled = true,
            configureApp: a => a.UseEndpoints(e => e.MapMentorAgentMcp()));

        WithheldWarnings(app).Should().ContainSingle("no MCP request has been made")
            .Which.Should().Contain("export_ledger");
        app.Logs.Records.Should().NotContain(r => r.Message.Contains("MCP tool collection ready"),
            "naming the withheld actions does not build the catalogue");

        app.Services.GetRequiredService<MentorAgentMcpToolCollection>().GetTools();

        app.Logs.Records.Should().Contain(r => r.Message.Contains("MCP tool collection ready"));
        WithheldWarnings(app).Should().ContainSingle("the first request builds the catalogue and does not repeat it");
    }

    private static IEnumerable<string> WithheldWarnings(ServerAppFactory app) => app.Logs.Records
        .Where(r => r.Level == LogLevel.Warning && r.Message.Contains("withheld from the MCP server", StringComparison.Ordinal))
        .Select(r => r.Message);

    [Fact]
    public async Task The_ungated_neighbours_of_a_withheld_tool_are_still_published()
    {
        // Fail closed on the gated ones, not on the class they live in. TestOrderService carries
        // both kinds, and dropping the whole class would take the feature with it.
        var (collection, host) = Build();
        await using var _ = host;

        var published = ToolNames(collection);
        published.Should().Contain("count_open_orders");
        published.Should().NotContain("cancel_order");

        collection.Dispose();
    }
}
