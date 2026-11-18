using FluentAssertions;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Novior.MentorAgent.Tests.Server;

/// <summary>
/// Story 1288 — on the hub, a UI action the client declared in its page snapshot goes through the gate like every
/// other action: the observers see its decisions, and an action a policy stops never reaches the client. The hub runs
/// a declared action by sending <c>UIActionRequested</c> to the client; before, it ran in the loop of
/// <c>UIActionsMiddleware</c>, which the gate never saw. The in-process path (Blazor Server, MAUI Hybrid) is
/// <c>UIActionGateTests</c>; <c>/mentor/chat</c> cannot declare UI actions — it has no page snapshot.
/// </summary>
public sealed class HubUIActionGateTests
{
    private sealed class Client
    {
        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal List<(string Name, string? Parameter)> Requested { get; } = [];

        internal void Attach(HubConnection hub)
        {
            hub.On("StreamingCompleted", () => _completed.TrySetResult());
            hub.On<string>("Error", _ => _completed.TrySetResult());
            hub.On<string, string?>("UIActionRequested", (name, parameter) =>
            {
                lock (Requested) Requested.Add((name, parameter));
            });
        }

        internal Task WaitAsync() => _completed.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    private static async Task<(Client Client, HubConnection Hub)> DeclareAndAskAsync(ServerAppFactory app)
    {
        var hub = app.CreateHubConnection();
        var client = new Client();
        client.Attach(hub);
        await hub.StartAsync();

        await hub.InvokeAsync("UpdatePageContext", new PageContextSnapshot
        {
            PageName = "Orders",
            UIActions = [new UIActionInfo { Name = "select_order", Description = "Selects an order in the grid", ParameterHint = "integer: order id" }],
        });
        await hub.InvokeAsync("SendMessage", "seleziona l'ordine 1002", null);
        await client.WaitAsync();
        return (client, hub);
    }

    [Fact]
    public async Task A_UI_action_the_client_declared_goes_through_the_gate_and_reaches_the_client()
    {
        var observer = new RecordingActionObserver();
        await using var app = await ServerAppFactory.CreateAsync(configureServices: s => s.AddMentorActionObserver(observer));
        app.Chat.CallTool("select_order", new { parameter = 1002 }).Say("Selected.");

        var (client, hub) = await DeclareAndAskAsync(app);
        await using var _ = hub;
        await app.Services.GetRequiredService<MentorActionDecisions>().FlushAsync();

        client.Requested.Should().ContainSingle().Which.Name.Should().Be("select_order");
        observer.Steps("select_order").Should().Equal(["Allowed/allowed", "Executed/completed"],
            "an action of the page is decided by the gate on the hub too");
        observer.Seen.Should().Contain(d => d.ToolName == "select_order" && d.DisplayName == "Selects an order in the grid (parameter: integer: order id)",
            "the text of its decisions is its description, as the model reads it");
    }

    [Fact]
    public async Task A_UI_action_a_policy_stops_never_reaches_the_client()
    {
        var observer = new RecordingActionObserver();
        var policy = new TestActionPolicy
        {
            Verdict = r => r.ToolName == "select_order" ? MentorActionVerdict.Stop("not now") : MentorActionVerdict.Proceed,
        };
        await using var app = await ServerAppFactory.CreateAsync(configureServices: s =>
        {
            s.AddMentorActionObserver(observer);
            s.AddMentorActionPolicy(policy);
        });
        app.Chat.CallTool("select_order", new { parameter = 1002 }).Say("I could not.");

        var (client, hub) = await DeclareAndAskAsync(app);
        await using var _ = hub;
        await app.Services.GetRequiredService<MentorActionDecisions>().FlushAsync();

        client.Requested.Should().BeEmpty("the hub sends UIActionRequested only for an action the gate let through");
        observer.Steps("select_order").Should().ContainSingle().Which.Should().EndWith("/policy");
    }
}
