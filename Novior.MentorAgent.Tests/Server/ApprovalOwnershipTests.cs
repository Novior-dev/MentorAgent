using System.Net;
using FluentAssertions;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;

namespace Novior.MentorAgent.Tests.Server;

/// <summary>
/// Who may answer a HITL confirmation through <c>POST /mentor/approve</c> (BUG-058).
///
/// <para>
/// The endpoint took an <c>actionId</c> and approved, with no notion of who was asking — the
/// approval store is keyed by <see cref="Guid"/> alone. So anyone able to reach the server and
/// learn an action id could <b>approve a gated, destructive action</b> that its owner had not
/// answered, or <c>approved=false</c> to deny one they wanted. This is the boundary the whole HITL
/// feature exists to provide: BUG-021 and BUG-035 were both S1 for exactly "a gated action
/// reachable without a principal".
/// </para>
///
/// <para>
/// It is <b>not</b> a regression from this release — it predates rc.2 — but it is the same shape as
/// BUG-056, which was found first, and the action id shares the connection id's weakness: the
/// documented client sends it in a query string, so it reaches access logs, proxy logs and browser
/// history on the way back.
/// </para>
///
/// <para>
/// The rule is the same one <c>/mentor/session</c> uses: <b>ownership</b>, not merely
/// authentication. A confirmation raised on a connection owned by an authenticated principal may be
/// answered only by that principal; when the owner is anonymous, or when the confirmation did not
/// come from a hub connection at all (SSE, in-process Blazor Server), behaviour is unchanged.
/// </para>
/// </summary>
public sealed class ApprovalOwnershipTests
{
    private static ServerAppFactory Script(ServerAppFactory app)
    {
        app.Chat!.CallTool("issue_refund", new { orderId = 1001 }).Say("Refund issued.");
        return app;
    }

    private static HttpRequestMessage As(string url, string? user)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Add(ServerAppFactory.AsUserHeader, user ?? "-");
        return req;
    }

    /// <summary>Parks a turn on a confirmation and returns the pending action id.</summary>
    private static async Task<(Task Turn, Guid ActionId)> ParkAsync(ServerAppFactory app, HubConnection hub)
    {
        var seen = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<string, string, string>("ConfirmationRequired",
            (id, _, _) => seen.TrySetResult(Guid.Parse(id)));

        await hub.StartAsync();

        // Not awaited: SendMessage only returns when the turn finishes, and the turn cannot finish
        // until the approval arrives — that is the deadlock the HTTP endpoint exists to avoid.
        var turn = hub.InvokeAsync("SendMessage", "refund order 1001", null);

        var completed = await Task.WhenAny(seen.Task, Task.Delay(TimeSpan.FromSeconds(20)));
        completed.Should().BeSameAs(seen.Task, "the scripted tool call has to raise a confirmation");

        return (turn, await seen.Task);
    }

    /// <summary>Releases the parked turn so the test host is not left with one hanging.</summary>
    private static async Task ReleaseAsync(ServerAppFactory app, Guid actionId, Task turn)
    {
        await app.Client.SendAsync(As($"/mentor/approve?actionId={actionId}&approved=false", "ada"));
        await Task.WhenAny(turn, Task.Delay(TimeSpan.FromSeconds(15)));
    }

    [Fact]
    public async Task An_anonymous_caller_cannot_approve_someone_elses_action()
    {
        await using var app = Script(await ServerAppFactory.CreateAsync(userId: "ada"));
        await using var hub = app.CreateHubConnection();
        var (turn, actionId) = await ParkAsync(app, hub);

        var res = await app.Client.SendAsync(
            As($"/mentor/approve?actionId={actionId}&approved=true", user: null));

        res.StatusCode.Should().BeOneOf(
            [HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden],
            "approving runs a gated action the owner has not answered");

        await ReleaseAsync(app, actionId, turn);
    }

    [Fact]
    public async Task A_different_authenticated_user_cannot_approve_it_either()
    {
        await using var app = Script(await ServerAppFactory.CreateAsync(userId: "ada"));
        await using var hub = app.CreateHubConnection();
        var (turn, actionId) = await ParkAsync(app, hub);

        var res = await app.Client.SendAsync(
            As($"/mentor/approve?actionId={actionId}&approved=true", user: "bob"));

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        await ReleaseAsync(app, actionId, turn);
    }

    [Fact]
    public async Task A_stranger_cannot_deny_it_either()
    {
        // Denial matters as much as approval: refusing someone's action is a denial of service on
        // the feature, and it is the cheaper attack because it needs no timing luck.
        await using var app = Script(await ServerAppFactory.CreateAsync(userId: "ada"));
        await using var hub = app.CreateHubConnection();
        var (turn, actionId) = await ParkAsync(app, hub);

        var res = await app.Client.SendAsync(
            As($"/mentor/approve?actionId={actionId}&approved=false", user: "bob"));

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        await ReleaseAsync(app, actionId, turn);
    }

    [Fact]
    public async Task The_owner_can_approve_and_the_turn_completes()
    {
        // The control. Without it, "nobody can approve anything" satisfies every test above — and
        // that would break the entire HITL flow rather than secure it.
        await using var app = Script(await ServerAppFactory.CreateAsync(userId: "ada"));
        await using var hub = app.CreateHubConnection();
        var (turn, actionId) = await ParkAsync(app, hub);

        var res = await app.Client.SendAsync(
            As($"/mentor/approve?actionId={actionId}&approved=true", user: "ada"));

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var finished = await Task.WhenAny(turn, Task.Delay(TimeSpan.FromSeconds(20)));
        finished.Should().BeSameAs(turn, "the approval has to release the parked turn");
    }

    [Fact]
    public async Task A_host_without_authentication_keeps_working()
    {
        // No principal on the connection means there is nothing to compare. Failing closed here
        // would break HITL for every unauthenticated application — and for SSE clients, whose
        // confirmations never pass through a hub connection at all.
        await using var app = Script(await ServerAppFactory.CreateAsync());   // no userId
        await using var hub = app.CreateHubConnection();
        var (turn, actionId) = await ParkAsync(app, hub);

        var res = await app.Client.PostAsync($"/mentor/approve?actionId={actionId}&approved=true", null);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var finished = await Task.WhenAny(turn, Task.Delay(TimeSpan.FromSeconds(20)));
        finished.Should().BeSameAs(turn);
    }
}
