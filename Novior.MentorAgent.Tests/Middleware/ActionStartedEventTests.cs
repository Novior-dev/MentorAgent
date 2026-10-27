using FluentAssertions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Xunit;

namespace Novior.MentorAgent.Tests.Middleware;

/// <summary>
/// <see cref="IMentorStateService.OnActionStarted"/>: the tool's technical name next to the text shown.
/// <see cref="IMentorStateService.OnActionExecuting"/> carries only the text shown, the action's description, so whoever
/// recorded the actions from it could not tell for sure which tool ran.
/// </summary>
public sealed class ActionStartedEventTests
{
    [Fact]
    public async Task A_tool_call_raises_the_technical_name_and_the_text_shown()
    {
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false);
        var state = host.Get<IMentorStateService>();
        var started = new List<MentorActionStarted>();
        var executing = new List<string>();
        state.OnActionStarted += started.Add;
        state.OnActionExecuting += executing.Add;
        host.Chat!.CallTool("count_open_orders").Say("Three.");

        (await host.RunTurnAsync("how many open orders?")).Should().BeNull();

        var call = started.Should().ContainSingle(s => s.ToolName == "count_open_orders").Subject;
        call.DisplayName.Should().NotBeNullOrWhiteSpace().And.NotBe("count_open_orders", "the text shown is the description");
        executing.Should().Contain(call.DisplayName, "OnActionExecuting still carries the text shown, unchanged");
    }

    [Fact]
    public void An_implementation_written_before_the_event_still_compiles_and_ignores_it()
    {
        // RecordingStateService implements IMentorStateService and declares no OnActionStarted: the interface's own
        // accessors serve it, and they keep nothing.
        IMentorStateService state = new RecordingStateService();
        var raised = false;

        state.OnActionStarted += _ => raised = true;
        state.NotifyActionExecuting("count_open_orders", "Counts the open orders");
        state.OnActionStarted -= _ => raised = true;

        raised.Should().BeFalse();
    }
}
