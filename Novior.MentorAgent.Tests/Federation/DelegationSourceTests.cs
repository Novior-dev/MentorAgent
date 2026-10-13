using FluentAssertions;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Models;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace Novior.MentorAgent.Tests.Federation;

/// <summary>
/// BUG-082 — on a host with remote agents, a delegated answer has to say <b>whose</b> it is.
///
/// <para>
/// Found live on the published rc.10, re-driving row F2 of the release matrix with every answer held
/// against the peer's REST data <i>and</i> the peer's log. "Chiedi a ShopFlowRemote: quanto ha speso in
/// totale la cliente Anna Ferrari?", from the React sample, came back in 8.8 s instead of ~30, left no
/// task on the peer, and read "…€5.800,00 sull'istanza ShopFlowRemote". Reproduced on the source pair
/// with the span in hand: the coordinator had called <c>route_to_specialist</c> with the request
/// tidied to "Quanto ha speso in totale la cliente Anna Ferrari?" and <b>no</b> <c>specialist</c>;
/// the router handed a question about a customer to the local CustomerAgent; the tool returned that
/// agent's words and nothing else; the coordinator attributed the figure to the system the user had
/// asked about. It was right only because both samples share their seed.
/// </para>
///
/// <para>
/// A routing miss is a rate, and a prompt lowers a rate. What must not depend on a rate is whether the
/// user is told the truth about where a figure came from — so the result carries its source.
/// </para>
/// </summary>
public sealed class DelegationSourceTests
{
    private static readonly string[] Remotes = ["ShopFlowRemote"];

