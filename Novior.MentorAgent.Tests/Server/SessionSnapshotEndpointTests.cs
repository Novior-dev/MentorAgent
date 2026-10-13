using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Novior.MentorAgent.Abstractions.Models;
using FluentAssertions;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Novior.MentorAgent.Tests.Server;

/// <summary>
/// <c>GET</c>/<c>POST /mentor/session</c> — ROADMAP #34.
///
/// <para>
/// A SignalR reconnect is a <b>new connection id</b>, and MentorAgent keys its per-connection DI
/// scope by that id — so a reconnect is a new conversation. That is deliberate (it is exactly what
/// keeps two browsers apart) and the documented remedy is
/// <c>SerializeSessionAsync</c>/<c>RestoreSessionAsync</c>. But those live on the orchestrator
/// inside the server's DI scope, so on WebAssembly they throw <c>NotSupportedException</c> naming
/// that reason, and a React or MAUI client has no way to reach them at all. The remedy had no
/// transport.
/// </para>
///
/// <para>
/// The endpoint gives it one, and inherits the properties the neighbouring HTTP endpoints already
/// have: it takes a <c>connectionId</c> because that is what identifies the scope, it must be HTTP
/// rather than a hub method for the same reason approve and cancel are (SignalR dispatches one
/// invocation at a time per connection), and an unknown connection is a 404 rather than a new
/// empty session.
/// </para>
/// </summary>
public sealed class SessionSnapshotEndpointTests
{
    [Fact]
    public async Task An_unknown_connection_id_is_not_found_rather_than_a_fresh_session()
    {
        // The alternative — inventing a session for an id nobody has — would let a client restore
        // into a scope that does not exist and believe it had succeeded.
        await using var app = await ServerAppFactory.CreateAsync();
        var client = app.Client;

        var get = await client.GetAsync("/mentor/session?connectionId=does-not-exist");
        var post = await client.PostAsJsonAsync(
            "/mentor/session?connectionId=does-not-exist", JsonDocument.Parse("{}").RootElement);

        get.StatusCode.Should().Be(HttpStatusCode.NotFound);
        post.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_connection_that_has_said_nothing_yet_has_no_snapshot_to_save()
    {
        // `null` rather than an empty snapshot is the rule B22 established: a caller who stores the
        // empty one overwrites a good saved conversation with nothing.
        await using var app = await ServerAppFactory.CreateAsync();
        await using var hub = app.CreateHubConnection();
        await hub.StartAsync();

        // StartAsync completes on the handshake, which does not guarantee that the server has
        // finished OnConnectedAsync and registered this connection's DI scope — so an HTTP call
        // made immediately afterwards can legitimately answer 404 "unknown connection". That race
        // is real for clients too (see the endpoint's own note) but it is not what this test is
        // about, so the precondition is arranged rather than raced: one cheap hub invocation
        // guarantees the scope exists before the snapshot is asked for.
        await hub.InvokeAsync("UpdatePageContext", new PageContextSnapshot { PageName = "Home" });

        var res = await app.Client.GetAsync($"/mentor/session?connectionId={hub.ConnectionId}");

        res.StatusCode.Should().Be(HttpStatusCode.NoContent,
            "there is nothing to save, and that is not an error");
    }

    [Fact]
    public async Task A_conversation_survives_being_saved_and_restored_onto_a_second_connection()
    {
        // The whole point, driven as a reconnect actually happens: talk on one connection, save,
        // drop it, open another, restore, and see the history the model is sent grow rather than
        // start again.
        await using var app = await ServerAppFactory.CreateAsync();
        var client = app.Client;

        await using (var first = app.CreateHubConnection())
        {
            await first.StartAsync();
            await first.InvokeAsync("SendMessage", "il mio ordine preferito è il 1002", null);

            var saved = await client.GetAsync($"/mentor/session?connectionId={first.ConnectionId}");
            saved.StatusCode.Should().Be(HttpStatusCode.OK);
            var snapshot = await saved.Content.ReadFromJsonAsync<JsonElement>();

            await using var second = app.CreateHubConnection();
            await second.StartAsync();
            second.ConnectionId.Should().NotBe(first.ConnectionId, "a reconnect is a new scope");

            // As the README tells a client: restore after the first hub call. StartAsync returns once the
            // handshake is done, possibly before OnConnectedAsync has registered the connection's scope,
            // and a POST in that gap is a 404 (seen under a loaded full-suite run; a hub call is only
            // dispatched after OnConnectedAsync).
            await second.InvokeAsync("UpdatePageContext", new PageContextSnapshot { PageName = "Home" });

            var restored = await client.PostAsJsonAsync(
                $"/mentor/session?connectionId={second.ConnectionId}", snapshot);
            restored.StatusCode.Should().Be(HttpStatusCode.OK);

            var before = app.Chat.Calls.Count;
            await second.InvokeAsync("SendMessage", "quale ordine ho detto?", null);
            app.Chat.Calls.Count.Should().BeGreaterThan(before);

            // The restored turn has to carry the earlier exchange. Asserting on the messages the
            // model was actually sent, not on what it answered — the answer is the model's choice,
            // the history is the library's.
            var sent = string.Join("\n", app.Chat.Calls[^1].Messages.Select(m => m.Text));
            sent.Should().Contain("1002",
                "the restored session is the previous conversation, or the endpoint achieves nothing");
        }
    }

    [Fact]
    public async Task A_snapshot_from_somewhere_else_is_refused_and_leaves_the_conversation_alone()
    {
        // BUG-026's rule, now reachable over HTTP: AF's deserializer answers arbitrary JSON with a
        // valid *empty* session, which would silently wipe a live conversation. The refusal has to
        // happen before anything is replaced, and it has to be a 400 rather than a 500 — this is
        // the caller's input, not a server fault.
        await using var app = await ServerAppFactory.CreateAsync();
        await using var hub = app.CreateHubConnection();
        await hub.StartAsync();
        await hub.InvokeAsync("SendMessage", "ciao", null);

        var client = app.Client;
        var res = await client.PostAsJsonAsync(
            $"/mentor/session?connectionId={hub.ConnectionId}",
            JsonDocument.Parse("""{"not":"a session"}""").RootElement);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // And the conversation is still there afterwards.
        var still = await client.GetAsync($"/mentor/session?connectionId={hub.ConnectionId}");
        still.StatusCode.Should().Be(HttpStatusCode.OK,
            "a refused restore must not have destroyed what was already there");
    }

    // ── BUG-110: what the endpoint accepts ──────────────────────────────────────

    private static async Task<(ServerAppFactory App, HubConnection Hub, string Snapshot)> ConversationAsync(string text)
    {
        var app = await ServerAppFactory.CreateAsync();
        var hub = app.CreateHubConnection();
        await hub.StartAsync();
        await hub.InvokeAsync("SendMessage", text, null);
        var saved = await app.Client.GetAsync($"/mentor/session?connectionId={hub.ConnectionId}");
        saved.StatusCode.Should().Be(HttpStatusCode.OK);
        return (app, hub, await saved.Content.ReadAsStringAsync());
    }

    private static Task<HttpResponseMessage> PostAsync(ServerAppFactory app, HubConnection hub, string body)
        => app.Client.PostAsync($"/mentor/session?connectionId={hub.ConnectionId}",
               new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

    [Theory]
    [InlineData("""{"mentorAgentSession":1.5,"session":{}}""")]
    [InlineData("""{"mentorAgentSession":1,"session":{}}""")]
    [InlineData("""{"mentorAgentSession":1,"session":42}""")]
    public async Task A_body_that_is_not_a_snapshot_this_server_issued_is_a_400_and_wipes_nothing(string body)
    {
        // Live on rc.13: 1.5 answered 500 (FormatException); an empty stamped session answered 200 and
        // wiped the conversation.
        var (app, hub, _) = await ConversationAsync("il mio ordine preferito è il 1002");
        await using var _a = app;
        await using var _h = hub;

        var res = await PostAsync(app, hub, body);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await hub.InvokeAsync("SendMessage", "quale ordine ho detto?", null);
        app.Chat.Calls[^1].Messages.Should().Contain(m => m.Text.Contains("1002"), "the conversation was left alone");
    }

    [Fact]
    public async Task A_snapshot_is_opaque_to_the_client()
    {
        // The client stores it and hands it back; it has no reason to read — or edit — the transcript.
        var (app, hub, snapshot) = await ConversationAsync("il mio ordine preferito è il 1002");
        await using var _a = app;
        await using var _h = hub;

        // A phrase with spaces: base64url has none, so ciphertext can never contain it by chance (a bare
        // "1002" could, about once in ten thousand runs).
        snapshot.Should().NotContain("il mio ordine");
    }

    [Fact]
    public async Task An_edited_snapshot_is_refused()
    {
        // Live on rc.13: a snapshot with its codes renamed restored the renamed codes — the history came
        // from the body. An edited transcript (a fake tool result, a fake approval) is history the model
        // then trusts.
        var (app, hub, snapshot) = await ConversationAsync("il mio ordine preferito è il 1002");
        await using var _a = app;
        await using var _h = hub;

        var edited = snapshot.Contains("\"protected\"")
            ? Flip(snapshot)
            : snapshot.Replace("1002", "FORGED-0000");

        var res = await PostAsync(app, hub, edited);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await hub.InvokeAsync("SendMessage", "quale ordine ho detto?", null);
        app.Chat.Calls[^1].Messages.Should().NotContain(m => m.Text.Contains("FORGED-0000"));
    }

    [Fact]
    public async Task A_protected_value_that_is_not_even_base64_is_a_400_not_a_500()
    {
        var (app, hub, _) = await ConversationAsync("ciao");
        await using var _a = app;
        await using var _h = hub;

        var res = await PostAsync(app, hub, """{"mentorAgentServerSession":1,"protected":"!!! not base64 !!!"}""");

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_unprotected_snapshot_saved_before_rc14_is_refused_with_a_re_save_message()
    {
        var (app, hub, _) = await ConversationAsync("ciao");
        await using var _a = app;
        await using var _h = hub;
        // What GET returned until rc.13: the core envelope, in clear.
        const string rc13Body = """{"mentorAgentSession":1,"session":{"stateBag":{"InMemoryChatHistoryProvider":{"messages":[]}}}}""";

        var res = await PostAsync(app, hub, rc13Body);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("GET /mentor/session");
    }

    [Fact]
    public async Task A_protected_snapshot_the_core_cannot_use_is_a_400_with_the_cores_reason_and_wipes_nothing()
    {
        // Review of the rc.14 fix: every other test body fails at the envelope, so the endpoint's path for a
        // snapshot that unprotects fine and is then refused by the core ran in no test.
        var (app, hub, _) = await ConversationAsync("il mio ordine preferito è il 1002");
        await using var _a = app;
        await using var _h = hub;
        var protector = app.Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("MentorAgent.Server.SessionSnapshot", "v1", "anonymous");
        var body = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["mentorAgentServerSession"] = 1,
            ["protected"] = protector.Protect("""{"mentorAgentSession":1.5,"session":{}}"""),
        });

        var res = await PostAsync(app, hub, body);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("cannot be read by this version");
        await hub.InvokeAsync("SendMessage", "quale ordine ho detto?", null);
        app.Chat.Calls[^1].Messages.Should().Contain(m => m.Text.Contains("1002"), "the conversation was left alone");
    }

    private static string Flip(string snapshot)
    {
        using var doc = JsonDocument.Parse(snapshot);
        var value = doc.RootElement.GetProperty("protected").GetString()!;
        var i = value.Length / 2;
        var flipped = value[..i] + (value[i] == 'A' ? 'B' : 'A') + value[(i + 1)..];
        return snapshot.Replace(value, flipped);
    }
}
