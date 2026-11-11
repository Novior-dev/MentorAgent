using FluentAssertions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.App;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Gating;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Novior.MentorAgent.Tests.Filtering;

/// <summary>
/// BUG-205 — the coordinator's model is offered only the actions this turn's user may run (BUG-200), and the agents
/// under it were not: a specialist, a team member and an agent of an <see cref="IMentorAgentSource"/> were offered
/// every tool they hold, those the user had no role for included, and a team member's instructions listed them with
/// their descriptions. The model could propose or call one, and the gate then refused it. They are now offered, and
/// told, what the gate lets this user run, decided from the same reading of the user as the coordinator's, with the
/// same notice when something is left out.
///
/// <para>
/// In the test application the specialist <c>ShippingAgent</c> holds <c>get_tracking_status</c> and
/// <c>cancel_shipment</c> (Manager); the team member Data Analyst holds <c>read_quarter_revenue</c> and
/// <c>approve_budget_line</c> (Manager), the Strategist the expense skill's tools, which need no role; the
/// coordinator's <c>cancel_order</c> needs the Manager role and <c>export_ledger</c> the Admin role.
/// </para>
/// </summary>
public sealed class AgentRoleFilterTests
{
    private const string Notice = "Some other actions of this application are NOT available to this user on this turn";
    private const string SignIn = "add that signing in may make it available";

    private static string LastSection(FakeChatClient.RecordedCall call)
    {
        var instructions = call.Instructions ?? "";
        return instructions[Math.Max(0, instructions.LastIndexOf("## ", StringComparison.Ordinal))..];
    }

    private static ChatResponse Text(MentorHostFixture host, string text)
        => new(new ChatMessage(ChatRole.Assistant, text)) { ModelId = host.Chat!.ModelId };

