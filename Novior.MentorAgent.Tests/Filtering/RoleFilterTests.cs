using FluentAssertions;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.App;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Novior.MentorAgent.Tests.Filtering;

/// <summary>
/// BUG-200 — the coordinator's model was offered, and counted in the prompt, every action of the application, those
/// the user had no role for included: it announced one, and the gate then refused it. It is now offered only what
/// the gate lets this user run, by the MCP server's rule: an action that needs a role travels for a signed-in user
/// who holds one of its roles. Decided for every turn, so a user who signs in, or another user of the same session,
/// is offered what their own roles allow.
///
/// <para>
/// In the test application <c>cancel_order</c> needs the Manager role, <c>export_ledger</c> the Admin role, and
/// <c>count_open_orders</c> none.
/// </para>
/// </summary>
public sealed class RoleFilterTests
{
    /// <summary>The notice of a turn that left an action out, and its sentence for a user who is not signed in.</summary>
    private const string Notice = "Some other actions of this application are NOT available to this user on this turn";
    private const string SignIn = "add that signing in may make it available";

    /// <summary>The notice's first sentence: a tool the model was given is available (the 311 live check).</summary>
    private const string GivenToolsAvailable = "Every tool you were given in this request is available to this user";

    private static IReadOnlyList<FakeChatClient.RecordedCall> CoordinatorCalls(FakeChatClient chat)
        => chat.Calls.Where(c => c.ToolNames.Count > 0).ToList();

    /// <summary>The last section of the request's instructions: the notice goes at the end, after everything else.</summary>
    private static string LastSection(FakeChatClient.RecordedCall call)
    {
        var instructions = call.Instructions ?? "";
        return instructions[Math.Max(0, instructions.LastIndexOf("## ", StringComparison.Ordinal))..];
    }

    private static string PromptOf(FakeChatClient.RecordedCall call) => (call.Instructions ?? "") + call.SystemText;

    /// <summary>
    /// The prompt's count of the direct actions, with <paramref name="leftOut"/> left out: of those the session is built
    /// with, which an action shipped off is not (story 1549).
    /// </summary>
    private static string Count(MentorHostFixture host, params string[] leftOut)
        => $"**{host.Get<MentorDiscoveryService>().Discover().Level1Methods.Count(m => m.EnabledByDefault && !leftOut.Contains(m.ToolName))} direct actions**";

    [Theory]
    [Trait("Rule", "roles.offered-when-allowed")]
    [InlineData(MentorHitlMode.Blocking)]
    [InlineData(MentorHitlMode.Native)]
    public async Task Each_user_is_offered_and_told_only_the_actions_their_roles_allow(MentorHitlMode mode)
    {
        await using var host = MentorHostFixture.Create(o =>
        {
            o.HitlMode = mode;
            o.MemoryAutoCapture = false;
        });
        host.Chat!.Say("Hello.");

        host.Auth.SignIn("ada", "Manager");
        (await host.RunTurnAsync("hello")).Should().BeNull();
        var ada = CoordinatorCalls(host.Chat).Last();

        host.Auth.SignIn("bob", "Admin");
        (await host.RunTurnAsync("hello again")).Should().BeNull();
        var bob = CoordinatorCalls(host.Chat).Last();

        ada.ToolNames.Should().Contain(["cancel_order", "count_open_orders"])
            .And.NotContain("export_ledger", "mode {0}: ada is no Admin", mode);
        PromptOf(ada).Should().Contain(Count(host, "export_ledger")).And.NotContain(Count(host));

        bob.ToolNames.Should().Contain(["export_ledger", "count_open_orders"])
            .And.NotContain("cancel_order", "mode {0}: bob is no Manager, in the same session", mode);
        PromptOf(bob).Should().Contain(Count(host, "cancel_order")).And.NotContain(Count(host));
    }

    [Fact]
    [Trait("Rule", "roles.signed-in")]
    [Trait("Rule", "roles.offered-when-allowed")]
    public async Task A_user_who_is_not_signed_in_is_offered_no_action_that_needs_a_role()
    {
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false);
        host.Chat!.Say("Hello.");

