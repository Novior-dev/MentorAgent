using FluentAssertions;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Core.Mcp;
using Novior.MentorAgent.Models;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Novior.MentorAgent.Tests.Federation;

/// <summary>
/// What a second visitor costs, measured where it is actually paid: two DI scopes of one host.
///
/// <para>
/// A scope is a browser circuit on Blazor Server and a connection on the hub. Everything the MCP
/// builder does <em>per scope</em> has to keep happening per scope — the status badge and the
/// confirmation registrations are that scope's — while the connection underneath is shared when it
/// can be.
/// </para>
/// </summary>
public sealed class McpScopeSharingTests
{
    private static MentorHostFixture HostWith(FakeMcpSessionFactory factory, params MentorMcpServer[] servers)
        => MentorHostFixture.Create(
            o => o.McpServers = servers,
            services => services.AddSingleton<IMcpSessionFactory>(factory),
            withRecorder: true);

    private static async Task BuildInNewScopeAsync(MentorHostFixture host)
    {
        await using var scope = host.Get<IServiceScopeFactory>().CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<McpToolsBuilder>().BuildAsync();
    }

    [Fact]
    public async Task A_local_server_is_started_once_per_session_as_before()
    {
        // The compatibility guarantee. A stdio server can hold state for the person who started it,
        // so nothing about it changes unless the host opts in.
        var factory = new FakeMcpSessionFactory();
        await using var host = HostWith(factory,
            new MentorMcpServer { Name = "files", Command = "npx", Arguments = ["-y", "server-filesystem"] });

        await host.Get<McpToolsBuilder>().BuildAsync();
        await BuildInNewScopeAsync(host);

        factory.Connects.Should().Be(2);
    }

    [Fact]
    public async Task A_local_server_marked_shared_is_started_once_for_the_process()
    {
        // The opt-in that turns eight node processes into two.
        var factory = new FakeMcpSessionFactory();
        await using var host = HostWith(factory,
            new MentorMcpServer
            { Name = "files", Command = "npx", Arguments = ["-y", "server-filesystem"], Shared = true });

        await host.Get<McpToolsBuilder>().BuildAsync();
        await BuildInNewScopeAsync(host);

        factory.Connects.Should().Be(1);
    }

    [Fact]
    public async Task A_remote_server_is_shared_without_being_asked()
    {
        var factory = new FakeMcpSessionFactory();
        await using var host = HostWith(factory,
            new MentorMcpServer { Name = "docs", ServerUrl = "http://127.0.0.1:59999/mcp" });

        await host.Get<McpToolsBuilder>().BuildAsync();
        await BuildInNewScopeAsync(host);
        await BuildInNewScopeAsync(host);

        factory.Connects.Should().Be(1, "three visitors, one connection to a remote endpoint");
    }

    [Fact]
    public async Task Every_session_still_gets_its_own_tools_and_its_own_status_event()
    {
        // The regression sharing could easily cause: the second visitor's badge never turns green,
        // because the connection it would have reported was opened for somebody else.
        var factory = new FakeMcpSessionFactory();
        await using var host = HostWith(factory,
            new MentorMcpServer { Name = "docs", ServerUrl = "http://127.0.0.1:59999/mcp" });

        await host.Get<McpToolsBuilder>().BuildAsync();

        await using var scope = host.Get<IServiceScopeFactory>().CreateAsyncScope();
        var recorder = new RecordingStateService();
        var second = new McpToolsBuilder(
            scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<Novior.MentorAgent.Options.MentorOptions>>(),
            recorder,
            scope.ServiceProvider.GetRequiredService<MentorConfirmationBuilder>(),
            scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>(),
            scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<McpToolsBuilder>>(),
            host.Get<McpClientPool>(),
            factory);

        var tools = await second.BuildAsync();

        tools.Should().NotBeEmpty("the second session gets the tool list even though it did not connect");
        recorder.Log.Should().Contain(e => e.Event == nameof(RecordingStateService.NotifyMcpServerStatusChanged),
            "and its own badge is told the server is online");
        factory.Connects.Should().Be(1);
    }

    [Fact]
    public async Task A_shared_server_that_requires_confirmation_gates_its_tools_in_every_session()
    {
        // The gate is registered per scope from the cached tool list, so a session that never
        // connected still asks before calling.
        var factory = new FakeMcpSessionFactory();
        await using var host = HostWith(factory,
            new MentorMcpServer
            {
                Name = "docs",
                ServerUrl = "http://127.0.0.1:59999/mcp",
                RequiresConfirmation = true,
            });

        await host.Get<McpToolsBuilder>().BuildAsync();

        await using var scope = host.Get<IServiceScopeFactory>().CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<McpToolsBuilder>().BuildAsync();

        scope.ServiceProvider.GetRequiredService<MentorConfirmationBuilder>()
            .RequiresApproval("docs_tool").Should().BeTrue();
    }

    [Fact]
    public async Task Ending_a_session_closes_only_what_that_session_owned()
    {
        // Disposing a pooled session at end of scope would close it for everyone — pooling's own
        // failure mode, inverted.
        var factory = new FakeMcpSessionFactory();
        await using var host = HostWith(factory,
            new MentorMcpServer { Name = "docs", ServerUrl = "http://127.0.0.1:59999/mcp" },
            new MentorMcpServer { Name = "files", Command = "npx", Arguments = ["-y", "server-filesystem"] });

        await using (var scope = host.Get<IServiceScopeFactory>().CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<McpToolsBuilder>().BuildAsync();

        var shared = factory.Sessions.Single(s => s.ServerName == "docs");
        var private_ = factory.Sessions.Single(s => s.ServerName == "files");

        shared.Disposed.Should().BeFalse("the pool owns it and other sessions are still using it");
        private_.Disposed.Should().BeTrue("this one belonged to the scope that ended");
    }
}