    private static ChatResponse Invoke(MentorHostFixture host, string name, Dictionary<string, object?> args)
        => new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(Guid.NewGuid().ToString("N"), name, args)]))
        {
            ModelId = host.Chat!.ModelId,
        };

    // ── Specialists ──────────────────────────────────────────────────────────────

    /// <summary>The specialist's requests: the only ones that carry its own read.</summary>
    private static List<FakeChatClient.RecordedCall> SpecialistCalls(MentorHostFixture host, int from = 0)
        => host.Chat!.Calls.Skip(from).Where(c => c.ToolNames.Contains("get_tracking_status")).ToList();

    private static Func<IReadOnlyList<ChatMessage>, ChatOptions?, ChatResponse> TrackOrder(MentorHostFixture host)
        => ActionDecisionTests.Delegating(host, "where is order 7?", "shipping", "get_tracking_status",
            new() { ["orderId"] = 7 });

    [Theory]
    [Trait("Rule", "roles.offered-when-allowed")]
    [InlineData(MentorHitlMode.Blocking)]
    [InlineData(MentorHitlMode.Native)]
    public async Task Each_user_is_offered_only_the_specialists_actions_their_roles_allow(MentorHitlMode mode)
    {
        await using var host = MentorHostFixture.Create(o =>
        {
            o.HitlMode = mode;
            o.MemoryAutoCapture = false;
        });
        host.Chat!.Respond(TrackOrder(host));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        host.Auth.SignIn("ada", "Manager");
        (await host.RunTurnAsync("where is order 7?", deadline.Token)).Should().BeNull();
        var ada = SpecialistCalls(host);

        var from = host.Chat.CallCount;
        host.Auth.SignIn("bob", "Admin");
        (await host.RunTurnAsync("where is order 7?", deadline.Token)).Should().BeNull();
        var bob = SpecialistCalls(host, from);

        ada.Should().NotBeEmpty("the specialist ran. Warnings: {0}", host.Logs.WarningText);
        ada.Should().OnlyContain(c => c.ToolNames.Contains("cancel_shipment"), "mode {0}: ada is a Manager", mode);
        ada.Should().NotContain(c => (c.Instructions ?? "").Contains(Notice), "nothing of the specialist's was left out");

        bob.Should().NotBeEmpty();
        bob.Should().OnlyContain(c => !c.ToolNames.Contains("cancel_shipment"),
            "mode {0}: bob is no Manager, in the same session", mode);
        bob.Should().OnlyContain(c => LastSection(c).Contains(Notice), "the specialist is told, as the coordinator is");
        bob.Should().OnlyContain(c => !LastSection(c).Contains(SignIn), "bob is signed in");
        bob.Should().OnlyContain(c => !LastSection(c).Contains("cancel_shipment") && !LastSection(c).Contains("Manager"),
            "the notice names no action and no role");
        bob.Should().OnlyContain(c => !c.Messages.Any(m => m.Text.Contains(Notice)), "it is never a message");
    }

    [Fact]
    public async Task A_user_who_is_not_signed_in_is_told_by_the_specialist_that_signing_in_may_help()
    {
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false);
        host.Chat!.Respond(TrackOrder(host));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        (await host.RunTurnAsync("where is order 7?", deadline.Token)).Should().BeNull();

        var calls = SpecialistCalls(host);
        calls.Should().NotBeEmpty("the specialist ran. Warnings: {0}", host.Logs.WarningText);
        calls.Should().OnlyContain(c => !c.ToolNames.Contains("cancel_shipment"));
        calls.Should().OnlyContain(c => LastSection(c).Contains(Notice) && LastSection(c).Contains(SignIn));
    }

    [Fact]
    public async Task A_specialists_action_is_left_out_also_where_the_coordinator_holds_none_that_needs_a_role()
    {
        // Without the order service no action of the coordinator needs a role: the turn reads the user for the
        // specialist's alone.
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false,
            s => s.RemoveAll<TestOrderService>());
        host.Chat!.Respond(TrackOrder(host));
        host.Auth.SignIn("bob", "Admin");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        (await host.RunTurnAsync("where is order 7?", deadline.Token)).Should().BeNull();

        var calls = SpecialistCalls(host);
        calls.Should().NotBeEmpty("the specialist ran. Warnings: {0}", host.Logs.WarningText);
        calls.Should().OnlyContain(c => !c.ToolNames.Contains("cancel_shipment") && LastSection(c).Contains(Notice));
    }

    [Fact]
    public async Task A_task_from_another_agent_is_never_told_by_the_specialist_that_signing_in_may_help()
    {
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false);
        host.Get<MentorTurnContext>().Surface = MentorActionSurface.A2A;
        host.Chat!.Respond(TrackOrder(host));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        (await host.RunTurnAsync("where is order 7?", deadline.Token)).Should().BeNull();

        var calls = SpecialistCalls(host);
        calls.Should().NotBeEmpty("the specialist ran. Warnings: {0}", host.Logs.WarningText);
        calls.Should().OnlyContain(c => LastSection(c).Contains(Notice) && !LastSection(c).Contains(SignIn),
            "nobody can sign in on a task from another agent");
    }

    [Fact]
    public async Task A_call_to_a_specialists_action_left_out_is_still_refused_by_the_gate()
    {
        // The specialist's model names an action it was not offered: its function-invoking client still holds the
        // tool, and the gate refuses it before any banner, as before.
        var observer = new RecordingActionObserver();
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false,
            s => s.AddMentorActionObserver(observer));
        host.Auth.SignIn("bob", "Admin");
        var delegating = TrackOrder(host);
        host.Chat!.Respond((messages, options) =>
            options?.Tools?.Any(t => t.Name == "get_tracking_status") == true
            && messages.LastOrDefault()?.Contents.OfType<FunctionResultContent>().Any() != true
                ? Invoke(host, "cancel_shipment", new() { ["orderId"] = 7 })
                : delegating(messages, options));
        var asked = 0;
        host.Get<IMentorStateService>().OnConfirmationRequired += _ => Interlocked.Increment(ref asked);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        (await host.RunTurnAsync("cancel the shipment of order 7", deadline.Token)).Should().BeNull();
        await host.Get<MentorActionDecisions>().FlushAsync();

        host.Get<TestShippingAgent>().Invoked.Should().BeEmpty();
        asked.Should().Be(0);
        observer.Steps("cancel_shipment").Should().Equal("Denied/role-missing");
    }

    // ── Team members ─────────────────────────────────────────────────────────────

    private static bool Is(FakeChatClient.RecordedCall call, string role)
        => (call.Instructions ?? "").Contains($"Your role in the team is: **{role}**", StringComparison.Ordinal);

    /// <summary>The coordinator asks the team; the Data Analyst speaks, then the Strategist approves.</summary>
    private static Func<IReadOnlyList<ChatMessage>, ChatOptions?, ChatResponse> AskTheTeam(MentorHostFixture host)
        => (messages, options) =>
        {
            var instructions = options?.Instructions ?? "";
            var tools = options?.Tools?.Select(t => t.Name).ToList() ?? [];
            var answered = messages.LastOrDefault()?.Contents.OfType<FunctionResultContent>().Any() == true;

            if (instructions.Contains("Your role in the team is: **Data Analyst**", StringComparison.Ordinal))
                return Text(host, "Revenue is steady.");
            if (instructions.Contains("Your role in the team is: **Strategist**", StringComparison.Ordinal))
                return Text(host, "Keep the budget. [TEAM:APPROVED]");
            if (tools.Contains("business_analysis_team"))
                return answered
                    ? Text(host, "relayed")
                    : Invoke(host, "business_analysis_team", new() { ["task"] = "review the quarter" });
            return Text(host, "SAFE");
        };

    [Theory]
    [Trait("Rule", "roles.offered-when-allowed")]
    [InlineData(MentorHitlMode.Blocking)]
    [InlineData(MentorHitlMode.Native)]
    public async Task Each_team_member_is_offered_and_told_only_the_tools_the_users_roles_allow(MentorHitlMode mode)
    {
        await using var host = MentorHostFixture.Create(o =>
        {
            o.HitlMode = mode;
            o.MemoryAutoCapture = false;
        });
        host.Chat!.Respond(AskTheTeam(host));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        host.Auth.SignIn("ada", "Manager");
        (await host.RunTurnAsync("review the quarter", deadline.Token)).Should().BeNull();
        var from = host.Chat.CallCount;
        host.Auth.SignIn("bob", "Admin");
        (await host.RunTurnAsync("review the quarter", deadline.Token)).Should().BeNull();

        var ada = host.Chat.Calls.Take(from).Where(c => Is(c, "Data Analyst")).ToList();
        var bob = host.Chat.Calls.Skip(from).Where(c => Is(c, "Data Analyst")).ToList();

        ada.Should().NotBeEmpty("the team ran. Warnings: {0}", host.Logs.WarningText);
        ada.Should().OnlyContain(c => c.ToolNames.Contains("approve_budget_line")
                                      && (c.Instructions ?? "").Contains("**approve_budget_line**"),
            "mode {0}: ada is a Manager", mode);
        ada.Should().NotContain(c => (c.Instructions ?? "").Contains(Notice));

        bob.Should().NotBeEmpty();
        bob.Should().OnlyContain(c => c.ToolNames.Contains("read_quarter_revenue")
                                      && !c.ToolNames.Contains("approve_budget_line"),
            "mode {0}: bob is no Manager", mode);
        bob.Should().OnlyContain(c => !(c.Instructions ?? "").Contains("approve_budget_line")
                                      && !(c.Instructions ?? "").Contains("Approves a budget line"),
            "the member's instructions listed the tool with its description");
        bob.Should().OnlyContain(c => (c.Instructions ?? "").Contains("**read_quarter_revenue**"),
            "the tools bob may run are still listed");
        bob.Should().OnlyContain(c => LastSection(c).Contains(Notice));

        host.Chat.Calls.Where(c => Is(c, "Strategist")).Should().NotBeEmpty()
            .And.OnlyContain(c => !(c.Instructions ?? "").Contains(Notice),
                "nothing of the Strategist's needs a role: its prompt is as built");
    }

    // ── Agents of an IMentorAgentSource ──────────────────────────────────────────

    /// <summary>A source that hands back one agent with the tools it is given, the way Novior.MentorAgent.Declarative does.</summary>
    private sealed class LedgerAgentSource : IMentorAgentSource
    {
        public Task<IReadOnlyList<AIAgent>> GetAgentsAsync(MentorAgentSourceContext context, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AIAgent>>(
            [
                new ChatClientAgent(context.ChatClient, new ChatClientAgentOptions
                {
                    Name = "LedgerAgent",
                    Description = "Keeps the order ledger",
                    ChatOptions = new ChatOptions
                    {
                        Instructions = "You are the ledger specialist.",
                        Tools = [.. context.Tools],
                    },
                }),
            ]);
    }

    [Theory]
    [InlineData(MentorHitlMode.Blocking)]
    [InlineData(MentorHitlMode.Native)]
    public async Task Each_user_is_offered_only_the_tools_of_an_agent_source_their_roles_allow(MentorHitlMode mode)
    {
        await using var host = MentorHostFixture.Create(o =>
        {
            o.HitlMode = mode;
            o.MemoryAutoCapture = false;
        }, s => s.AddSingleton<IMentorAgentSource>(new LedgerAgentSource()));
        host.Chat!.Respond(ActionDecisionTests.Delegating(host, "how many open orders?", "ledger", "count_open_orders", []));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        host.Auth.SignIn("ada", "Manager");
        (await host.RunTurnAsync("how many open orders?", deadline.Token)).Should().BeNull();
        var from = host.Chat.CallCount;
        host.Auth.SignIn("bob", "Admin");
        (await host.RunTurnAsync("how many open orders?", deadline.Token)).Should().BeNull();

        static bool OfTheAgent(FakeChatClient.RecordedCall c)
            => (c.Instructions ?? "").Contains("You are the ledger specialist.", StringComparison.Ordinal);
        var ada = host.Chat.Calls.Take(from).Where(OfTheAgent).ToList();
        var bob = host.Chat.Calls.Skip(from).Where(OfTheAgent).ToList();

        ada.Should().NotBeEmpty("the source's agent ran. Warnings: {0}", host.Logs.WarningText);
        ada.Should().OnlyContain(c => c.ToolNames.Contains("cancel_order") && !c.ToolNames.Contains("export_ledger"),
            "mode {0}: ada is a Manager and no Admin", mode);
        bob.Should().NotBeEmpty();
        bob.Should().OnlyContain(c => c.ToolNames.Contains("export_ledger") && !c.ToolNames.Contains("cancel_order"),
            "mode {0}: bob is an Admin and no Manager", mode);
        ada.Concat(bob).Should().OnlyContain(c => LastSection(c).Contains(Notice));
    }

    // ── An application whose agents need no role ─────────────────────────────────

    [Fact]
    public async Task An_application_whose_actions_need_no_role_reads_nobody_and_keeps_every_prompt_as_built()
    {
        // Without the order service, the specialist and the analyst's tools, no action of the test application needs a
        // role: the team still runs, its Strategist with the expense skill's tools.
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false, s =>
        {
            s.RemoveAll<TestOrderService>();
            s.RemoveAll<TestShippingAgent>();
            s.RemoveAll<TestAnalysisTools>();
        });
        host.Chat!.Respond(AskTheTeam(host));
        host.Auth.SignIn("ada");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        (await host.RunTurnAsync("review the quarter", deadline.Token)).Should().BeNull();

        var strategist = host.Chat.Calls.Where(c => Is(c, "Strategist")).ToList();
        strategist.Should().NotBeEmpty("the team ran. Warnings: {0}", host.Logs.WarningText);
        var member = host.Get<MentorDiscoveryService>().Discover().Level3Teams
            .SelectMany(t => t.Members).Single(m => m.Attribute.Role == "Strategist");
        var built = host.Get<MentorPromptBuilder>().BuildTeamMemberInstructions(member, MentorLanguage.English);
        strategist.Should().OnlyContain(c => c.Instructions == built, "the member's instructions, unchanged");
        host.Chat.Calls.Should().NotContain(c => (c.Instructions ?? "").Contains(Notice));
        var turn = host.Get<MentorTurnContext>();
        turn.AgentsHoldRoleGatedTools.Should().BeFalse("no agent holds a tool that needs a role");
        turn.OfferDecided.Should().BeFalse("the turn read nobody to decide what to offer");
    }
}
