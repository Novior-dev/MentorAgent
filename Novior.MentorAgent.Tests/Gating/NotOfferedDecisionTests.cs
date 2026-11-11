using System.Diagnostics;
using FluentAssertions;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.App;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.AI;
using Xunit;

namespace Novior.MentorAgent.Tests.Gating;

/// <summary>
/// Story 1600 — a call to an action of the application that the coordinator was not given in the turn (one that is off,
/// or another agent's) reaches no gate: the agent answers "not found" and nothing runs. Its one decision is Denied,
/// <see cref="MentorActionReasons.NotOffered"/>, so an audit of the actions sees the call the model made. A call the
/// coordinator was given keeps the decisions of its gate, and only those.
/// </summary>
public sealed class NotOfferedDecisionTests
{
    private static MentorHostFixture Create(RecordingActionObserver observer, Action<MentorOptions>? configure = null)
        => MentorHostFixture.Create(o =>
        {
            o.MemoryAutoCapture = false;
            configure?.Invoke(o);
        }, s => s.AddMentorActionObserver(observer));

    private static Task FlushAsync(MentorHostFixture host) => host.Get<MentorActionDecisions>().FlushAsync();

    private static ChatResponse ToolCall(MentorHostFixture host, string callId, string tool, Dictionary<string, object?>? args = null)
        => new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(callId, tool, args)])) { ModelId = host.Chat!.ModelId };

    [Theory]
    [InlineData(MentorHitlMode.Blocking)]
    [InlineData(MentorHitlMode.Native)]
    public async Task A_call_to_an_action_that_is_off_is_denied_as_not_offered(MentorHitlMode mode)
    {
        // archive_order ships off ([MentorAction(Enabled = false)]): the coordinator is not given it, and answers "not found".
        var observer = new RecordingActionObserver();
        await using var host = Create(observer, o => o.HitlMode = mode);
        host.Auth.SignIn("ada");
        host.Chat!.Respond((_, _) => ToolCall(host, "call-9", "archive_order", new() { ["orderId"] = 3 }))
            .Say("It was not done.");

        (await host.RunTurnAsync("archive order 3")).Should().BeNull();
        await FlushAsync(host);

        host.Get<TestOrderService>().Invoked.Should().BeEmpty();
        observer.Steps("archive_order").Should().Equal("Denied/not-offered");
        var decision = observer.Seen.Single(d => d.ToolName == "archive_order");
        decision.CallId.Should().Be("call-9", "the model's id for the call");
        decision.AgentKind.Should().Be(MentorAgentKind.Coordinator);
        decision.Surface.Should().Be(MentorActionSurface.InProcess);
        decision.TurnId.Should().NotBeNullOrEmpty();
        decision.User!.Identity!.Name.Should().Be("ada", "the user is read as for any other decision");
        decision.DisplayName.Should().NotBeNullOrWhiteSpace().And.NotBe("archive_order", "the text shown is the description");
        decision.RequiredRoles.Should().BeEmpty();
        decision.ExceptionType.Should().BeNull();
    }

    [Fact]
    public async Task A_call_to_another_agents_action_is_denied_as_not_offered()
    {
        // get_tracking_status is the shipping specialist's: the coordinator reaches it only by handing the turn over.
        var observer = new RecordingActionObserver();
        await using var host = Create(observer);
        host.Chat!.Respond((_, _) => ToolCall(host, "call-4", "get_tracking_status", new() { ["orderId"] = 4 }))
            .Say("I could not look it up.");

        (await host.RunTurnAsync("where is order 4?")).Should().BeNull();
        await FlushAsync(host);

        host.Get<TestShippingAgent>().Invoked.Should().BeEmpty();
        observer.Steps("get_tracking_status").Should().Equal("Denied/not-offered");
    }

    [Fact]
    public async Task The_call_has_one_span_that_says_denied_not_offered()
    {
        var source = "MentorAgent.Tests.NotOffered." + Guid.NewGuid().ToString("N");
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == source,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => { lock (spans) spans.Add(a); },
        };
        ActivitySource.AddActivityListener(listener);

        var observer = new RecordingActionObserver();
        await using var host = Create(observer, o =>
        {
            o.EnableObservability = true;
            o.ObservabilitySourceName = source;
        });
        host.Chat!.CallTool("archive_order", new { orderId = 3 }).Say("It was not done.");

        (await host.RunTurnAsync("archive order 3")).Should().BeNull();

        List<Activity> tools;
        lock (spans) tools = [.. spans.Where(s => s.OperationName == "mentor.tool.archive_order")];
        var span = tools.Should().ContainSingle().Subject;
        span.GetTagItem("mentoragent.tool.outcome").Should().Be("denied");
        span.GetTagItem("mentoragent.tool.reason").Should().Be("not-offered");
    }

    [Fact]
    public async Task A_call_the_coordinator_was_given_keeps_the_decisions_of_its_gate_and_only_those()
    {
        // count_open_orders runs; export_ledger is the coordinator's, and its gate refuses it to a user not signed in.
        var observer = new RecordingActionObserver();
        await using var host = Create(observer);
        host.Chat!.Respond((_, _) => ToolCall(host, "call-1", "count_open_orders"))
            .Respond((_, _) => ToolCall(host, "call-2", "export_ledger"))
            .Say("Three; the ledger needs you to sign in.");

        (await host.RunTurnAsync("how many open orders? and export the ledger")).Should().BeNull();
        await FlushAsync(host);

        observer.Steps("count_open_orders").Should().Equal("Allowed/allowed", "Executed/completed");
        observer.Steps("export_ledger").Should().Equal("Denied/login-required");
        observer.Seen.Should().NotContain(d => d.ReasonCode == MentorActionReasons.NotOffered);
        host.Logs.Records.Should().NotContain(r => r.Message.Contains("not among the tools of this turn"));
    }

    [Fact]
    public async Task A_name_no_action_of_the_application_has_gets_no_decision()
    {
        var observer = new RecordingActionObserver();
        await using var host = Create(observer);
        host.Chat!.CallTool("launch_rockets").Say("There is nothing to launch.");

        (await host.RunTurnAsync("launch the rockets")).Should().BeNull();
        await FlushAsync(host);

        observer.Seen.Should().NotContain(d => d.ToolName == "launch_rockets", "no action of the application has that name");
    }
}
