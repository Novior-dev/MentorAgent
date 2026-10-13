using FluentAssertions;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Core.Mcp;
using Novior.MentorAgent.Models;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Novior.MentorAgent.Tests.Scenarios;

/// <summary>
/// <c>WarmUpAtStartup</c> — paying the shared start-up cost before anyone is waiting for it (C3).
///
/// <para>
/// Measured on the sample: the first message of a fresh process waited 3 seconds before a token
/// appeared, and 12 to 50 seconds when other circuits were starting their own MCP servers at the same
/// time. Almost all of that is work every session shares — the tool catalogue's embeddings, the
/// connections, the remote agent cards — done, until now, by whoever happened to arrive first.
/// </para>
/// </summary>
public sealed class WarmUpTests
{
    private static MentorHostFixture Host(bool warm, FakeMcpSessionFactory factory)
        => MentorHostFixture.Create(
            o =>
            {
                o.WarmUpAtStartup     = warm;
                o.EnableToolFiltering = true;
                o.McpServers = [new MentorMcpServer { Name = "docs", ServerUrl = "http://127.0.0.1:59999/mcp" }];
            },
            services => services.AddSingleton<IMcpSessionFactory>(factory),
            withEmbeddings: true);

    private static async Task RunWarmUpAsync(MentorHostFixture host)
    {
        // The fixture builds a container rather than a web host, so the hosted service is started
        // by hand — which is also the only way to await it without sleeping.
        var service = new MentorWarmUpService(
            host.Scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>()
                .CreateScope().ServiceProvider,
            host.Scope.ServiceProvider.GetRequiredService<
                Microsoft.Extensions.Options.IOptions<Novior.MentorAgent.Options.MentorOptions>>(),
            host.Scope.ServiceProvider.GetRequiredService<
                Microsoft.Extensions.Logging.ILogger<MentorWarmUpService>>());

        await service.StartAsync(default);
        await service.Completed.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task The_shared_work_is_done_before_the_first_visitor()
    {
        var factory = new FakeMcpSessionFactory();
        await using var host = Host(warm: true, factory);

        await RunWarmUpAsync(host);

        factory.Connects.Should().Be(1, "the shared MCP session is already open");
        host.Get<MentorEmbeddingCache>().Count.Should().BeGreaterThan(0,
            "and the tool catalogue is already embedded");
    }

    [Fact]
    public async Task With_the_option_off_nothing_happens_until_someone_asks()
    {
        var factory = new FakeMcpSessionFactory();
        await using var host = Host(warm: false, factory);

        await RunWarmUpAsync(host);

        factory.Connects.Should().Be(0);
        host.Get<MentorEmbeddingCache>().Count.Should().Be(0);
    }

    [Fact]
    public async Task A_warm_up_that_fails_does_not_stop_the_application()
    {
        // An application that refuses to boot because a remote MCP server is down would be a worse
        // failure than the slow first message this is meant to avoid.
        var factory = new FakeMcpSessionFactory { ThrowOnConnect = new HttpRequestException("refused") };
        await using var host = Host(warm: true, factory);

        var act = async () => await RunWarmUpAsync(host);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task The_warm_up_waits_until_the_application_has_started()
    {
        // Its documentation said "after the host has started listening" while StartAsync launched the
        // work at once, before the other hosted services — the web server among them — had started.
        var factory = new FakeMcpSessionFactory();
        await using var host = Host(warm: true, factory);
        var lifetime = new StartedLater();
        var service = new MentorWarmUpService(
            host.Scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>().CreateScope().ServiceProvider,
            host.Scope.ServiceProvider.GetRequiredService<
                Microsoft.Extensions.Options.IOptions<Novior.MentorAgent.Options.MentorOptions>>(),
            host.Scope.ServiceProvider.GetRequiredService<
                Microsoft.Extensions.Logging.ILogger<MentorWarmUpService>>(),
            lifetime);

        await service.StartAsync(default);
        var early = await Task.WhenAny(service.Completed.Task, Task.Delay(TimeSpan.FromMilliseconds(500)));

        early.Should().NotBeSameAs(service.Completed.Task, "nothing runs before ApplicationStarted");
        factory.Connects.Should().Be(0);

        lifetime.Start();
        await service.Completed.Task.WaitAsync(TimeSpan.FromSeconds(30));

        factory.Connects.Should().Be(1, "once the application has started, the warm-up runs");
    }

    private sealed class StartedLater : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _started = new();

        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }

        internal void Start() => _started.Cancel();
    }

    [Fact]
    public async Task The_first_real_turn_after_a_warm_up_embeds_nothing_shared()
    {
        // The measurable half: what the first visitor is charged for.
        var factory = new FakeMcpSessionFactory();
        await using var host = Host(warm: true, factory);
        host.Chat!.SayRepeatedly("ok", 4);

        await RunWarmUpAsync(host);
        var embeddedDuringWarmUp = host.Embeddings!.InputCount;

        await host.RunTurnAsync("quanti ordini sono aperti?");

        (host.Embeddings.InputCount - embeddedDuringWarmUp).Should().BeLessThan(3,
            "the turn embeds its own message, not the catalogue");
    }
}