    private static AgentResponse Answer(string author, string text) => new(
    [
        new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("h1", "handoff_to_1")])
        {
            AuthorName = MentorPromptBuilder.HandoffRouterName,
        },
        new ChatMessage(ChatRole.Assistant, text) { AuthorName = author },
    ]);

    private static (string Result, TestLogCollector Logs) Result(AgentResponse response, string? requested, string[] remotes)
    {
        var logs = new TestLogCollector();
        var logger = new TestLoggerProvider(logs).CreateLogger("delegation");
        return (MentorOrchestrator.DelegationResult(response, logger, requested, remotes), logs);
    }

    // ── the live failure: nobody was named, a local specialist answered ─────────

    [Fact]
    public void A_local_answer_on_a_host_with_remote_agents_is_labelled_local_and_says_what_to_do()
    {
        var (result, logs) = Result(
            Answer("CustomerAgent", "Anna Ferrari ha speso in totale €5.800,00."), requested: null, Remotes);

        result.Should().StartWith("[Note for you, not text for the user");
        result.Should().Contain("this application's own data").And.Contain("no remote agent was contacted");
        result.Should().Contain("once more with `specialist` set to that agent's name",
            "a fact is not an instruction (BUG-073): told only that nobody was contacted, the model relays the local figure");
        result.Should().Contain("ShopFlowRemote", "the coordinator is told which names count as another system");
        result.Should().NotContain("CustomerAgent",
            "the note must not hand the coordinator an internal name to repeat to the user");
        result.Should().EndWith("Anna Ferrari ha speso in totale €5.800,00.", "the answer still travels");
        logs.WarningText.Should().BeEmpty("nothing went wrong that the host can know about: nobody was asked for");
    }

    [Fact]
    public void A_local_answer_to_a_request_for_the_remote_agent_is_not_the_remote_agents()
    {
        var (result, logs) = Result(
            Answer("CustomerAgent", "Anna Ferrari ha speso in totale €5.800,00."), requested: "ShopFlowRemote", Remotes);

        result.Should().StartWith("NOT ShopFlowRemote'S ANSWER");
        result.Should().Contain("nothing was asked of ShopFlowRemote").And.Contain("this application's own data");
        result.Should().NotContain("once more",
            "the name was already in front of the router: the same call takes the same path, and nothing bounds a retry");
        result.Should().NotContain("CustomerAgent");
        logs.HasWarning("ShopFlowRemote was asked for and did not take part").Should().BeTrue(
            "a request that reached the wrong system has to be visible to the operator. Warnings:{0}{1}",
            Environment.NewLine, logs.WarningText);
    }

    [Fact]
    public void A_remote_answer_is_labelled_remote()
    {
        var (result, logs) = Result(
            Answer("ShopFlowRemote", "Anna Ferrari ha speso €5.800,00 sul nostro e-commerce."),
            requested: "shopflowremote ", Remotes);

        result.Should().StartWith("[Note for you, not text for the user — this answer comes from ShopFlowRemote, a remote agent");
        result.Should().Contain("not this application's",
            "\"sul nostro e-commerce\" is the peer speaking — seen relayed word for word by the API sample on rc.10");
        result.Should().NotContain("once more").And.NotContain("NOT ShopFlowRemote'S");
        logs.WarningText.Should().BeEmpty("case and padding are the model's, not a mismatch");
    }

    [Fact]
    public void A_remote_agent_that_was_reached_and_a_local_one_that_spoke_are_both_named_as_sources()
    {
        var response = new AgentResponse(
        [
            new ChatMessage(ChatRole.Assistant, "Qui risultano 5 clienti.") { AuthorName = "CustomerAgent" },
            new ChatMessage(ChatRole.Assistant, "Da noi risultano 7 clienti.") { AuthorName = "ShopFlowRemote" },
        ]);

        var (result, _) = Result(response, requested: "ShopFlowRemote", Remotes);

        result.Should().Contain("part of this answer comes from ShopFlowRemote").And.Contain("Keep the two apart");
        result.Should().NotContain("NOT ShopFlowRemote'S");
    }

    [Fact]
    public void A_peer_that_is_configured_but_was_down_when_the_session_was_built_is_still_a_remote_name()
    {
        // Review finding: the prompts advertise every CONFIGURED remote, the workflow holds only the
        // ones whose card could be fetched. In that window a request for the peer can only be
        // answered locally — the case that most needs the note, and the one a "built agents" list misses.
        var (result, logs) = Result(
            Answer("CustomerAgent", "Anna Ferrari ha speso in totale €5.800,00."),
            requested: "ShopFlowRemote", remotes: ["ShopFlowRemote"]);

        result.Should().StartWith("NOT ShopFlowRemote'S ANSWER");
        logs.HasWarning("did not take part").Should().BeTrue();
    }

    // ── what must not change ─────────────────────────────────────────────────────

    [Fact]
    public void A_host_without_remote_agents_reads_exactly_what_it_read_before()
    {
        var (named, logs) = Result(Answer("OrderAgent", "L'ordine #1002 è Shipped."), requested: "OrderAgent", remotes: []);
        named.Should().Be("L'ordine #1002 è Shipped.");

        // Asked for one local specialist, answered by another: not this bug. OrderAgent ↔ CustomerAgent
        // hand off to each other by design, and whose data it is does not change.
        var (other, _) = Result(Answer("CustomerAgent", "Paolo Verdi ha il piano Basic."), requested: "OrderAgent", remotes: []);
        other.Should().Be("Paolo Verdi ha il piano Basic.");
        logs.WarningText.Should().BeEmpty();
    }

    [Fact]
    public void The_two_argument_form_is_unchanged()
    {
        var logger = new TestLoggerProvider(new TestLogCollector()).CreateLogger("delegation");

        MentorOrchestrator.DelegationResult(Answer("OrderAgent", "L'ordine #1002 è Shipped."), logger)
            .Should().Be("L'ordine #1002 è Shipped.");
    }

    [Fact]
    public void A_name_the_model_made_up_is_not_a_request_for_the_remote_agent()
    {
        var (result, logs) = Result(
            Answer("CustomerAgent", "Paolo Verdi ha il piano Basic."), requested: "the remote one", Remotes);

        result.Should().StartWith("[Note for you", "the source note does not depend on the name");
        result.Should().NotContain("NOT ");
        logs.WarningText.Should().BeEmpty("compared as an identifier, never read as prose");
    }

    [Fact]
    public void A_remote_agent_that_took_part_without_speaking_was_still_reached()
    {
        // Review finding: "answered by" is read from messages with text, "was reached" must not be.
        var response = new AgentResponse(
        [
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c9", "handoff_to_2")]) { AuthorName = "ShopFlowRemote" },
            new ChatMessage(ChatRole.Assistant, "Ecco il dato.") { AuthorName = "CustomerAgent" },
        ]);

        var (result, logs) = Result(response, requested: "ShopFlowRemote", Remotes);

        result.Should().NotStartWith("NOT ShopFlowRemote'S ANSWER", "it was contacted; saying otherwise would be the false statement");
        logs.WarningText.Should().BeEmpty();
    }

    [Fact]
    public void The_failure_results_are_not_prefixed()
    {
        // NOT DELEGATED / DELEGATION FAILED are read by their first words.
        var routerOnly = new AgentResponse(
        [
            new ChatMessage(ChatRole.Assistant, "Sto chiedendo allo specialista…")
            {
                AuthorName = MentorPromptBuilder.HandoffRouterName,
            },
        ]);

        Result(routerOnly, requested: "ShopFlowRemote", Remotes).Result.Should().StartWith("NOT DELEGATED");
    }

    // ── the whole path, through the real host ────────────────────────────────────

    private const string PeerUrl = "http://127.0.0.1:59997";

    private static MentorHostFixture HostWithAPeer() => MentorHostFixture.Create(o =>
    {
        o.MemoryAutoCapture = false;
        o.RemoteAgents =
        [
            new MentorRemoteAgent { Name = "PeerInstance", Description = "The other deployment", AgentCardUrl = PeerUrl },
        ];
    });

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

    /// <summary>
    /// One brain, told apart by its tools (see RepeatedDelegationTests). The coordinator delegates
    /// the way the live one did — the request tidied, <c>specialist</c> as given — and the router
    /// hands off to its <b>first</b> function: a local specialist, because the application's agents
    /// are wired before the remote ones.
    /// </summary>
    private static (List<string> Received, List<string> RouterInstructions) MisroutingScript(
        FakeChatClient chat, string? specialist)
    {
        var received = new List<string>();
        var routerInstructions = new List<string>();
        chat.Respond((messages, options) =>
        {
            var tools = options?.Tools?.Select(t => t.Name).ToList() ?? [];
            if (tools.Count == 0) return Text("SAFE");

            if (tools.Contains("route_to_specialist"))
            {
                var toolResult = messages.LastOrDefault()?.Contents.OfType<FunctionResultContent>().LastOrDefault();
                if (toolResult is not null)
                {
                    received.Add(toolResult.Result?.ToString() ?? "(null)");
                    return Text("relayed");
                }

                var args = new Dictionary<string, object?> { ["request"] = "Quanto ha speso in totale la cliente Anna Ferrari?" };
                if (specialist is not null) args["specialist"] = specialist;
                return Call("route_to_specialist", args);
            }

            var handoffs = tools.Where(t => t.StartsWith("handoff_to", StringComparison.Ordinal)).ToList();
            if (handoffs.Count > 0 && handoffs.Count == tools.Count)
            {
                routerInstructions.Add(options?.Instructions ?? string.Empty);
                return Call(handoffs[0], new Dictionary<string, object?>());
            }

            return Text("Anna Ferrari ha speso in totale €5.800,00.");
        });
        return (received, routerInstructions);

        ChatResponse Text(string text) => new(new ChatMessage(ChatRole.Assistant, text)) { ModelId = chat.ModelId };
        ChatResponse Call(string name, IDictionary<string, object?> args) =>
            new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(Guid.NewGuid().ToString("N"), name, args)]))
            {
                ModelId = chat.ModelId,
            };
    }

    [Fact]
    public async Task The_live_failure_reaches_the_coordinator_labelled_local()
    {
        await using var host = HostWithAPeer();
        await SeedCardAsync(host);
        var (received, _) = MisroutingScript(host.Chat!, specialist: null);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await host.RunTurnAsync("Chiedi a PeerInstance: quanto ha speso in totale la cliente Anna Ferrari?", deadline.Token);

        received.Should().ContainSingle("the coordinator has to have been handed one delegation result");
        received[0].Should().StartWith("[Note for you", "Result:{0}{1}", Environment.NewLine, received[0]);
        received[0].Should().Contain("no remote agent was contacted").And.Contain("PeerInstance").And.Contain("€5.800,00");
    }

    [Fact]
    public async Task A_request_that_named_the_peer_and_was_answered_locally_is_reported_to_the_operator_too()
    {
        await using var host = HostWithAPeer();
        await SeedCardAsync(host);
        var (received, _) = MisroutingScript(host.Chat!, specialist: "PeerInstance");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await host.RunTurnAsync("Chiedi a PeerInstance: quanto ha speso in totale la cliente Anna Ferrari?", deadline.Token);

        received.Should().ContainSingle();
        received[0].Should().StartWith("NOT PeerInstance'S ANSWER", "Result:{0}{1}", Environment.NewLine, received[0]);
        host.Logs.HasWarning("PeerInstance was asked for and did not take part").Should().BeTrue(
            "Warnings:{0}{1}", Environment.NewLine, host.Logs.WarningText);
    }

    [Fact]
    public async Task The_router_is_shown_which_specialist_is_another_system_and_that_the_addressee_outweighs_the_subject()
    {
        await using var host = HostWithAPeer();
        await SeedCardAsync(host);
        var (_, routerInstructions) = MisroutingScript(host.Chat!, specialist: "PeerInstance");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await host.RunTurnAsync("Chiedi a PeerInstance: quanto ha speso Anna Ferrari?", deadline.Token);

        routerInstructions.Should().NotBeEmpty();
        routerInstructions[0].Should().Contain("- PeerInstance (remote agent — another system):",
            "a rule about \"a remote agent\" needs the list to say which entry is one");
        routerInstructions[0].Should().Contain("addressed to a remote agent is about THAT system's data");
        routerInstructions[0].Length.Should().BeLessThan(1500, "BUG-076's budget for the router's prompt still holds");
    }

    [Fact]
    public async Task A_turn_that_arrived_over_a2a_reads_what_it_read_before()
    {
        // Not offered the remotes (BUG-080) — in the prompt, in the router's list, and here.
        await using var host = HostWithAPeer();
        await SeedCardAsync(host);
        host.Get<MentorTurnOrigin>().FromAnotherAgent = true;
        var (received, routerInstructions) = MisroutingScript(host.Chat!, specialist: "PeerInstance");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await host.RunTurnAsync("quanto ha speso in totale la cliente Anna Ferrari?", deadline.Token);

        routerInstructions.Should().NotBeEmpty();
        routerInstructions[0].Should().NotContain("remote agent");
        received.Should().ContainSingle();
        received[0].Should().Be("Anna Ferrari ha speso in totale €5.800,00.",
            "there is one system in this turn, so there is nothing to label");
    }
}