        (await host.RunTurnAsync("hello")).Should().BeNull();

        var call = CoordinatorCalls(host.Chat).Single();
        call.ToolNames.Should().Contain("count_open_orders").And.NotContain(["cancel_order", "export_ledger"]);
        PromptOf(call).Should().Contain(Count(host, "cancel_order", "export_ledger"));
    }

    [Fact]
    [Trait("Rule", "roles.offered-when-allowed")]
    public async Task A_user_who_holds_every_role_is_offered_every_action_and_the_prompt_as_built()
    {
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false);
        host.Auth.SignIn("ada", "Manager", "Admin");
        host.Chat!.Say("Hello.");

        (await host.RunTurnAsync("hello")).Should().BeNull();

        var call = CoordinatorCalls(host.Chat).Single();
        call.ToolNames.Should().Contain(["cancel_order", "export_ledger", "count_open_orders"]);
        PromptOf(call).Should().Contain(Count(host));
        PromptOf(call).Should().NotContain(Notice, "nothing was left out, and the cached prefix stays as it is");
    }

    // ── The notice ──────────────────────────────────────────────────────────────
    // With an action taken away for the turn the model can claim it anyway — "Stock aggiornato" with no tool
    // called (BUG-118), an invented capability (the withheld hosted tools). So the turn says so, in the
    // instructions of every request and at their end: never as a message, which a service-managed history
    // would store and carry into later turns. It names no action and no role.

    [Theory]
    [InlineData(MentorHitlMode.Blocking)]
    [InlineData(MentorHitlMode.Native)]
    public async Task A_turn_that_leaves_an_action_out_says_so_at_the_end_of_the_instructions(MentorHitlMode mode)
    {
        await using var host = MentorHostFixture.Create(o =>
        {
            o.HitlMode = mode;
            o.MemoryAutoCapture = false;
        });
        host.Auth.SignIn("ada", "Manager");
        host.Chat!.Say("I cannot do that here.");

        (await host.RunTurnAsync("export the ledger")).Should().BeNull();

        var call = CoordinatorCalls(host.Chat).Single();
        LastSection(call).Should().Contain(Notice, "mode {0}: at the end, after everything else", mode);
        LastSection(call).Should().Contain(GivenToolsAvailable,
            "read live, 'some actions are NOT available' alone made the model refuse a note it had the tool for");
        LastSection(call).Should().Contain("NEVER write that an action has been carried out");
        call.Instructions.Should().Contain("## Available Capabilities", "it is added, nothing is replaced");
        call.Instructions.Should().NotContain(SignIn, "ada is signed in");
        LastSection(call).Should().NotContain("export_ledger").And.NotContain("Admin",
            "the notice names no action and no role");
        call.Messages.Should().NotContain(m => m.Text.Contains(Notice), "it is never a message");
    }

    [Fact]
    public async Task A_user_who_is_not_signed_in_is_told_that_signing_in_may_help()
    {
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false);
        host.Chat!.Say("Please sign in.");

        (await host.RunTurnAsync("cancel order 5")).Should().BeNull();

        var last = LastSection(CoordinatorCalls(host.Chat).Single());
        last.Should().Contain(Notice).And.Contain(SignIn);
        last.Should().Contain("never when a tool you called refused or stopped it",
            "live, the sentence followed a policy's Stop: it is only for an action the model was not given");
        last.Should().NotContain("Manager").And.NotContain("cancel_order");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_task_from_another_agent_is_never_told_that_signing_in_may_help(bool byOrigin)
    {
        // Nobody can sign in on an A2A task: live, the caller was told to sign in.
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false);
        if (byOrigin) host.Get<MentorTurnOrigin>().FromAnotherAgent = true;
        else host.Get<MentorTurnContext>().Surface = MentorActionSurface.A2A;
        host.Chat!.Say("Not here.");

        (await host.RunTurnAsync("cancel order 5")).Should().BeNull();

        var last = LastSection(CoordinatorCalls(host.Chat).Single());
        last.Should().Contain(Notice, "an action is still left out for its roles");
        last.Should().NotContain(SignIn);
    }

    [Fact]
    public async Task An_action_only_a_policy_left_out_never_suggests_signing_in()
    {
        // No action of this application needs a role: what is left out, a policy left out, and signing in opens nothing.
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false, s =>
        {
            s.RemoveAll<TestOrderService>();
            s.AddMentorActionPolicy(new TestActionPolicy { Allowed = r => r.ToolName != "get_weather" });
        });
        host.Chat!.Say("Not here.");

        (await host.RunTurnAsync("what is the weather in Rome?")).Should().BeNull();

        var call = CoordinatorCalls(host.Chat).Single();
        call.ToolNames.Should().NotContain("get_weather");
        LastSection(call).Should().Contain(Notice).And.NotContain(SignIn);
    }

    [Fact]
    public async Task An_application_whose_actions_need_no_role_gets_no_notice()
    {
        // Without the test application's order service the coordinator offers no action that needs a role.
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false,
            s => s.RemoveAll<TestOrderService>());
        host.Chat!.Say("Hello.");

        (await host.RunTurnAsync("hello")).Should().BeNull();

        var call = CoordinatorCalls(host.Chat).Single();
        call.ToolNames.Should().NotContain("cancel_order");
        PromptOf(call).Should().NotContain(Notice).And.NotContain(SignIn);
    }

    [Fact]
    public async Task On_path_B_the_notice_ends_the_instructions_after_the_hosts_own()
    {
        var inner = new FakeChatClient();
        await using var host = MentorHostFixture.Create(
            o =>
            {
                o.ChatClient = null;
                o.Agent = inner.AsAIAgent(instructions: "You are a test agent.");
                o.MemoryAutoCapture = false;
            },
            withChatClient: false);
        inner.Say("Please sign in.");

        (await host.RunTurnAsync("cancel order 5")).Should().BeNull();

        var call = CoordinatorCalls(inner).Single();
        call.Instructions.Should().StartWith("You are a test agent.", "the host's persona is untouched");
        LastSection(call).Should().Contain(Notice).And.Contain(SignIn);
        call.Messages.Should().NotContain(m => m.Text.Contains(Notice));
    }

    [Fact]
    [Trait("Rule", "roles.offered-when-allowed")]
    public async Task On_path_B_the_agent_is_handed_only_the_actions_the_user_may_run()
    {
        var inner = new FakeChatClient();
        await using var host = MentorHostFixture.Create(
            o =>
            {
                o.ChatClient = null;
                o.Agent = inner.AsAIAgent(instructions: "You are a test agent.");
                o.MemoryAutoCapture = false;
            },
            withChatClient: false);
        inner.Say("Hello.");

        host.Auth.SignIn("ada", "Manager");
        (await host.RunTurnAsync("hello")).Should().BeNull();
        host.Auth.SignIn("bob", "Admin");
        (await host.RunTurnAsync("hello again")).Should().BeNull();

        var calls = CoordinatorCalls(inner);
        calls[0].ToolNames.Should().Contain("cancel_order").And.NotContain("export_ledger");
        calls[^1].ToolNames.Should().Contain("export_ledger").And.NotContain("cancel_order");
    }

    [Fact]
    [Trait("Rule", "roles.offered-when-allowed")]
    public async Task A_call_to_an_action_left_out_is_still_refused_by_the_gate()
    {
        // The model can still name an action it was not offered — one it saw called earlier in the conversation, or
        // made up. The function-invoking client finds it among the agent's tools, and the gate refuses it as before.
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false);
        host.Auth.SignIn("ada", "Manager");
        host.Chat!.CallTool("export_ledger").Say("You may not export the ledger.");

        (await host.RunTurnAsync("export the ledger")).Should().BeNull();

        CoordinatorCalls(host.Chat)[0].ToolNames.Should().NotContain("export_ledger");
        host.Get<TestOrderService>().Invoked.Should().NotContain(nameof(TestOrderService.ExportLedger));
    }
}
