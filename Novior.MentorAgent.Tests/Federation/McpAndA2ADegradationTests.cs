using FluentAssertions;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Models;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Novior.MentorAgent.Tests.Federation;

/// <summary>
/// What happens when a federated dependency is not there — an MCP server that will not start, a
/// remote A2A agent whose card cannot be fetched.
///
/// <para>
/// Both builders run while the coordinator is being assembled, on the user's first message. An
/// exception escaping either of them takes the whole turn down over a dependency the assistant did
/// not strictly need: the local tools, the pages, the memory and the model are all still there. So
/// the contract is the same for both — degrade, tell the badge, warn, and carry on — and the only
/// way to be sure of it is to point them at something that is genuinely not running.
/// </para>
///
/// <para>
/// These use real unreachable addresses on a closed port rather than a mock transport. The point is
/// the failure path, and a mocked failure proves the catch block exists without proving it catches
/// what actually gets thrown.
/// </para>
/// </summary>
public sealed class McpAndA2ADegradationTests
{
    /// <summary>A port nothing is listening on, so a connection attempt fails quickly.</summary>
    private const string DeadEndpoint = "http://127.0.0.1:9/";

    private static (McpToolsBuilder Builder, RecordingStateService State, TestLogCollector Logs) Mcp(
        params MentorMcpServer[] servers)
    {
        var logs = new TestLogCollector();
        var factory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Debug);
            b.AddProvider(new TestLoggerProvider(logs));
        });

        var options = new MentorOptions
        {
            AppName = "TestApp",
            ScanAssemblies = [typeof(McpAndA2ADegradationTests).Assembly],
            McpServers = servers,
        };
        var wrapped = new OptionsWrapper<MentorOptions>(options);
        var state = new RecordingStateService();

        var builder = new McpToolsBuilder(
            wrapped, state,
            Confirmations(wrapped),
            factory,
            new TypedLogger<McpToolsBuilder>(factory.CreateLogger("mcp")));

        return (builder, state, logs);
    }

    private static (A2AAgentBuilder Builder, TestLogCollector Logs) A2A(params MentorRemoteAgent[] agents)
    {
        var logs = new TestLogCollector();
        var options = new MentorOptions
        {
            AppName = "TestApp",
            ScanAssemblies = [typeof(McpAndA2ADegradationTests).Assembly],
            RemoteAgents = agents,
        };

        var builder = new A2AAgentBuilder(
            new OptionsWrapper<MentorOptions>(options),
            new TypedLogger<A2AAgentBuilder>(new TestLoggerProvider(logs).CreateLogger("a2a")));

        return (builder, logs);
    }

    /// <summary>
    /// The shared confirmation builder. It reads the discovery catalogue for `[MentorAction]`
    /// gating; MCP tool names are registered on top of that at connection time.
    /// </summary>
    private static MentorConfirmationBuilder Confirmations(IOptions<MentorOptions> options)
        => new(new MentorDiscoveryService(options, Microsoft.Extensions.Logging.Abstractions
                   .NullLogger<MentorDiscoveryService>.Instance),
               options);

    private static CancellationToken Deadline(int seconds = 30)
        => new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token;

    private static IReadOnlyList<(string Server, bool Connected)> StatusEvents(RecordingStateService state)
        => state.Log
            .Where(e => e.Event == nameof(RecordingStateService.NotifyMcpServerStatusChanged))
            .Select(e => ((string)e.Args[0]!, (bool)e.Args[1]!))
            .ToList();

    // ── MCP: nothing configured ─────────────────────────────────────────────────

    [Fact]
    public async Task With_no_mcp_servers_nothing_is_attempted()
    {
        var (builder, state, logs) = Mcp();

        var tools = await builder.BuildAsync(Deadline(5));

        tools.Should().BeEmpty();
        StatusEvents(state).Should().BeEmpty("a badge for a server nobody configured is noise");
        logs.WarningText.Should().BeEmpty();
        await builder.DisposeAsync();
    }

    [Fact]
    public async Task Disposing_a_builder_that_connected_to_nothing_is_safe()
    {
        var (builder, _, _) = Mcp();
        await builder.BuildAsync(Deadline(5));

        var act = async () => { await builder.DisposeAsync(); await builder.DisposeAsync(); };

        await act.Should().NotThrowAsync();
    }

    // ── MCP: misconfiguration ───────────────────────────────────────────────────

    [Fact]
    public async Task A_server_with_neither_a_url_nor_a_command_is_reported_not_thrown()
    {
        // The one purely local failure: there is nothing to connect to because the configuration
        // never said where. It must still travel the same degradation path as an outage, or the
        // first message of the application throws instead of answering.
        var (builder, state, logs) = Mcp(new MentorMcpServer { Name = "docs" });

        var tools = await builder.BuildAsync(Deadline(5));

        tools.Should().BeEmpty();
        logs.HasWarning("Failed to connect to MCP server 'docs'").Should().BeTrue(logs.WarningText);
        await builder.DisposeAsync();
    }

    [Fact]
    public async Task A_server_that_is_not_running_leaves_the_badge_disconnected()
    {
        // The badge is the only thing that tells a user why an answer is thinner than usual. A
        // failure that fired no event would leave it stuck on "connecting" forever.
        var (builder, state, logs) = Mcp(new MentorMcpServer { Name = "docs", ServerUrl = DeadEndpoint });

        await builder.BuildAsync(Deadline(5));

        StatusEvents(state).Should().ContainSingle()
                           .Which.Should().Be(("docs", false));
        await builder.DisposeAsync();
    }

    [Fact]
    public async Task One_unreachable_server_does_not_stop_the_next_being_tried()
    {
        // The loop must not abandon the rest of the list on the first failure — otherwise the order
        // of a configuration array silently decides which integrations work. Two servers rather
        // than three: each real connection attempt costs a couple of seconds, and the second one
        // already proves the loop continued.
        var (builder, state, _) = Mcp(
            new MentorMcpServer { Name = "first", ServerUrl = DeadEndpoint },
            new MentorMcpServer { Name = "second", ServerUrl = DeadEndpoint });

        await builder.BuildAsync(Deadline(10));

        StatusEvents(state).Select(e => e.Server).Should().Equal("first", "second");
        await builder.DisposeAsync();
    }

    [Fact]
    public async Task An_unreachable_server_contributes_no_tools()
    {
        // Fail closed: a phantom tool the model can call but nothing can serve turns every use of
        // it into an error the user sees.
        var (builder, _, _) = Mcp(new MentorMcpServer { Name = "docs", ServerUrl = DeadEndpoint });

        (await builder.BuildAsync(Deadline(5))).Should().BeEmpty();

        await builder.DisposeAsync();
    }

    // ── MCP: the confirmation gate ──────────────────────────────────────────────

    [Fact]
    public void A_server_marked_as_requiring_confirmation_gates_its_tools_by_name()
    {
        // MCP tools live outside the discovery cache, so the gate cannot read an attribute for
        // them. Registering the names with the shared confirmation builder is what makes a remote
        // tool ask the user exactly like a local [MentorAction(RequiresConfirmation = true)].
        // Novior.MentorAgent.Abstractions declares nothing, so the discovery catalogue is empty and the
        // only gated names are the ones registered here — never the test assembly, which would
        // pick up every other suite's fixtures.
        var options = new OptionsWrapper<MentorOptions>(new MentorOptions
        {
            AppName = "TestApp",
            ScanAssemblies = [typeof(Novior.MentorAgent.Abstractions.Interfaces.IMentorPageContext).Assembly],
        });
        var confirmations = Confirmations(options);

        confirmations.RegisterApprovalRequired("delete_issue");

        confirmations.RequiresApproval("delete_issue").Should().BeTrue();
        confirmations.RequiresApproval("list_issues").Should().BeFalse(
            "only the registered names are gated");
    }

    // ── A2A: nothing configured ─────────────────────────────────────────────────

    [Fact]
    public async Task With_no_remote_agents_nothing_is_attempted()
    {
        var (builder, logs) = A2A();

        (await builder.BuildAsync(Deadline(5))).Should().BeEmpty();

        logs.WarningText.Should().BeEmpty();
        builder.Dispose();
    }

    // ── A2A: unreachable ────────────────────────────────────────────────────────

    [Fact]
    public async Task A_remote_agent_whose_card_cannot_be_fetched_degrades_rather_than_throwing()
    {
        var (builder, logs) = A2A(new MentorRemoteAgent
        {
            Name = "billing", Description = "Handles invoices", AgentCardUrl = DeadEndpoint,
        });

        var agents = await builder.BuildAsync(Deadline(5));

        agents.Should().BeEmpty();
        logs.HasWarning("Failed to connect to remote agent 'billing'").Should().BeTrue(logs.WarningText);
        builder.Dispose();
    }

    [Fact]
    public async Task The_warning_names_the_url_that_did_not_answer()
    {
        // A wrong base URL is the most common A2A misconfiguration — the resolver appends
        // /.well-known/agent-card.json itself, so a URL that already includes the path fails.
        // Printing what was actually dialled is what makes that visible.
        var (builder, logs) = A2A(new MentorRemoteAgent
        {
            Name = "billing", Description = "Handles invoices", AgentCardUrl = DeadEndpoint,
        });

        await builder.BuildAsync(Deadline(5));

        logs.WarningText.Should().Contain(DeadEndpoint);
        builder.Dispose();
    }

    [Fact]
    public async Task A_malformed_agent_url_is_reported_like_any_other_failure()
    {
        var (builder, logs) = A2A(new MentorRemoteAgent
        {
            Name = "billing", Description = "Handles invoices", AgentCardUrl = "not a url",
        });

        var act = async () => await builder.BuildAsync(Deadline(5));

        await act.Should().NotThrowAsync();
        logs.HasWarning("Failed to connect to remote agent 'billing'").Should().BeTrue(logs.WarningText);
        builder.Dispose();
    }

    [Fact]
    public async Task One_unreachable_remote_agent_does_not_stop_the_next_being_tried()
    {
        var (builder, logs) = A2A(
            new MentorRemoteAgent { Name = "first", Description = "d", AgentCardUrl = DeadEndpoint },
            new MentorRemoteAgent { Name = "second", Description = "d", AgentCardUrl = DeadEndpoint });

        await builder.BuildAsync(Deadline(10));

        logs.HasWarning("'first'").Should().BeTrue(logs.WarningText);
        logs.HasWarning("'second'").Should().BeTrue(logs.WarningText);
        builder.Dispose();
    }

    // ── A2A ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Disposing_the_a2a_builder_twice_is_safe()
    {
        var (builder, _) = A2A();

        var act = () => { builder.Dispose(); builder.Dispose(); };

        act.Should().NotThrow();
    }
}
