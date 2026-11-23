using FluentAssertions;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Server.Hubs;
using Novior.MentorAgent.Tests.App;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Xunit;

namespace Novior.MentorAgent.Tests.Server;

/// <summary>
/// A hub connection that closes while one of its actions runs: the action's last decision still reaches the observers.
/// From 1.4.0 the decision read the page from the connection's scope, closed by then, and was lost, with the turn: a
/// user who closed the tab during an action left it "allowed" in the observers' record, with no outcome.
/// </summary>
public sealed class ConnectionClosedDecisionTests
{
    [Theory]
    [InlineData(true, "Stopped/session-ended")]   // the action stops on the turn's token
    [InlineData(false, "Failed/exception")]       // the action ends after the connection, and fails
    public async Task An_action_running_when_its_hub_connection_closes_still_gets_its_last_decision(bool stops, string last)
    {
        var observer = new RecordingActionObserver();
        await using var app = await ServerAppFactory.CreateAsync(o => o.MemoryAutoCapture = false,
            configureServices: s => s.AddMentorActionObserver(observer));
        app.Chat.CallTool("failing_action", new { }).Say("Done.");

        await using var connection = app.CreateHubConnection();
        await connection.StartAsync();
        var id = connection.ConnectionId!;
        await connection.InvokeAsync("UpdatePageContext", new PageContextSnapshot { PageName = "Orders" });
        var held = new TaskCompletionSource();
        app.Services.GetRequiredService<MentorHubScopes>().GetOrCreate(id)
            .GetRequiredService<TestOrderService>().Hold = () => held.Task;

        await connection.SendAsync("SendMessage", "Run the failing action", null);
        await Until(() => observer.Steps("failing_action").Count > 0, app);

        await connection.DisposeAsync();   // the user closes the tab while the action runs
        await Until(() => app.Services.GetRequiredService<MentorHubScopes>().TryGet(id) is null, app);
        if (stops) held.SetCanceled(); else held.SetResult();

        await Until(() => observer.Steps("failing_action").Count > 1, app);
        observer.Steps("failing_action").Should().Equal(["Allowed/allowed", last]);
        observer.Seen.Last(d => d.ToolName == "failing_action").PageName.Should().Be("Orders",
            "the page is read when the call reaches the gate, while the connection's scope is open");
        app.Logs.Exceptions.OfType<ObjectDisposedException>().Should().BeEmpty("the turn ends without touching the closed scope");
    }

    private static async Task Until(Func<bool> condition, ServerAppFactory app)
    {
        for (var waited = 0; !condition() && waited < 10_000; waited += 50)
        {
            await app.Services.GetRequiredService<MentorActionDecisions>().FlushAsync();
            await Task.Delay(50);
        }
        condition().Should().BeTrue("within 10 s");
    }
}
