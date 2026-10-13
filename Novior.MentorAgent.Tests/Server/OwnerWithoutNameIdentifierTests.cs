using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Principal;
using System.Text.Json;
using FluentAssertions;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Novior.MentorAgent.Tests.Server;

/// <summary>
/// The owner of a hub connection is recognised without a <c>NameIdentifier</c> claim (BUG-130).
///
/// <para>
/// <c>/mentor/approve</c>, <c>/mentor/cancel</c> and <c>/mentor/session</c> compared <c>NameIdentifier</c>
/// alone, and so did the purpose that binds a session snapshot to its owner. Windows authentication issues
/// no <c>NameIdentifier</c>, nor does JwtBearer with <c>MapInboundClaims = false</c> (it keeps <c>sub</c>):
/// the owner was refused, 403, its own confirmations, its own Stop and its own session. Now both read
/// <c>NameIdentifier</c>, then <c>sub</c>, then <c>oid</c>, then the identity's name — the order of the MCP
/// rate limit.
/// </para>
/// </summary>
public sealed class OwnerWithoutNameIdentifierTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    public static TheoryData<string> Shapes => new() { ClaimTypes.Name, "sub", "oid" };

    private static HttpRequestMessage As(HttpMethod method, string url, string user, string? idClaims = null)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add(ServerAppFactory.AsUserHeader, user);
        if (idClaims is not null) req.Headers.Add(ServerAppFactory.IdClaimsHeader, idClaims);
        return req;
    }

    private static async Task<(Task Turn, Guid ActionId)> ParkAsync(ServerAppFactory app, HubConnection hub)
    {
        app.Chat.CallTool("issue_refund", new { orderId = 1001 }).Say("Refund issued.");
        var seen = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<string, string, string>("ConfirmationRequired", (id, _, _) => seen.TrySetResult(Guid.Parse(id)));
        await hub.StartAsync();

        var turn = hub.InvokeAsync("SendMessage", "refund order 1001", null);
        (await Task.WhenAny(seen.Task, Task.Delay(Patience))).Should().BeSameAs(seen.Task);
        return (turn, await seen.Task);
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task The_owner_answers_its_own_confirmation_and_a_stranger_gets_403(string idClaim)
    {
        await using var app = await ServerAppFactory.CreateAsync(userId: "ada", idClaimTypes: [idClaim]);
        await using var hub = app.CreateHubConnection();
        var (turn, actionId) = await ParkAsync(app, hub);

        var url = $"/mentor/approve?actionId={actionId}&approved=true";
        (await app.Client.SendAsync(As(HttpMethod.Post, url, "bob"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await app.Client.SendAsync(As(HttpMethod.Post, url, "ada"))).StatusCode.Should().Be(HttpStatusCode.OK,
            "the owner is identified by {0} when there is no NameIdentifier", idClaim);

        (await Task.WhenAny(turn, Task.Delay(Patience))).Should().BeSameAs(turn, "the approval releases the turn");
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task The_owner_stops_saves_and_restores_and_a_stranger_gets_403(string idClaim)
    {
        await using var app = await ServerAppFactory.CreateAsync(userId: "ada", idClaimTypes: [idClaim]);
        app.Chat.Say("Noted.");
        await using var hub = app.CreateHubConnection();
        await hub.StartAsync();
        await hub.InvokeAsync("SendMessage", "remember order 1001", null);

        var cancel = $"/mentor/cancel?connectionId={hub.ConnectionId}";
        var session = $"/mentor/session?connectionId={hub.ConnectionId}";

        (await app.Client.SendAsync(As(HttpMethod.Post, cancel, "bob"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await app.Client.SendAsync(As(HttpMethod.Post, cancel, "ada"))).StatusCode.Should().Be(HttpStatusCode.OK);

        (await app.Client.SendAsync(As(HttpMethod.Get, session, "bob"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var saved = await app.Client.SendAsync(As(HttpMethod.Get, session, "ada"));
        saved.StatusCode.Should().Be(HttpStatusCode.OK);
        var snapshot = await saved.Content.ReadFromJsonAsync<JsonElement>();

        var restore = As(HttpMethod.Post, session, "ada");
        restore.Content = JsonContent.Create(snapshot);
        (await app.Client.SendAsync(restore)).StatusCode.Should().Be(HttpStatusCode.OK,
            "the snapshot was bound to the owner's {0}, and the owner restores it", idClaim);
    }

    [Fact]
    public async Task A_name_never_matches_somebody_elses_identifier()
    {
        // The key names the claim it came from. A principal whose name happens to be another user's
        // NameIdentifier is somebody else.
        await using var app = await ServerAppFactory.CreateAsync(userId: "42", idClaimTypes: [ClaimTypes.NameIdentifier]);
        await using var hub = app.CreateHubConnection();
        var (turn, actionId) = await ParkAsync(app, hub);

        var url = $"/mentor/approve?actionId={actionId}&approved=true";
        (await app.Client.SendAsync(As(HttpMethod.Post, url, "42", idClaims: "name"))).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);

        (await app.Client.SendAsync(As(HttpMethod.Post, url, "42"))).StatusCode.Should().Be(HttpStatusCode.OK);
        await Task.WhenAny(turn, Task.Delay(Patience));
    }

    [Fact]
    public async Task A_snapshot_of_a_NameIdentifier_owner_is_bound_as_in_rc14()
    {
        // The owner key of a principal with NameIdentifier keeps its rc.14 form, so a snapshot saved by
        // rc.14 still restores. Read back with the rc.14 purpose: it opens.
        await using var app = await ServerAppFactory.CreateAsync(userId: "ada");
        app.Chat.Say("Noted.");
        await using var hub = app.CreateHubConnection();
        await hub.StartAsync();
        await hub.InvokeAsync("SendMessage", "remember order 1001", null);

        var saved = await app.Client.SendAsync(As(HttpMethod.Get, $"/mentor/session?connectionId={hub.ConnectionId}", "ada"));
        var body = await saved.Content.ReadFromJsonAsync<JsonElement>();

        var rc14 = app.Services.GetRequiredService<IDataProtectionProvider>()
                      .CreateProtector("MentorAgent.Server.SessionSnapshot", "v1", "user:ada");
        var open = () => rc14.Unprotect(body.GetProperty("protected").GetString()!);
        open.Should().NotThrow();
    }

    [Fact]
    public void Windows_authentication_issues_no_NameIdentifier()
    {
        // The .NET fact behind BUG-130, on the machine running the tests: the current Windows identity is
        // authenticated, has a name and no NameIdentifier claim.
        if (!OperatingSystem.IsWindows()) return;   // a Windows identity exists only there

        using var identity = WindowsIdentity.GetCurrent();

        identity.IsAuthenticated.Should().BeTrue();
        identity.Name.Should().NotBeNullOrEmpty();
        identity.FindFirst(ClaimTypes.NameIdentifier).Should().BeNull();
    }
}
