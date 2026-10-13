using FluentAssertions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Tests.App;
using Novior.MentorAgent.Tests.Harness;
using Novior.MentorAgent.Utility;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Novior.MentorAgent.Tests.Levels;

/// <summary>
/// BUG-144 — a confirmation declined inside a delegated request has to reach the coordinator as a decision.
///
/// <para>
/// Seen on a test app on rc.14: a team with <c>HandoffTo</c> approved closing a case, the coordinator handed it
/// to the specialist, the specialist's action asked for confirmation and the user pressed Cancel. The final
/// answer was "there was a problem closing the case… shall I try again?". The specialist had been given the
/// refusal with its instruction (BUG-031, BUG-115); <c>route_to_specialist</c> gave the coordinator only what the
/// specialists said, and the team's result had just told it to execute. An action that did not run read as a
/// failure. The decision now travels in the delegation's result, with the same rule as the tool result, and
/// names what was declined and what, if anything, ran beside it.
/// </para>
/// </summary>
public sealed class DelegatedDeclineTests
{
    private static readonly string Declined = MentorUtils.ToolDeclined("Cancelled");

    private static string Result(Func<string, bool>? isReadOnly, params ChatMessage[] messages)
        => MentorOrchestrator.DelegationResult(new AgentResponse([.. messages]), NullLogger.Instance,
            requestedSpecialist: null, remoteAgentNames: [], isReadOnly);

    private static string Result(params ChatMessage[] messages) => Result(null, messages);

    private static ChatMessage Router() => new(ChatRole.Assistant, [new FunctionCallContent("h1", "handoff_to_1")])
    {
        AuthorName = MentorPromptBuilder.HandoffRouterName,
    };

    private static ChatMessage Call(string callId, string tool) => new(ChatRole.Assistant,
        [new FunctionCallContent(callId, tool, new Dictionary<string, object?> { ["id"] = 3 })])
    {
        AuthorName = "CustomerAgent",
    };

    private static ChatMessage ToolResult(string callId, object result)
        => new(ChatRole.Tool, [new FunctionResultContent(callId, result)]);

    private static ChatMessage Says(string text) => new(ChatRole.Assistant, text) { AuthorName = "CustomerAgent" };

    // ── One action, declined ────────────────────────────────────────────────────

    [Fact]
    public void A_declined_action_in_the_delegated_request_reaches_the_coordinator_as_a_decision()
    {
        var result = Result(
            Router(),
            Call("c1", "update_customer_plan"),
            ToolResult("c1", Declined),
            Says("There was a problem updating the plan."));

        result.Should().StartWith("DECLINED BY THE USER:", "the coordinator reads these results by their first words");
        result.Should().Contain("'update_customer_plan'", "the result says which action was declined");
        result.Should().Contain(MentorUtils.DeclinedRule, "the same rule as a declined tool result");
        result.Should().NotContain("problem", "the specialist's words are not relayed: the rule is to say only that it did not run");
    }

    [Fact]
    public void A_declined_action_with_no_words_after_it_is_not_reported_as_not_delegated()
    {
        // A specialist that ends on the refusal says nothing. Without the check that was NOT DELEGATED — "answer it
        // yourself with your own tools" — an invitation to run the action the user had just declined.
        var result = Result(Router(), Call("c1", "update_customer_plan"), ToolResult("c1", Declined));

        result.Should().StartWith("DECLINED BY THE USER:");
        result.Should().NotContain("NOT DELEGATED");
    }

    [Fact]
    public void An_action_that_ran_leaves_the_specialists_answer_as_it_was()
    {
        // The control: only a refusal changes the result.
        var result = Result(
            Router(),
            Call("c1", "update_customer_plan"),
            ToolResult("c1", MentorUtils.ToolSuccess("updated")),
            Says("Customer 3 is now on Premium."));

        result.Should().Be("Customer 3 is now on Premium.");
    }

    // ── Something else ran beside it ────────────────────────────────────────────

    [Fact]
    public void A_write_that_ran_beside_the_declined_one_is_not_reported_as_nothing_changed()
    {
        // "Put customer 3 on Premium and order 1002 in Processing": specialists hand requests to each other, so one
        // delegation can carry a yes and a no. "Nothing changed" would then be false about a record — BUG-115's
        // error the other way round.
        var result = Result(
            Router(),
            Call("c1", "update_customer_plan"),
            ToolResult("c1", Declined),
            Call("c2", "update_status"),
            ToolResult("c2", "✅ Ordine #1002 → Processing"),
            Says("I could not change the plan because of a problem."));

        result.Should().StartWith("DECLINED BY THE USER, IN PART:");
        result.Should().Contain("not to run 'update_customer_plan'").And.Contain("'update_status' did run");
        result.Should().Contain("what you did not do and what was done");
        result.Should().NotContain("nothing changed", "one record did change");
        result.Should().NotContain("problem");
    }

