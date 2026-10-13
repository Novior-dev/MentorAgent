using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;

namespace Novior.MentorAgent.Tests.Server;

/// <summary>
/// Who may read or replace a conversation through <c>/mentor/session</c> (BUG-056).
///
/// <para>
/// The endpoint was added for ROADMAP #34 with no authorization at all, matching its neighbours
/// <c>/mentor/approve</c> and <c>/mentor/cancel</c>. That was the wrong thing to match: those two
/// <i>act</i> on a turn, while this one <b>returns the conversation itself</b>. Measured live against
/// a server with JWT authentication configured, a completely anonymous caller received another
/// user's transcript — 200, with the planted secret in the body.
/// </para>
///
/// <para>
/// The connection id is 128 bits of randomness, but it is not a secret: it travels in a query
/// string, so it lands in access logs, proxy logs and browser history, and the owning page can read
/// it from its own client. A capability that leaks into logs cannot be the only thing standing
/// between a stranger and a conversation.
/// </para>
///
/// <para>
/// The rule implemented is <b>ownership</b>, not merely authentication: if the connection was opened
/// by an authenticated principal, the caller must be that same principal. A host with no
/// authentication at all keeps today's behaviour, because there is no identity to compare and the
/// connection id is the only notion of "whose" that exists there.
/// </para>
/// </summary>
public sealed class SessionSnapshotAuthorizationTests
{
    private const string Secret = "PRATICA-8891";

