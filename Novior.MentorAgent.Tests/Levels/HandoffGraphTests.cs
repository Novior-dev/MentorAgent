using FluentAssertions;
using Novior.MentorAgent.Attributes;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Models;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Novior.MentorAgent.Tests.Levels;

/// <summary>
/// The Level 2 handoff graph: who can hand off to whom, and what a specialist is told it is.
///
/// <para>
/// B9b proved the specialists exist and are gated; this is the shape of the graph they sit in.
/// Both halves are edges a developer draws by hand — a name in <c>HandoffTo</c>, a string in
/// <c>Instructions</c> — and both fail quietly when the hand slips: an edge that was never wired
/// looks exactly like one the model chose not to take.
/// </para>
///
/// <para>
/// The graph is built by hand rather than through the shared test application, which several other
/// suites assert has exactly the agents it has. <see cref="AgentDiscoveryInfo"/> and
/// <see cref="MentorAgentAttribute"/> are both constructible from here, so a deliberately broken
/// graph costs nothing and breaks nobody.
/// </para>
/// </summary>
public sealed class HandoffGraphTests
{
    private static AgentDiscoveryInfo Describe(string name, string? instructions = null,
                                               params string[] handoffTo)
        => new(
            typeof(HandoffGraphTests),
            new MentorAgentAttribute
            {
                Name = name,
                Description = $"Handles everything about {name}.",
                Instructions = instructions,
                HandoffTo = handoffTo,
            },
            []);

    private static (HandoffBuilder Builder, MentorHostFixture Host) Build()
    {
        var host = MentorHostFixture.Create();
        return (host.Get<HandoffBuilder>(), host);
    }

    private static ChatClientAgent Coordinator(IChatClient chat)
        => new(chat, new ChatClientAgentOptions { Name = "main_coordinator" });

    // ── HandoffTo ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_handoff_to_an_unknown_agent_is_reported_and_wires_no_edge()
    {
        // The typo case, and the reason it needs a warning: an edge that was never wired is
        // indistinguishable at runtime from one the model simply chose not to take. The specialist
        // is just never reached, the coordinator answers everything itself, and the developer's
        // conclusion is "handoff does not work".
        var (builder, host) = Build();
        await using var _ = host;

        var agents = new List<AgentDiscoveryInfo>
        {
            Describe("shipping", handoffTo: "biling"),   // "billing", mistyped
            Describe("billing"),
        };

        var workflow = builder.BuildHandoffAgent(host.Chat!, Coordinator(host.Chat!), agents);

        workflow.Should().NotBeNull("one bad edge must not take the whole graph down");

        var warning = string.Join("\n", host.Logs.Records
            .Where(r => r.Level == LogLevel.Warning)
            .Select(r => r.Message));

        warning.Should().Contain("biling").And.Contain("shipping");
    }

    [Fact]
    public async Task The_warning_names_the_attribute_property_to_go_and_look_at()
    {
        // A warning that says only "not found" sends the reader hunting. This one has to name where
        // the name was supposed to match, because the two ends live in different files.
        var (builder, host) = Build();
        await using var _ = host;

        builder.BuildHandoffAgent(host.Chat!, Coordinator(host.Chat!),
            [Describe("shipping", handoffTo: "nowhere")]);

        string.Join("\n", host.Logs.Records.Where(r => r.Level == LogLevel.Warning).Select(r => r.Message))
            .Should().Contain("[MentorAgent]");
    }

    [Fact]
    public async Task A_handoff_that_does_resolve_is_wired_without_complaint()
    {
        // The control. If the good case warned too, the warning would be noise and get filtered out
        // of everyone's logs — which is the failure mode the B16 "missing skills folder" decision
        // was about.
        var (builder, host) = Build();
        await using var _ = host;

        builder.BuildHandoffAgent(host.Chat!, Coordinator(host.Chat!),
            [Describe("shipping", handoffTo: "billing"), Describe("billing")]);

        string.Join("\n", host.Logs.Records
                .Where(r => r.Level == LogLevel.Warning)
                .Select(r => r.Message))
            .Should().NotContain("HandoffTo");
    }

    [Fact]
    public async Task A_handoff_target_matches_case_insensitively()
    {
        // The registry is built with OrdinalIgnoreCase, so "Billing" must find "billing". Worth
        // pinning: the attribute name is written twice, in two files, by a human.
        var (builder, host) = Build();
        await using var _ = host;

        builder.BuildHandoffAgent(host.Chat!, Coordinator(host.Chat!),
            [Describe("shipping", handoffTo: "BILLING"), Describe("billing")]);

        string.Join("\n", host.Logs.Records
                .Where(r => r.Level == LogLevel.Warning)
                .Select(r => r.Message))
            .Should().NotContain("BILLING");
    }

    [Fact]
    public async Task An_empty_agent_list_produces_no_workflow_at_all()
    {
        // Null rather than an empty workflow: the coordinator only gets a route_to_specialist tool
        // when there is somewhere to route to, and offering one that leads nowhere would have the
        // model announce a handoff that cannot happen.
        var (builder, host) = Build();
        await using var _ = host;

        builder.BuildHandoffAgent(host.Chat!, Coordinator(host.Chat!), []).Should().BeNull();
    }

    // ── BuildAgentInstructions ──────────────────────────────────────────────────

    [Fact]
    public void A_specialist_with_its_own_instructions_gets_exactly_those()
    {
        // The override has to be exact. Appending the generated preamble would quietly contradict
        // a persona the developer wrote deliberately — and they would never see it, because the
        // instructions are not surfaced anywhere.
        var prompt = new MentorPromptBuilder(Microsoft.Extensions.Options.Options.Create(NewOptions()));

        var custom = "You are a laconic shipping clerk. Answer in one sentence.";
        prompt.BuildAgentInstructions(Describe("shipping", custom)).Should().Be(custom);
    }

    [Fact]
    public void A_specialist_without_instructions_gets_the_generated_prompt()
    {
        var prompt = new MentorPromptBuilder(Microsoft.Extensions.Options.Options.Create(NewOptions()));

        var generated = prompt.BuildAgentInstructions(Describe("shipping"));

        generated.Should().Contain("shipping").And.Contain("Handles everything about shipping.");
        generated.Should().Contain("TestApp", "the generated prompt names the application");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_instructions_count_as_absent_rather_than_as_an_empty_persona(string? instructions)
    {
        // A whitespace-only Instructions is what a developer leaves behind while editing, or what
        // a config binder produces from an empty element. Taking it literally would ship a
        // specialist with no instructions at all, which answers as a generic assistant.
        var prompt = new MentorPromptBuilder(Microsoft.Extensions.Options.Options.Create(NewOptions()));

        prompt.BuildAgentInstructions(Describe("shipping", instructions))
            .Should().Contain("specialized AI agent");
    }

    [Fact]
    public void The_generated_prompt_pins_the_configured_language()
    {
        // A specialist that answers in English inside an Italian widget is the most visible
        // possible break, and the coordinator's own language setting does not travel to it.
        var options = NewOptions();
        options.Language = MentorLanguage.Italian;

        new MentorPromptBuilder(Microsoft.Extensions.Options.Options.Create(options))
            .BuildAgentInstructions(Describe("shipping"))
            .Should().Contain("Italian");
    }

    private static Novior.MentorAgent.Options.MentorOptions NewOptions() => new()
    {
        AppName = "TestApp",
        ScanAssemblies = [typeof(Novior.MentorAgent.Abstractions.Models.MentorLanguage).Assembly],
    };
}