    [Fact]
    public void A_read_before_the_declined_action_keeps_the_plain_result()
    {
        // Reading the profile before the declined update changed nothing: with the read declared read-only
        // (ReadOnly, IsReadOnlyAction) the result stays the simple one.
        var result = Result(
            name => name.StartsWith("get_", StringComparison.Ordinal),
            Router(),
            Call("c1", "get_customer"),
            ToolResult("c1", "Mario Rossi — Basic"),
            Call("c2", "update_customer_plan"),
            ToolResult("c2", Declined));

        result.Should().StartWith("DECLINED BY THE USER:");
        result.Should().Contain("nothing changed").And.NotContain("get_customer");
    }

    [Fact]
    public void An_undeclared_read_before_the_declined_action_is_listed_as_done()
    {
        // Not knowing it only reads, the result lists it as done — "I read the profile" is true; "nothing was
        // done" might not be.
        var result = Result(
            Router(),
            Call("c1", "get_customer"),
            ToolResult("c1", "Mario Rossi — Basic"),
            Call("c2", "update_customer_plan"),
            ToolResult("c2", Declined));

        result.Should().StartWith("DECLINED BY THE USER, IN PART:");
        result.Should().Contain("'get_customer' did run");
    }

    [Fact]
    public void An_action_that_failed_beside_the_declined_one_is_not_listed_as_done()
    {
        var result = Result(
            Router(),
            Call("c1", "update_status"),
            ToolResult("c1", MentorUtils.ToolError("order not found")),
            Call("c2", "update_customer_plan"),
            ToolResult("c2", Declined));

        result.Should().StartWith("DECLINED BY THE USER:");
        result.Should().NotContain("update_status");
    }

    // ── The whole path, in process ──────────────────────────────────────────────

    [Fact]
    public async Task Cancel_on_a_specialists_action_reaches_the_coordinator_through_the_handoff_workflow()
    {
        // The real workflow, the real gate: the coordinator delegates, the router hands off, ShippingAgent calls
        // cancel_shipment (RequiresConfirmation, Manager), the banner is answered Cancel. What the coordinator is
        // handed must say the user declined — whatever the specialist says after it.
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false);
        host.Auth.SignIn("ada", "Manager");
        var received = new List<string>();
        host.Chat!.Respond((messages, options) =>
        {
            var tools = options?.Tools?.Select(t => t.Name).ToList() ?? [];
            var toolResult = messages.LastOrDefault()?.Contents.OfType<FunctionResultContent>().LastOrDefault();

            if (tools.Count == 0)
                return Text("SAFE");

            if (tools.Contains("route_to_specialist"))
            {
                if (toolResult is not null)
                {
                    received.Add(toolResult.Result?.ToString() ?? "(null)");
                    return Text("relayed");
                }
                return Invoke("route_to_specialist", new() { ["request"] = "cancel the shipment of order 7" });
            }

            if (tools.All(t => t.StartsWith("handoff_to", StringComparison.Ordinal)))
                return Invoke(tools[0], []);

            if (tools.Contains("cancel_shipment"))
                return toolResult is null
                    ? Invoke("cancel_shipment", new() { ["orderId"] = 7 })
                    : Text("There was a problem cancelling the shipment. Shall I try again?");

            return Text("SAFE");
        });

        var asked = 0;
        var state = host.Get<IMentorStateService>();
        state.OnConfirmationRequired += req =>
        {
            Interlocked.Increment(ref asked);
            state.Cancel(req.ActionId);
        };

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var error = await host.RunTurnAsync("cancel the shipment of order 7", deadline.Token);

        error.Should().BeNull();
        asked.Should().Be(1, "the specialist's action asks before it runs");
        host.Get<TestShippingAgent>().Invoked.Should().NotContain(nameof(TestShippingAgent.CancelShipment));
        received.Should().ContainSingle().Which.Should().StartWith("DECLINED BY THE USER:",
            "the coordinator must be told the user declined, not handed the specialist's words. Warnings: {0}",
            host.Logs.WarningText);
        received[0].Should().Contain("'cancel_shipment'");

        ChatResponse Text(string text) => new(new ChatMessage(ChatRole.Assistant, text)) { ModelId = host.Chat.ModelId };

        ChatResponse Invoke(string name, Dictionary<string, object?> args) =>
            new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(Guid.NewGuid().ToString("N"), name, args)]))
            {
                ModelId = host.Chat.ModelId,
            };
    }
}
