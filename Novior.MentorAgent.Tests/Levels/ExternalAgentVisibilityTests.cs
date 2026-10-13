using FluentAssertions;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Novior.MentorAgent.Tests.Levels;

/// <summary>
/// An agent supplied by an <see cref="IMentorAgentSource"/> has to be <b>nameable</b> by the
/// coordinator, not merely reachable by it.
///
/// <para>
/// Found live on the packaged rc.5 (BUG-069): asked to consult the declarative <c>ShippingAgent</c>,
/// the assistant answered <i>"Non è disponibile uno ShippingAgent su questa piattaforma"</i> — while
/// the server log, three lines earlier, read <c>Handoff: declarative agent 'ShippingAgent' added to
/// workflow.</c> The graph had it; the prompt did not. <c>route_to_specialist</c>'s own description
/// names no agent, so nothing the model could see said the specialist existed, and the model did the
/// only honest thing available to it: it denied the agent.
/// </para>
///
/// <para>
/// This is the shape of BUG-013 again — built, counted, logged, and never shown to the model — and
/// it is why the assertion below is on the <b>instructions</b> rather than on the tool list.
/// </para>
/// </summary>
public sealed class ExternalAgentVisibilityTests
{
    /// <summary>A source that hands back one agent, the way Novior.MentorAgent.Declarative does.</summary>
    private sealed class OneAgentSource(string name, string description) : IMentorAgentSource
    {
        public Task<IReadOnlyList<AIAgent>> GetAgentsAsync(
            MentorAgentSourceContext context, CancellationToken ct = default)
        {
            var agent = new ChatClientAgent(
                context.ChatClient, "You are the shipping specialist.", name, description);
            return Task.FromResult<IReadOnlyList<AIAgent>>([agent]);
        }
    }

    private static async Task<string> CoordinatorInstructionsAsync(MentorHostFixture host)
    {
        await host.RunTurnAsync("ciao");
        // MemoryAutoCapture is off in these fixtures, but be explicit anyway: the coordinator's own
        // request is the last one that carried tools.
        var call = host.Chat!.Calls.Last(c => c.Tools.Count > 0);
        return call.Instructions ?? string.Empty;
    }

    [Fact]
    public async Task An_agent_from_a_source_is_named_in_the_coordinator_instructions()
    {
        await using var host = MentorHostFixture.Create(
            o => o.MemoryAutoCapture = false,
            services => services.AddSingleton<IMentorAgentSource>(
                new OneAgentSource("ShippingAgent", "Handles couriers, delivery times and tracking.")));
        host.Chat!.SayRepeatedly("ok", 4);

        var instructions = await CoordinatorInstructionsAsync(host);

        instructions.Should().Contain("ShippingAgent",
            "the model cannot delegate to a specialist it has never been told about");
        instructions.Should().Contain("couriers",
            "the description is what lets the model decide when to delegate");
    }

    [Fact]
    public async Task Attribute_declared_specialists_are_still_named_exactly_once()
    {
        // The control. Listing the source's agents must not duplicate, reorder or displace the ones
        // the attribute scan already found — the block is inside the cacheable prefix.
        await using var host = MentorHostFixture.Create(
            o => o.MemoryAutoCapture = false,
            services => services.AddSingleton<IMentorAgentSource>(
                new OneAgentSource("ShippingAgent", "Handles couriers.")));
        host.Chat!.SayRepeatedly("ok", 4);

        var instructions = await CoordinatorInstructionsAsync(host);

        // The fixture app declares TestShippingAgent via [MentorAgent]; whatever it is called, an
        // attribute-declared specialist must appear once and only once.
        var occurrences = System.Text.RegularExpressions.Regex
            .Matches(instructions, System.Text.RegularExpressions.Regex.Escape("ShippingAgent")).Count;
        occurrences.Should().BeGreaterThan(0);
        instructions.Split("ShippingAgent").Length.Should().Be(occurrences + 1);
    }

    [Fact]
    public async Task The_instructions_say_how_to_delegate_not_only_that_one_can()
    {
        // BUG-073, found on the published rc.6 the moment BUG-069 was verified: told the specialist
        // existed, the model now *narrated* the handoff — "Inoltro subito la domanda allo
        // ShippingAgent … attendo la sua risposta" — and ended the turn 4.8 s later with no tool
        // call, three times, once under an explicit order. The capabilities block said "You can
        // delegate to N specialized agents" and never named route_to_specialist; the tool's own
        // description named no agent. Nothing the model could see connected "delegate" to a tool.
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false);
        host.Chat!.SayRepeatedly("ok", 4);

        var instructions = await CoordinatorInstructionsAsync(host);

        instructions.Should().Contain("route_to_specialist",
            "a capability the prompt describes without naming the tool that performs it is narrated, not performed");
        instructions.Should().Contain("unless you have called that tool",
            "the rule against narrating an unperformed handoff has to live next to the capability it governs");
    }

    [Fact]
    public async Task The_handoff_tool_names_the_specialists_it_reaches()
    {
        // The tool schema is what the model reads when choosing a tool; a description that says
        // "the most appropriate specialized agent" gives it nothing to match a request against.
        // A name the fixture app does not declare itself — its own [MentorAgent] is "ShippingAgent",
        // so reusing that here would prove nothing about the source.
        await using var host = MentorHostFixture.Create(
            o => o.MemoryAutoCapture = false,
            services => services.AddSingleton<IMentorAgentSource>(
                new OneAgentSource("ReturnsAgent", "Handles returns and refunds.")));
        host.Chat!.SayRepeatedly("ok", 4);
        await host.RunTurnAsync("ciao");

        var tool = host.Chat.Calls.Last(c => c.Tools.Count > 0).Tool("route_to_specialist");
        tool.Should().NotBeNull();
        tool!.Description.Should().Contain("ReturnsAgent", "a source-supplied specialist is reachable through the same tool");
        tool.Description.Should().Contain("ShippingAgent", "the attribute-declared specialists are named too");
    }

    [Fact]
    public async Task With_no_source_registered_nothing_extra_is_claimed()
    {
        // A block that appears empty-handed would be worse than no block: it would invite the model
        // to delegate to a specialist that does not exist, which is the failure this file is about,
        // pointing the other way.
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false);
        host.Chat!.SayRepeatedly("ok", 4);

        var instructions = await CoordinatorInstructionsAsync(host);

        instructions.Should().NotContain("0 agent(s) from registered source");
    }
}
