using FluentAssertions;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Memory;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Novior.MentorAgent.Tests.Observability;

/// <summary>
/// The coordinator's configuration summary is about the <b>application</b>, and it was written once
/// per <b>session</b>.
///
/// <para>
/// Measured live on rc.4: about fifteen Information lines per new browser circuit — routing, tool
/// filtering, hosted tools, the approval list, and one carrying the ~900-character domain scope
/// sentence. Identical every time, because the configuration is identical every time. A day with a
/// thousand visitors is fifteen thousand lines that say the same thing, and the log stops being
/// readable exactly where an operator needs to read it.
/// </para>
///
/// <para>
/// Degradation <b>warnings</b> stay per session on purpose: those are about what this session is
/// missing, and a host that reads only warnings must keep seeing them.
/// </para>
/// </summary>
public sealed class BuildAnnouncementTests
{
    private static int InfoLines(MentorHostFixture host, string fragment)
        => host.Logs.Records.Count(r => r.Level == LogLevel.Information && r.Message.Contains(fragment));

    private static async Task RunTurnInNewSessionAsync(MentorHostFixture host)
    {
        await using var scope = host.Get<IServiceScopeFactory>().CreateAsyncScope();
        var orchestrator = scope.ServiceProvider
            .GetRequiredService<Novior.MentorAgent.Abstractions.Interfaces.IMentorOrchestrator>();
        await orchestrator.SendMessageAsync("ciao");
    }

    [Fact]
    public async Task The_configuration_is_announced_once_per_process_not_once_per_session()
    {
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false);
        host.Chat!.SayRepeatedly("ok", 6);

        await host.RunTurnAsync("ciao");
        await RunTurnInNewSessionAsync(host);
        await RunTurnInNewSessionAsync(host);

        InfoLines(host, "Coordinator built with").Should().Be(1,
            "three sessions, one application, one summary");
        InfoLines(host, "Coordinator initialized").Should().Be(1);
    }

    [Fact]
    public async Task A_later_session_still_leaves_a_trace_at_debug()
    {
        // Silence would be its own defect: "did the second circuit build a coordinator at all?" has
        // to remain answerable.
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false);
        host.Chat!.SayRepeatedly("ok", 6);

        await host.RunTurnAsync("ciao");
        await RunTurnInNewSessionAsync(host);

        host.Logs.Records.Count(r => r.Level == LogLevel.Debug
                                     && r.Message.Contains("Coordinator built for a new session"))
            .Should().Be(1);
    }

    [Fact]
    public async Task Degradation_warnings_are_still_raised_for_every_session()
    {
        // Tool filtering without an embedding generator is this session's problem, not the
        // application's biography — every session that loses the feature says so.
        await using var host = MentorHostFixture.Create(o =>
        {
            o.EnableToolFiltering = true;   // no EmbeddingGenerator → warns at every build
            o.MemoryAutoCapture   = false;
        });
        host.Chat!.SayRepeatedly("ok", 6);

        await host.RunTurnAsync("ciao");
        await RunTurnInNewSessionAsync(host);

        host.Logs.Records.Count(r => r.Level >= LogLevel.Warning
                                     && r.Message.Contains("no EmbeddingGenerator is configured"))
            .Should().Be(2, "one per session");
    }

    [Fact]
    public async Task A_remote_agents_connection_line_is_announced_once()
    {
        // BUG-068's addendum, seen on the published rc.6 with a live A2A peer: "Remote agent
        // 'ShopFlowRemote' connected from …" was written at Information once per circuit — the
        // A2A builder has its own logger and never consulted the announcement. The general test
        // above could not see it: the fixture app declares no remote agent. The card is pre-seeded
        // in the process-wide cache (same key the builder computes), so no socket is involved.
        await using var host = MentorHostFixture.Create(o =>
        {
            o.MemoryAutoCapture = false;
            o.RemoteAgents =
            [
                new Novior.MentorAgent.Models.MentorRemoteAgent
                {
                    Name = "peer", Description = "A seeded peer", AgentCardUrl = "http://127.0.0.1:59999",
                },
            ];
        });

        var card = new A2A.AgentCard
        {
            Name = "peer", Version = "1.0.0",
            SupportedInterfaces =
            [
                new A2A.AgentInterface { Url = "http://127.0.0.1:59999/a2a", ProtocolBinding = "HTTP+JSON", ProtocolVersion = "1.0" },
            ],
        };
        await host.Get<A2ACardCache>().GetAsync(
            "peer|http://127.0.0.1:59999|", "peer", "http://127.0.0.1:59999",
            _ => Task.FromResult(card), default);

        host.Chat!.SayRepeatedly("ok", 9);
        await host.RunTurnAsync("ciao");
        await RunTurnInNewSessionAsync(host);
        await RunTurnInNewSessionAsync(host);

        InfoLines(host, "connected from").Should().Be(1,
            "the peer is the application's, and three circuits are one application");
    }

    [Fact]
    public async Task No_build_time_information_line_is_repeated_for_a_later_session()
    {
        // Found live on the packaged rc.5, not by this suite: the second Blazor circuit still
        // reprinted seven Information lines. The sentinel was claimed one statement too late (the
        // skills, the external agents and the hosted-tool declaration are all built before it) and
        // three collaborating builders never learned about it at all. The assertion is deliberately
        // general — "no build line twice" — rather than a list of the lines that were wrong then.
        await using var host = MentorHostFixture.Create(o =>
        {
            o.MemoryAutoCapture  = false;
            o.EnableSkills       = true;
            o.HostedTools        = MentorHostedTools.WebSearch | MentorHostedTools.ImageGeneration;
            o.HostedImageModel   = "gpt-image-1-mini";
        });
        host.Chat!.SayRepeatedly("ok", 9);

        await host.RunTurnAsync("ciao");
        await RunTurnInNewSessionAsync(host);
        await RunTurnInNewSessionAsync(host);

        // Per-turn accounting legitimately repeats; the application's biography does not.
        var repeated = host.Logs.Records
            .Where(r => r.Level == LogLevel.Information && !r.Message.Contains("Tokens —"))
            .GroupBy(r => r.Message)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Count()}× {g.Key}")
            .ToList();

        string.Join(Environment.NewLine, repeated).Should().BeEmpty(
            "three sessions of one application announce its configuration once");
    }
}
