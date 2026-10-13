using FluentAssertions;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.AI;
using Xunit;

namespace Novior.MentorAgent.Tests.Levels;

/// <summary>
/// A delegated turn has to work <b>every</b> time in a conversation, not only the first.
///
/// <para>
/// Found live on the published rc.7, the first build in which <c>route_to_specialist</c> was really
/// called (BUG-073 had hidden everything behind it): the first delegated turn reached the specialist
/// and came back with its answer; the second one, same circuit, returned at once — no agent ran
/// between <c>Tool call: route_to_specialist</c> and <c>Tool result</c> — and the coordinator fell
/// back to narrating ("Sto chiedendo allo specialista…"). No in-process test had ever executed the
/// handoff tool twice in one session: the suite checked that it was <i>offered</i>, and once that it
/// could be <i>called</i>.
/// </para>
///
/// <para>
/// The fake below is one brain answering as whoever is asking, told apart by the tools it is handed:
/// the coordinator owns <c>route_to_specialist</c>; the workflow's triage agent owns nothing but
/// <c>handoff_to_*</c> functions; a specialist owns real tools.
/// </para>
/// </summary>
public sealed class RepeatedDelegationTests
{
    private sealed class Trace
    {
        public List<string> Events { get; } = [];
        public List<string> TriageInstructions { get; } = [];
        public List<string> Received { get; } = [];
        public int SpecialistAnswers;
        public override string ToString() => string.Join(Environment.NewLine, Events);
    }

    private static long MeteredChatRequests(MentorHostFixture host)
        => host.Get<Novior.MentorAgent.Core.MentorMetrics>()
               .GetSnapshot().Models
               .Where(m => m.Kind != Novior.MentorAgent.Abstractions.Models.MentorModelKind.Embedding)
               .Sum(m => m.RequestCount);

    private static Trace Script(FakeChatClient chat, bool routerNarrates = false, bool specialistThrows = false)
    {
        var trace = new Trace();
        chat.Respond(Brain);
        return trace;

        ChatResponse Brain(IReadOnlyList<ChatMessage> messages, ChatOptions? options)
        {
            // By AITool name, not OfType<AIFunction>(): the workflow's handoff functions are
            // declaration-only (AIFunctionDeclaration), and a filter on the invocable subtype makes
            // the triage agent look like it was handed nothing at all.
            var tools = options?.Tools?.Select(t => t.Name).ToList() ?? [];
            var last = messages.LastOrDefault();
            var toolResult = last?.Contents.OfType<FunctionResultContent>().LastOrDefault();

            if (tools.Count == 0)
                return Text("SAFE");

            if (tools.Contains("route_to_specialist"))
            {
                if (toolResult is not null)
                {
                    var result = toolResult.Result?.ToString() ?? "(null)";
                    trace.Received.Add(result);
                    trace.Events.Add("coordinator received: " + result);
                    return Text("RELAY " + result);
                }

                var request = messages.Last(m => m.Role == ChatRole.User).Text;
                trace.Events.Add("coordinator delegates: " + request);
                return Call("route_to_specialist", new Dictionary<string, object?> { ["request"] = request });
            }

            var handoffs = tools.Where(t => t.StartsWith("handoff_to", StringComparison.Ordinal)).ToList();
            if (handoffs.Count > 0 && handoffs.Count == tools.Count)
            {
                trace.TriageInstructions.Add(options?.Instructions ?? string.Empty);
                if (routerNarrates)
                {
                    trace.Events.Add("triage narrates instead of handing off");
                    return Text("Sto chiedendo allo specialista spedizioni le opzioni per l'ordine.");
                }

                trace.Events.Add("triage hands off via " + handoffs[0]);
                return Call(handoffs[0], new Dictionary<string, object?>());
            }

            if (specialistThrows)
            {
                trace.Events.Add("specialist throws");
                throw new HttpRequestException("remote agent unreachable (scripted)");
            }

            var n = Interlocked.Increment(ref trace.SpecialistAnswers);
            trace.Events.Add($"specialist answers #{n}");
            return Text($"SPECIALIST-ANSWER-{n}");
        }

        ChatResponse Text(string text) => new(new ChatMessage(ChatRole.Assistant, text))
        {
            ModelId = chat.ModelId,
            Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 },
        };

