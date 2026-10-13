using FluentAssertions;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Models;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Novior.MentorAgent.Tests.Federation;

/// <summary>
/// BUG-080 — a request that arrives over A2A is answered where it lands.
///
/// <para>
/// Found live on the published rc.8, re-driving row F3 of the release matrix. BUG-078 had kept every
/// A2A round trip from completing, so nothing had ever shown what a MentorAgent host does with a
/// request it receives <i>from</i> a peer when it has a peer of its own: it built its remote agents
/// for that turn like for any other, and its coordinator — handed a request that named a remote
/// specialist — delegated it onward. Two hosts that peer each other (the ApiServer and React samples
/// do, and so will any pair of deployments that can ask each other questions) passed one question
/// back and forth until a process was killed: nine tasks, ~6,000 input tokens a hop, no bound.
/// </para>
///
/// <para>
/// Stripping the router's marker (see <see cref="A2AChatClientTests"/>) removes the trigger that was
/// seen. This is the guard for the ones that were not: a turn whose caller is another agent is not
/// offered this host's remote agents at all, so there is no hop to take.
/// </para>
/// </summary>
public sealed class A2AForwardingTests
{
    private const string PeerUrl = "http://127.0.0.1:59998";

    private static MentorRemoteAgent Peer => new()
    {
        Name = "PeerInstance", Description = "The other deployment", AgentCardUrl = PeerUrl,
    };

    /// <summary>The card, pre-seeded under the key the builder computes — no socket is involved.</summary>
    private static Task SeedCardAsync(MentorHostFixture host) =>
        host.Get<A2ACardCache>().GetAsync(
            $"PeerInstance|{PeerUrl}|", "PeerInstance", PeerUrl,
            _ => Task.FromResult(new A2A.AgentCard
            {
                Name = "PeerInstance", Version = "1.0.0",
                SupportedInterfaces =
                [
                    new A2A.AgentInterface
                    {
                        Url = PeerUrl + "/a2a", ProtocolBinding = A2A.ProtocolBindingNames.JsonRpc, ProtocolVersion = "1.0",
                    },
                ],
            }), default);

    private static MentorHostFixture HostWithAPeer() => MentorHostFixture.Create(o =>
    {
        o.MemoryAutoCapture = false;
        o.RemoteAgents = [Peer];
    });

    private static int Lines(MentorHostFixture host, string fragment) =>
        host.Logs.Records.Count(r => r.Message.Contains(fragment, StringComparison.Ordinal));

    [Fact]
    public async Task An_ordinary_turn_is_offered_the_hosts_remote_agents()
    {
        // The control. If this fails the fixture is wrong, not the guard.
        await using var host = HostWithAPeer();
        await SeedCardAsync(host);
        host.Chat!.SayRepeatedly("ok", 3);

        await host.RunTurnAsync("ciao");

        var coordinator = host.Chat.Calls.Last(c => c.ToolNames.Contains("route_to_specialist"));
        Lines(host, "connected from").Should().Be(1, "a person's turn gets the peer");
        (coordinator.Instructions ?? "").Should().Contain("PeerInstance");
        coordinator.Tool("route_to_specialist")!.Description.Should().Contain("PeerInstance");
    }

    [Fact]
    public async Task A_turn_that_arrived_over_a2a_is_not_offered_them()
    {
        await using var host = HostWithAPeer();
        await SeedCardAsync(host);
        host.Get<MentorTurnOrigin>().FromAnotherAgent = true;   // what MentorAgentA2AHandler sets
        host.Chat!.SayRepeatedly("ok", 3);

        await host.RunTurnAsync("quanti prodotti a catalogo?");

        Lines(host, "connected from").Should().Be(0,
            "the remote agents are not built for a turn that must not hop again");

        var coordinator = host.Chat.Calls.Last(c => c.ToolNames.Contains("route_to_specialist"));
        (coordinator.Instructions ?? "").Should().NotContain("PeerInstance",
            "a capability the turn does not have must not be in its prompt (BUG-035's lesson: every reader of a list)");
        coordinator.Tool("route_to_specialist")!.Description.Should().NotContain("PeerInstance",
            "and the tool's own description is the other place the model reads the specialists from");
    }

    [Fact]
    public async Task The_operator_is_told_once_that_forwarding_was_withheld()
    {
        // A capability that silently is not there is the failure mode this campaign keeps finding;
        // the line is at Debug because it is per request and expected.
        await using var host = HostWithAPeer();
        await SeedCardAsync(host);
        host.Get<MentorTurnOrigin>().FromAnotherAgent = true;
        host.Chat!.SayRepeatedly("ok", 3);

        await host.RunTurnAsync("quanti prodotti a catalogo?");

        host.Logs.Records.Should().ContainSingle(r =>
            r.Level == LogLevel.Debug && r.Message.Contains("arrived over A2A", StringComparison.Ordinal)
                                      && r.Message.Contains("PeerInstance", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_local_specialists_still_serve_a_request_that_arrived_over_a2a()
    {
        // The guard removes the onward hop, not delegation: the peer asked THIS host, and this
        // host's own specialists are how it answers.
        await using var host = HostWithAPeer();
        await SeedCardAsync(host);
        host.Get<MentorTurnOrigin>().FromAnotherAgent = true;
        host.Chat!.SayRepeatedly("ok", 3);

        await host.RunTurnAsync("dove si trova l'ordine 7?");

        host.Chat.Calls.Should().Contain(c => c.ToolNames.Contains("route_to_specialist"),
            "the application's L2 agents are still behind route_to_specialist");
    }
}