    private static HttpRequestMessage As(HttpMethod method, string url, string? user)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add(ServerAppFactory.AsUserHeader, user ?? "-");   // "-" = anonymous
        return req;
    }

    /// <summary>A connection owned by <c>ada</c>, with something worth protecting in it.</summary>
    private static async Task<(ServerAppFactory App, HubConnection Hub)> OwnedByAdaAsync()
    {
        var app = await ServerAppFactory.CreateAsync(userId: "ada");
        var hub = app.CreateHubConnection();
        await hub.StartAsync();
        await hub.InvokeAsync("SendMessage", $"il mio numero di pratica è {Secret}", null);
        return (app, hub);
    }

    [Fact]
    public async Task An_anonymous_caller_cannot_read_someone_elses_conversation()
    {
        var (app, hub) = await OwnedByAdaAsync();
        await using var _ = app;
        await using var __ = hub;

        var res = await app.Client.SendAsync(
            As(HttpMethod.Get, $"/mentor/session?connectionId={hub.ConnectionId}", user: null));

        // Array overload, not params: BeOneOf's params form swallows a trailing "because" as
        // another candidate value, so the reason silently becomes an accepted status code.
        res.StatusCode.Should().BeOneOf(
            [HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden],
            "the endpoint returns the conversation itself, not a command against it");
        (await res.Content.ReadAsStringAsync()).Should().NotContain(Secret);
    }

    [Fact]
    public async Task A_different_authenticated_user_cannot_read_it_either()
    {
        // Being signed in is not the same as being the owner. This is the case a "RequireAuthorization"
        // would have let through, which is why the check is ownership rather than authentication.
        var (app, hub) = await OwnedByAdaAsync();
        await using var _ = app;
        await using var __ = hub;

        var res = await app.Client.SendAsync(
            As(HttpMethod.Get, $"/mentor/session?connectionId={hub.ConnectionId}", user: "bob"));

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await res.Content.ReadAsStringAsync()).Should().NotContain(Secret);
    }

    [Fact]
    public async Task The_owner_can_read_their_own_conversation()
    {
        // The control. Without it "nobody can read anything" would satisfy the two tests above.
        var (app, hub) = await OwnedByAdaAsync();
        await using var _ = app;
        await using var __ = hub;

        var res = await app.Client.SendAsync(
            As(HttpMethod.Get, $"/mentor/session?connectionId={hub.ConnectionId}", user: "ada"));

        res.StatusCode.Should().Be(HttpStatusCode.OK,
            "this is the whole point of the endpoint — a reconnect restoring its own conversation");
        // The body is opaque since BUG-110; that it restores the conversation is pinned by
        // The_owner_restores_their_own_snapshot_onto_a_new_connection.
    }

    [Fact]
    public async Task A_stranger_cannot_replace_someone_elses_conversation()
    {
        // Reading is the louder half; overwriting is the destructive one. A valid snapshot from a
        // stranger would install their transcript into the owner's live session.
        var (app, hub) = await OwnedByAdaAsync();
        await using var _ = app;
        await using var __ = hub;

        var mine = await app.Client.SendAsync(
            As(HttpMethod.Get, $"/mentor/session?connectionId={hub.ConnectionId}", user: "ada"));
        var snapshot = await mine.Content.ReadFromJsonAsync<JsonElement>();

        var req = As(HttpMethod.Post, $"/mentor/session?connectionId={hub.ConnectionId}", user: "bob");
        req.Content = JsonContent.Create(snapshot);
        var res = await app.Client.SendAsync(req);

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_host_without_authentication_keeps_working()
    {
        // No principal on the connection means there is nothing to compare, and the connection id
        // is already the only notion of "whose" such a host has — the same position
        // /mentor/cancel and /mentor/approve have always been in. Failing closed here would break
        // the documented reconnect flow for every unauthenticated application.
        await using var app = await ServerAppFactory.CreateAsync();       // no userId
        await using var hub = app.CreateHubConnection();
        await hub.StartAsync();
        await hub.InvokeAsync("UpdatePageContext", new PageContextSnapshot { PageName = "Home" });

        var res = await app.Client.GetAsync($"/mentor/session?connectionId={hub.ConnectionId}");

        res.StatusCode.Should().BeOneOf([HttpStatusCode.OK, HttpStatusCode.NoContent]);
    }

    // ── BUG-110 ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_malformed_body_without_credentials_is_401_not_400()
    {
        // The body used to be bound before the handler ran, so a stranger's malformed body got a 400 —
        // the ownership check never saw it. The check comes first now; the body is read only for its owner.
        var (app, hub) = await OwnedByAdaAsync();
        await using var _ = app;
        await using var __ = hub;

        var anonymous = As(HttpMethod.Post, $"/mentor/session?connectionId={hub.ConnectionId}", user: null);
        anonymous.Content = new StringContent("{not json", System.Text.Encoding.UTF8, "application/json");
        (await app.Client.SendAsync(anonymous)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var owner = As(HttpMethod.Post, $"/mentor/session?connectionId={hub.ConnectionId}", user: "ada");
        owner.Content = new StringContent("{}", System.Text.Encoding.UTF8, "text/plain");
        (await app.Client.SendAsync(owner)).StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
    }

    [Fact]
    public async Task Another_users_snapshot_cannot_be_restored_into_their_own_connection()
    {
        // Live on rc.13: an admin restored the manager's snapshot into the admin's own connection and the
        // model recalled the manager's codes. A snapshot is bound to the user it was saved for.
        var (app, hub) = await OwnedByAdaAsync();
        await using var _ = app;
        await using var __ = hub;
        var adas = await app.Client.SendAsync(
            As(HttpMethod.Get, $"/mentor/session?connectionId={hub.ConnectionId}", user: "ada"));
        var snapshot = await adas.Content.ReadAsStringAsync();

        await using var bobs = app.CreateHubConnectionAs("bob");
        await bobs.StartAsync();
        await bobs.InvokeAsync("UpdatePageContext", new PageContextSnapshot { PageName = "Home" });

        var restore = As(HttpMethod.Post, $"/mentor/session?connectionId={bobs.ConnectionId}", user: "bob");
        restore.Content = new StringContent(snapshot, System.Text.Encoding.UTF8, "application/json");
        var res = await app.Client.SendAsync(restore);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await bobs.InvokeAsync("SendMessage", "qual è il mio numero di pratica?", null);
        app.Chat.Calls[^1].Messages.Should().NotContain(m => m.Text.Contains(Secret),
            "ada's conversation must not become bob's history");
    }

    [Fact]
    public async Task The_owner_restores_their_own_snapshot_onto_a_new_connection()
    {
        // The control, now that the body is opaque: the owner's round trip still brings the conversation back.
        var (app, hub) = await OwnedByAdaAsync();
        await using var _ = app;
        await using var __ = hub;
        var saved = await app.Client.SendAsync(
            As(HttpMethod.Get, $"/mentor/session?connectionId={hub.ConnectionId}", user: "ada"));
        var snapshot = await saved.Content.ReadAsStringAsync();

        await using var again = app.CreateHubConnection();
        await again.StartAsync();
        await again.InvokeAsync("UpdatePageContext", new PageContextSnapshot { PageName = "Home" });
        var restore = As(HttpMethod.Post, $"/mentor/session?connectionId={again.ConnectionId}", user: "ada");
        restore.Content = new StringContent(snapshot, System.Text.Encoding.UTF8, "application/json");

        (await app.Client.SendAsync(restore)).StatusCode.Should().Be(HttpStatusCode.OK);
        await again.InvokeAsync("SendMessage", "qual è il mio numero di pratica?", null);
        app.Chat.Calls[^1].Messages.Should().Contain(m => m.Text.Contains(Secret));
    }
}
