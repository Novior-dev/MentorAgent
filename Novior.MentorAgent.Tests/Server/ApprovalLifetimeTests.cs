using System.Net;
using FluentAssertions;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Server.Hubs;
using Novior.MentorAgent.Tests.App;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Novior.MentorAgent.Tests.Server;

/// <summary>
/// A confirmation raised over the hub lives as long as the turn that waits for it, and
/// <c>POST /mentor/approve</c> says so (BUG-128, BUG-129).
///
/// <para>
/// Up to rc.14, when the owner's connection closed the hub forgot who had raised the confirmation, but the
/// turn kept waiting: anybody who knew the action id could approve it, with no owner left to check against,
/// and the action ran. After Stop the request stayed pending, and every late answer got a 200. Now the end
/// of the connection's scope cancels the turn, the request leaves the pending list with its wait, and an
/// answer that reaches nothing is a 404.
/// </para>
/// </summary>
public sealed class ApprovalLifetimeTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private static HttpRequestMessage Approve(Guid actionId, string user)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/mentor/approve?actionId={actionId}&approved=true");
        req.Headers.Add(ServerAppFactory.AsUserHeader, user);
        return req;
    }

    /// <summary>
    /// Parks ada's turn on the refund's confirmation. Returns the action id and the refund service of the
    /// connection's own scope, the one the tool would run in.
    /// </summary>
    private static async Task<(Task Turn, Guid ActionId, TestOrderService Orders)> ParkAsync(
        ServerAppFactory app, HubConnection hub)
    {
        app.Chat.CallTool("issue_refund", new { orderId = 1001 }).Say("Refund issued.");

        var seen = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<string, string, string>("ConfirmationRequired", (id, _, _) => seen.TrySetResult(Guid.Parse(id)));
        await hub.StartAsync();

        var turn = hub.InvokeAsync("SendMessage", "refund order 1001", null);
        (await Task.WhenAny(seen.Task, Task.Delay(Patience))).Should().BeSameAs(seen.Task,
            "the scripted tool call has to raise a confirmation");

        var orders = app.Services.GetRequiredService<MentorHubScopes>()
                        .TryGet(hub.ConnectionId!)!.GetRequiredService<TestOrderService>();
        return (turn, await seen.Task, orders);
    }

    private static async Task Eventually(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(25);
        condition().Should().BeTrue(because);
    }

    [Theory]
    [InlineData(MentorHitlMode.Blocking)]
    [InlineData(MentorHitlMode.Native)]
    public async Task When_the_owners_connection_closes_nobody_can_approve_the_action(MentorHitlMode mode)
    {
        await using var app = await ServerAppFactory.CreateAsync(o => o.HitlMode = mode, userId: "ada");
        var hub = app.CreateHubConnection();
        var (_, actionId, orders) = await ParkAsync(app, hub);
        var connectionId = hub.ConnectionId!;

        await hub.DisposeAsync();   // the owner closes the tab

        // A stranger tries while the server takes the connection down, and after. Never a 200: first the
        // owner check refuses, then nothing is left to answer.
        var scopes = app.Services.GetRequiredService<MentorHubScopes>();
        HttpStatusCode status;
        var deadline = DateTime.UtcNow + Patience;
        do
        {
            status = (await app.Client.SendAsync(Approve(actionId, "bob"))).StatusCode;
            status.Should().BeOneOf([HttpStatusCode.Forbidden, HttpStatusCode.NotFound],
                "a stranger must never approve someone else's action");
        }
        while (status != HttpStatusCode.NotFound && DateTime.UtcNow < deadline);

        status.Should().Be(HttpStatusCode.NotFound);
        scopes.TryGet(connectionId).Should().BeNull();
        (await app.Client.SendAsync(Approve(actionId, "ada"))).StatusCode.Should().Be(HttpStatusCode.NotFound,
            "the owner's own late answer reaches nothing either: the turn ended with the connection");

        await Eventually(() => !app.Services.GetRequiredService<MentorApprovalStore>().IsPending(actionId),
            "closing the connection cancels the wait, and the request goes with it");
        orders.Invoked.Should().NotContain(nameof(TestOrderService.IssueRefundAsync));
    }

    [Theory]
    [InlineData(MentorHitlMode.Blocking)]
    [InlineData(MentorHitlMode.Native)]
    public async Task An_approval_after_Stop_is_a_404_and_runs_nothing(MentorHitlMode mode)
    {
        await using var app = await ServerAppFactory.CreateAsync(o => o.HitlMode = mode, userId: "ada");
        await using var hub = app.CreateHubConnectionAs("ada");
        var (turn, actionId, orders) = await ParkAsync(app, hub);

        var stop = new HttpRequestMessage(HttpMethod.Post, $"/mentor/cancel?connectionId={hub.ConnectionId}");
        stop.Headers.Add(ServerAppFactory.AsUserHeader, "ada");
        (await app.Client.SendAsync(stop)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Task.WhenAny(turn, Task.Delay(Patience))).Should().BeSameAs(turn, "Stop ends the turn");

        (await app.Client.SendAsync(Approve(actionId, "ada"))).StatusCode.Should().Be(HttpStatusCode.NotFound,
            "the click landed after Stop: nothing waits for it, and the client must not count it as an approval");

        app.Services.GetRequiredService<MentorApprovalStore>().IsPending(actionId).Should().BeFalse();
        orders.Invoked.Should().NotContain(nameof(TestOrderService.IssueRefundAsync));
        hub.State.Should().Be(HubConnectionState.Connected);
    }
}