        ChatResponse Call(string name, IDictionary<string, object?> args) =>
            new(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent(Guid.NewGuid().ToString("N"), name, args)]))
            {
                ModelId = chat.ModelId,
                Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 },
            };
    }

    [Fact]
    public async Task The_first_delegated_turn_returns_the_specialists_answer()
    {
        // The control: if this one fails the fake is wrong, not the library.
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false);
        var trace = Script(host.Chat!);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var error = await host.RunTurnAsync("where is order 7", deadline.Token);

        error.Should().BeNull();
        trace.SpecialistAnswers.Should().Be(1, "the workflow has to reach a specialist. Trace:{0}{1}",
            Environment.NewLine, trace);
        trace.ToString().Should().Contain("coordinator received:").And.Contain("SPECIALIST-ANSWER-1",
            "the tool's result is the specialist's answer, and the coordinator has to be handed it");
    }

    [Fact]
    public async Task A_second_delegated_turn_in_the_same_session_reaches_a_specialist_again()
    {
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false);
        var trace = Script(host.Chat!);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await host.RunTurnAsync("where is order 7", deadline.Token);
        await host.RunTurnAsync("and what about order 8", deadline.Token);

        trace.SpecialistAnswers.Should().Be(2,
            "delegation is not a once-per-conversation capability. Trace:{0}{1}{0}Warnings:{0}{2}",
            Environment.NewLine, trace, host.Logs.WarningText);
        trace.ToString().Should().Contain("SPECIALIST-ANSWER-2",
            "the second answer has to travel back to the coordinator like the first");
    }

    // ── BUG-076 · what the workflow's own router is told ─────────────────────────

    [Fact]
    public async Task The_router_inside_the_workflow_is_not_told_to_call_a_tool_it_does_not_have()
    {
        // Found live on the published rc.7. BUG-073's fix taught the coordinator's prompt to say
        // "to delegate, call route_to_specialist" — and the workflow's entry agent was built from
        // that same prompt. Inside the workflow there is no route_to_specialist: the only way forward
        // is a handoff_to_* function. Told to call a tool it could not see, the router did what
        // BUG-073 describes one level down: it answered "Sto chiedendo allo specialista spedizioni…"
        // in prose, the workflow ended on that sentence, and the coordinator relayed it.
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false);
        var trace = Script(host.Chat!);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await host.RunTurnAsync("where is order 7", deadline.Token);

        trace.TriageInstructions.Should().NotBeEmpty("the router has to have been consulted. Trace:{0}{1}",
            Environment.NewLine, trace);
        var instructions = trace.TriageInstructions[0];
        instructions.Should().NotContain("route_to_specialist",
            "the router is *inside* that tool; naming it sends the model looking for a function it was not given");
        instructions.Should().NotContain("navigate_to",
            "the router has no application tools at all — a prompt that lists them invites it to answer instead of routing");
    }

    [Fact]
    public async Task The_router_is_told_to_route_and_never_to_answer()
    {
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false);
        var trace = Script(host.Chat!);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await host.RunTurnAsync("where is order 7", deadline.Token);

        var instructions = trace.TriageInstructions.FirstOrDefault() ?? string.Empty;
        instructions.Should().Contain("handoff",
            "the one thing the router can do has to be the one thing its prompt is about");
        instructions.Should().Contain("Never answer the request yourself",
            "a router that may answer will, and its answer is a narration of a handoff that did not happen");
        instructions.Length.Should().BeLessThan(1500,
            "the coordinator's full prompt was being paid for a second time on every delegated turn, "
            + "by an agent with no tools to use any of it");
    }

    [Fact]
    public async Task The_coordinator_is_handed_the_specialists_words_not_a_serialised_response()
    {
        // The tool returned the AgentResponse object, which the function factory serialises:
        // {"messages":[{"authorName":…,"contents":[…]}],"agentId":"handoff-workflow","responseId":…}.
        // The model can dig the answer out of that, but it is paying for the envelope on every
        // delegated turn, and the team tool next door has always returned plain text.
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false);
        var trace = Script(host.Chat!);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await host.RunTurnAsync("where is order 7", deadline.Token);

        var received = string.Join(" | ", trace.Received);
        received.Should().Contain("SPECIALIST-ANSWER-1");
        received.Should().NotContain("responseId").And.NotContain("agentId").And.NotContain("\"contents\"",
            "the result of a delegation is what the specialist said");
    }

    [Fact]
    public async Task The_specialist_the_user_named_travels_to_the_router()
    {
        // Read off a live span: the user wrote "Delega all'agente remoto ShopFlowRemote questa
        // domanda: quanti prodotti ci sono nel suo catalogo?" and the coordinator called the tool
        // with request = "Quanti prodotti ci sono nel catalogo ShopFlow?". A model asked for "the
        // user's request" tidies it, and the name is the first thing to go — so the router chose
        // among descriptions with the one decisive fact removed, and on rc.7 a request addressed to
        // OrderAgent was answered by ShippingAgent. The name needs a parameter of its own.
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false);
        var routerSaw = new List<string>();
        host.Chat!.Respond((messages, options) =>
        {
            var tools = options?.Tools?.Select(t => t.Name).ToList() ?? [];
            if (tools.Contains("route_to_specialist"))
            {
                if (messages.Last().Contents.OfType<FunctionResultContent>().Any())
                    return new ChatResponse(new ChatMessage(ChatRole.Assistant, "relayed"));

                return new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [
                    new FunctionCallContent("c1", "route_to_specialist", new Dictionary<string, object?>
                    {
                        ["request"] = "quanti prodotti ci sono nel catalogo",
                        ["specialist"] = "ShippingAgent",
                    }),
                ]));
            }

            if (tools.Count > 0 && tools.All(t => t.StartsWith("handoff_to", StringComparison.Ordinal)))
            {
                routerSaw.Add(string.Join("\n", messages.Select(m => m.Text)));
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "NO_SPECIALIST"));
            }

            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "SAFE"));
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await host.RunTurnAsync("chiedi allo ShippingAgent quanti prodotti ci sono", deadline.Token);

        var tool = host.Chat.Calls.First(c => c.ToolNames.Contains("route_to_specialist")).Tool("route_to_specialist")!;
        // On the property itself: the word "specialist" is all over the descriptions.
        tool.JsonSchema.GetProperty("properties").TryGetProperty("specialist", out _).Should().BeTrue(
            "the tool has to offer somewhere to put the name. Schema: {0}", tool.JsonSchema);
        routerSaw.Should().NotBeEmpty();
        routerSaw[0].Should().Contain("ShippingAgent",
            "the router can only honour a name it is shown");
        routerSaw[0].Should().Contain("quanti prodotti ci sono nel catalogo", "and the request travels with it");
    }

    [Fact]
    public async Task A_router_that_answers_instead_of_routing_is_reported_as_not_delegated()
    {
        // The exact live failure, scripted: the router replies in prose and the workflow ends on it.
        // Handing that sentence to the coordinator as "the specialist's answer" is how the user was
        // told a request was on its way to someone who never received it. The result has to carry
        // the truth — nothing was delegated — and the operator has to get a line in the log.
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false);
        var trace = Script(host.Chat!, routerNarrates: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await host.RunTurnAsync("quali opzioni di spedizione ho", deadline.Token);

        trace.SpecialistAnswers.Should().Be(0, "this scenario is the one where no specialist runs");
        var received = string.Join(" | ", trace.Received);
        received.Should().StartWith("NOT DELEGATED",
            "the coordinator must not be handed the router's narration as an answer. Trace:{0}{1}",
            Environment.NewLine, trace);
        received.Should().NotContain("Sto chiedendo",
            "the narration is precisely the sentence that must not reach the user as a fact");
        host.Logs.HasWarning("route_to_specialist ended without any specialist answering").Should().BeTrue(
            "a delegation that reached nobody has to be visible to the operator. Warnings:{0}{1}",
            Environment.NewLine, host.Logs.WarningText);
    }

    [Fact]
    public async Task A_specialist_that_fails_is_reported_as_a_failure_not_as_a_refusal()
    {
        // Found live right after the router was fixed: it DID hand off to the remote A2A agent, the
        // call failed, and the workflow folded the exception into an ErrorContent nobody read. From
        // outside that was indistinguishable from "no specialist took it". They are different
        // problems with different owners, and the log has to say which one happened.
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false);
        var trace = Script(host.Chat!, specialistThrows: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await host.RunTurnAsync("where is order 7", deadline.Token);

        trace.ToString().Should().Contain("specialist throws", "the scenario needs the handoff to have happened");
        var received = string.Join(" | ", trace.Received);
        received.Should().StartWith("DELEGATION FAILED",
            "a specialist that raised is not a router that refused. Trace:{0}{1}{0}Warnings:{0}{2}",
            Environment.NewLine, trace, host.Logs.WarningText);
        host.Logs.HasWarning("the specialist it was handed to failed").Should().BeTrue(
            "Warnings:{0}{1}", Environment.NewLine, host.Logs.WarningText);
        host.Logs.HasWarning("ended without any specialist answering").Should().BeFalse(
            "the two outcomes must not be reported as each other");
    }

    [Fact]
    public async Task A_successful_delegation_is_not_reported_as_a_failure()
    {
        // The control for the warning above.
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false);
        Script(host.Chat!);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await host.RunTurnAsync("where is order 7", deadline.Token);

        host.Logs.HasWarning("ended without any specialist answering").Should().BeFalse();
    }

    // ── BUG-077 · who pays for the specialists ───────────────────────────────────

    [Fact]
    public async Task Every_model_call_inside_a_delegated_turn_is_metered_once()
    {
        // The specialists and the router were built on the raw, host-supplied client, so none of
        // their calls reached TokenUsageChatClient. Live, the session counter went from 5 to 6 calls
        // across a delegated turn that made at least three more in between — BUG-033's shape, on the
        // turns that cost the most.
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false);
        var trace = Script(host.Chat!);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await host.RunTurnAsync("where is order 7", deadline.Token);

        trace.SpecialistAnswers.Should().Be(1, "otherwise this measures nothing. Trace:{0}{1}", Environment.NewLine, trace);
        MeteredChatRequests(host).Should().Be(host.Chat!.Calls.Count,
            "a request the provider served was billed, whichever agent inside MentorAgent sent it");
    }

    [Fact]
    public async Task A_specialists_call_is_logged_under_the_deployment_when_the_response_names_no_model()
    {
        // Live, the specialists' new "Tokens —" lines read "model: , in: 404": Azure's first streamed
        // chunk carries an EMPTY model id, not a null one, and `modelId ??= update.ModelId` keeps the
        // empty string for the rest of the stream. The coordinator never showed it because the router
        // in front of it fills the id in (BUG-030); the specialists sit on the bare metering wrapper.
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false);
        host.Chat!.ModelId = "";
        host.Chat.DeploymentId = "gpt-deployment-x";
        Script(host.Chat);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await host.RunTurnAsync("where is order 7", deadline.Token);

        var tokenLines = host.Logs.Records
            .Where(r => r.Message.Contains("Tokens —", StringComparison.Ordinal))
            .Select(r => r.Message)
            .ToList();
        tokenLines.Should().NotBeEmpty();
        string.Join(Environment.NewLine, tokenLines).Should().NotContain("model: ,",
            "a line that names no model cannot be reconciled with the dashboard row it was counted in");
        string.Join(Environment.NewLine, tokenLines).Should().Contain("gpt-deployment-x",
            "the client knows its own deployment even when the response does not say");
    }

    [Fact]
    public async Task Every_model_call_inside_a_team_discussion_is_metered_once()
    {
        // Same construction, same gap: team members talk through the raw client too.
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false);
        host.Chat!
            .CallTool("business_analysis_team", new { task = "how did we do this quarter" })
            .Say("member one [TEAM:APPROVED]");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await host.RunTurnAsync("how did we do this quarter", deadline.Token);

        host.Chat.Calls.Count.Should().BeGreaterThan(2,
            "the coordinator twice plus at least one team member — if not, the team never ran. Warnings:{0}{1}",
            Environment.NewLine, host.Logs.WarningText);
        MeteredChatRequests(host).Should().Be(host.Chat.Calls.Count);
    }

    [Fact]
    public async Task Three_delegations_in_a_row_all_come_back()
    {
        // Two could pass by an accident of state (a run left half-open and resumed once).
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false);
        var trace = Script(host.Chat!);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));

        for (var i = 0; i < 3; i++)
            await host.RunTurnAsync($"question {i}", deadline.Token);

        var received = trace.Events.Where(e => e.StartsWith("coordinator received:", StringComparison.Ordinal)).ToList();
        received.Should().HaveCount(3, "Trace:{0}{1}", Environment.NewLine, trace);
        string.Join(" | ", received).Should()
            .Contain("SPECIALIST-ANSWER-1").And.Contain("SPECIALIST-ANSWER-2").And.Contain("SPECIALIST-ANSWER-3");
    }
}
