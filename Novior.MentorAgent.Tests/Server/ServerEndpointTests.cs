using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;

namespace Novior.MentorAgent.Tests.Server;

/// <summary>
/// The HTTP surface of <c>Novior.MentorAgent.Server</c> — until now, zero test coverage.
///
/// <para>
/// These endpoints are a contract with clients this repository does not compile: React, Vue,
/// Angular, MAUI. For them the Server README <em>is</em> the API, and a change here breaks an
/// integration that no build anywhere will catch. That makes the transport layer the part of the
/// library where an automated test is worth the most.
/// </para>
/// </summary>
public sealed class ServerEndpointTests
{
    // ── GET /mentor/tour ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Tour_endpoint_returns_steps_as_json()
    {
        await using var app = await ServerAppFactory.CreateAsync(o => o.EnableOnboardingTour = true);

        var response = await app.Client.GetAsync("/mentor/tour");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json",
            "the WASM client checks the content type — a SPA host answering with index.html and " +
            "HTTP 200 is the failure mode this guards");

        var steps = await response.Content.ReadFromJsonAsync<List<MentorTourStep>>();
        steps.Should().NotBeNull();
    }

    [Fact]
    public async Task Tour_steps_are_shaped_for_a_non_dotnet_client()
    {
        await using var app = await ServerAppFactory.CreateAsync(o => o.EnableOnboardingTour = true);

        var json = await app.Client.GetStringAsync("/mentor/tour");

        // camelCase is what a JavaScript client indexes by; PascalCase would break every
        // documented client silently.
        json.Should().MatchRegex("\"(title|body|url|tryAsking)\"|^\\[\\]$",
            "the documented payload uses camelCase members");
    }

    // ── GET /mentor/admin/metrics — role gated ───────────────────────────────────

    [Fact]
    public async Task Metrics_endpoint_serves_a_caller_in_the_dashboard_role()
    {
        await using var app = await ServerAppFactory.CreateAsync(
            o => o.DashboardRole = "Admin", userId: "u-1", roles: ["Admin"]);

        var response = await app.Client.GetAsync("/mentor/admin/metrics");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var snapshot = await response.Content.ReadFromJsonAsync<MentorMetricsSnapshot>();
        snapshot.Should().NotBeNull("the endpoint returns a MentorMetricsSnapshot");
    }

    [Fact]
    public async Task Metrics_endpoint_refuses_a_caller_in_the_wrong_role()
    {
        await using var app = await ServerAppFactory.CreateAsync(
            o => o.DashboardRole = "Admin", userId: "u-2", roles: ["Sales"]);

        var response = await app.Client.GetAsync("/mentor/admin/metrics");

        response.StatusCode.Should().BeOneOf([HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized],
            "operator data — cost, volumes, top actions — must never reach an end user");
    }

    [Fact]
    public async Task Metrics_endpoint_refuses_an_anonymous_caller()
    {
        await using var app = await ServerAppFactory.CreateAsync(o => o.DashboardRole = "Admin");

        var response = await app.Client.GetAsync("/mentor/admin/metrics");

        response.StatusCode.Should().BeOneOf([HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized],
            "no principal must fail closed, exactly like the wrong role");
    }

    [Fact]
    public async Task An_empty_DashboardRole_opens_the_endpoint_as_documented()
    {
        // Documented as "" = open, development only. Pinning it keeps the escape hatch honest —
        // and keeps anyone from quietly repurposing "" to mean "deny".
        await using var app = await ServerAppFactory.CreateAsync(o => o.DashboardRole = "");

        var response = await app.Client.GetAsync("/mentor/admin/metrics");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── SSE ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sse_get_streams_the_answer_as_data_lines()
    {
        await using var app = await ServerAppFactory.CreateAsync();
        app.Chat.Say("Hello from the server");

        using var response = await app.Client.GetAsync(
            "/mentor/chat?message=" + Uri.EscapeDataString("hi"),
            HttpCompletionOption.ResponseHeadersRead);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("data:", "SSE frames are data: lines, which is what the docs show");
    }

    [Fact]
    public async Task Sse_post_accepts_a_json_body_for_multimodal_turns()
    {
        // The POST variant exists because base64 image data does not fit in a query string.
        await using var app = await ServerAppFactory.CreateAsync(o => o.EnableImageInput = true);
        app.Chat.Say("I see the image");

        using var response = await app.Client.PostAsJsonAsync("/mentor/chat", new
        {
            message = "what is this?",
            attachments = new[]
            {
                new
                {
                    mimeType = "image/png",
                    // 1×1 transparent PNG
                    dataBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==",
                    fileName = "pixel.png",
                },
            },
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
    }

    [Fact]
    public async Task Sse_post_accepts_an_image_only_turn()
    {
        // Documented: "An image-only turn (empty text) is valid."
        await using var app = await ServerAppFactory.CreateAsync(o => o.EnableImageInput = true);
        app.Chat.Say("described");

        using var response = await app.Client.PostAsJsonAsync("/mentor/chat", new
        {
            message = "",
            attachments = new[]
            {
                new { mimeType = "image/png", url = "https://example.invalid/a.png" },
            },
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── POST /mentor/approve and /mentor/cancel ──────────────────────────────────

    [Fact]
    public async Task Approve_endpoint_accepts_a_verdict_over_http()
    {
        // The whole reason this is HTTP and not a hub method: SignalR dispatches one call at a
        // time per connection, so an approval sent through the hub can never run while the turn
        // that is waiting for it holds the pipe.
        await using var app = await ServerAppFactory.CreateAsync();

        var response = await app.Client.PostAsync(
            $"/mentor/approve?actionId={Guid.NewGuid()}&approved=true", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "an unknown id is a no-op, not an error — a stale dialog must not 500 — and nothing waits for it (BUG-128)");
    }

    [Fact]
    public async Task Approve_endpoint_accepts_a_rejection_too()
    {
        await using var app = await ServerAppFactory.CreateAsync();

        var response = await app.Client.PostAsync(
            $"/mentor/approve?actionId={Guid.NewGuid()}&approved=false", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Cancel_endpoint_accepts_a_connection_id()
    {
        await using var app = await ServerAppFactory.CreateAsync();

        var response = await app.Client.PostAsync(
            "/mentor/cancel?connectionId=not-a-real-connection", content: null);

        // 404 is deliberate — the endpoint resolves a per-connection scope and says so when there
        // is none. It is also a *normal* outcome, not only an error: pressing Stop just as the turn
        // finishes races the scope's disposal. The documented client ignores the status, which is
        // why this never surfaced; a client that checks it must know 404 is expected.
        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "an unknown or already-finished connection has no scope to cancel");
    }

    [Fact]
    public async Task Cancel_endpoint_reaches_a_live_connection()
    {
        // The positive half: with a real hub connection the endpoint resolves its scope and
        // returns OK. Without this, the test above would pass even if cancel were broken for
        // every connection, live or not.
        await using var app = await ServerAppFactory.CreateAsync();

        var hub = app.CreateHubConnection();

        // StartAsync completes on the handshake, which can be before OnConnectedAsync has finished
        // binding the per-connection scope on the server. HostedToolsDeclared is sent at the end of
        // OnConnectedAsync, so waiting for it is a deterministic "the scope exists now" — sleeping
        // would only make the race less likely, not absent.
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<int>("HostedToolsDeclared", _ => connected.TrySetResult());

        await hub.StartAsync();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(10));

        try
        {
            var response = await app.Client.PostAsync(
                $"/mentor/cancel?connectionId={hub.ConnectionId}", content: null);

            response.StatusCode.Should().Be(HttpStatusCode.OK,
                "a live connection has a scope, so Stop must reach its orchestrator");
        }
        finally
        {
            await hub.DisposeAsync();
        }
    }

    // ── Routes that must NOT exist ───────────────────────────────────────────────

    [Fact]
    public async Task Unmapped_mentor_routes_are_not_served()
    {
        // Guard against a catch-all creeping in: if everything under /mentor answered 200, every
        // negative assertion above would pass for the wrong reason.
        await using var app = await ServerAppFactory.CreateAsync();

        var response = await app.Client.GetAsync("/mentor/definitely-not-a-route");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
