using System.Net.Http.Json;
using FluentAssertions;
using Novior.MentorAgent.Tests.Harness;
using Xunit;

namespace Novior.MentorAgent.Tests.Server;

/// <summary>
/// What an SSE client actually receives, frame by frame.
///
/// <para>
/// <b>Why this file exists.</b> The three existing SSE tests in <see cref="ServerEndpointTests"/>
/// assert that the endpoint answers <c>200 text/event-stream</c> and that the body contains
/// <c>data:</c>. That proves the transport works; it proves nothing about the <em>protocol</em>. A
/// cross-app parity run found the gap: the identical request that renders cards over SignalR
/// produced, over SSE, a stream of 259 <c>chunk</c> frames and one <c>completed</c> — no cards —
/// while the server log read <c>[MentorAgent:Cards] 'show_order_cards' returned 5 card(s)</c>.
/// </para>
///
/// <para>
/// The shape of the failure is what makes it dangerous and is the reason every frame gets its own
/// assertion here: the missing frame does not produce an error. The turn ends with
/// <c>{"type":"completed"}</c>, so the client believes it succeeded. On the three surfaces this
/// package publishes — hub, SSE, widget — a client can only be sure of what it is handed, and
/// "completed" must never be handed to a client whose turn was blocked or whose outcome travelled
/// on a channel this endpoint does not forward.
/// </para>
/// </summary>
public sealed class SseFrameParityTests
{
    /// <summary>
    /// Drives one SSE turn and returns what arrived, reading incrementally.
    ///
    /// <para>
    /// Reading the body as one string is what an ordinary HTTP test does and it is wrong here: a
    /// turn parked on a confirmation deliberately holds the response open, so the read would block
    /// until the deadline and then throw away everything already received — including the frame
    /// under test. This reads as it goes and stops at <paramref name="stopWhen"/>, so a parked turn
    /// is measured rather than timed out.
    /// </para>
    ///
    /// <para>
    /// The deadline is mandatory rather than tidy. A test that can park must be able to stop
    /// parking; BUG-018 cost eleven minutes per test for want of one.
    /// </para>
    /// </summary>
    private static async Task<string> StreamAsync(
        ServerAppFactory app, string message, string? stopWhen = null, int seconds = 15)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        var received = new System.Text.StringBuilder();
        try
        {
            using var response = await app.Client.GetAsync(
                "/mentor/chat?message=" + Uri.EscapeDataString(message),
                HttpCompletionOption.ResponseHeadersRead,
                cts.Token);

            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            var buffer = new byte[4096];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cts.Token);
                if (read == 0) break;
                received.Append(System.Text.Encoding.UTF8.GetString(buffer, 0, read));
                if (stopWhen is not null && received.ToString().Contains(stopWhen)) break;
            }
        }
        catch (OperationCanceledException) { /* deadline — assert on what arrived */ }

        return received.ToString();
    }

    // ── Cards ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_card_returning_turn_writes_a_cards_frame()
    {
        // The server README documents this frame with a worked example and a transport table:
        // "SSE | a {"type":"cards","cards":[…]} frame in the stream". It is the one non-text frame
        // the endpoint has always claimed to emit.
        await using var app = await ServerAppFactory.CreateAsync();
        app.Chat.CallTool("show_order_card", new { orderId = 7 }).Say("Here is the order.");

        var body = await StreamAsync(app, "show me order 7");

        body.Should().Contain("\"type\":\"cards\"",
            "the card tool ran and produced cards — an SSE client that never receives them is told " +
            "the turn completed while the whole point of the turn was lost");
    }

    [Fact]
    public async Task The_cards_frame_carries_the_card_payload_not_just_the_type()
    {
        await using var app = await ServerAppFactory.CreateAsync();
        app.Chat.CallTool("show_order_card", new { orderId = 7 }).Say("Here is the order.");

        var body = await StreamAsync(app, "show me order 7");

        // camelCase with string enums, matching the documented example frame. A client reading
        // "kind": 1 would be reading a number whose meaning shifts the day a member is inserted.
        body.Should().Contain("\"kind\":\"order\"",
            "the documented frame is camelCase with string enums");
    }

    // ── Navigation ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_navigation_turn_writes_a_navigation_frame()
    {
        // Server README, built-in tools table: "navigate_to … Over SignalR/SSE this surfaces as the
        // NavigationRequested event — your client decides how to route (React Router, Angular
        // Router, MAUI Shell)". navigate_to is a core tool: it is registered on every application
        // that declares a page, so this is not an opt-in path a host can avoid.
        await using var app = await ServerAppFactory.CreateAsync();
        app.Chat.CallTool("navigate_to", new { url = "/reports" }).Say("Taking you there.");

        var body = await StreamAsync(app, "take me to the reports page");

        body.Should().Contain("\"type\":\"navigation\"",
            "the model told the user it was navigating; a client that receives no frame leaves the " +
            "user on the old page while the assistant claims otherwise");
    }

    [Fact]
    public async Task The_navigation_frame_carries_the_url()
    {
        await using var app = await ServerAppFactory.CreateAsync();
        app.Chat.CallTool("navigate_to", new { url = "/reports" }).Say("Taking you there.");

        var body = await StreamAsync(app, "take me to the reports page");

        body.Should().Contain("/reports", "a navigation frame without the destination is not actionable");
    }

    // ── Confirmations ───────────────────────────────────────────────────────────

    [Fact]
    public async Task A_turn_blocked_on_a_confirmation_says_so_instead_of_reporting_success()
    {
        // issue_refund is confirmation-only in the test app (cancel_order also carries roles, which
        // would refuse first and never reach the gate under test).
        await using var app = await ServerAppFactory.CreateAsync();
        app.Chat.CallTool("issue_refund", new { orderId = 7 }).Say("Refund issued.");

        var body = await StreamAsync(app, "refund order 7", stopWhen: "\"type\":\"confirmationRequired\"");

        body.Should().Contain("\"type\":\"confirmationRequired\"",
            "the action was withheld pending a human decision — reporting the turn as completed " +
            "tells the client the opposite of what happened");
        body.Should().NotContain("\"type\":\"completed\"",
            "the turn is parked, not finished; completed here is the claim that made this invisible");
    }

    [Fact]
    public async Task The_confirmation_frame_carries_the_action_id_the_approve_endpoint_needs()
    {
        await using var app = await ServerAppFactory.CreateAsync();
        app.Chat.CallTool("issue_refund", new { orderId = 7 }).Say("Refund issued.");

        var body = await StreamAsync(app, "refund order 7", stopWhen: "\"type\":\"confirmationRequired\"");

        // POST /mentor/approve?actionId=…&approved=true is transport-agnostic and already works
        // cross-origin (BUG-009). Without the id in the frame there is nothing for the client to
        // send, which is what makes the missing frame a dead end rather than a cosmetic gap.
        body.Should().Contain("\"actionId\"",
            "the approve endpoint is keyed by action id; a confirmation frame without one cannot be answered");
    }

    [Fact]
    public async Task Approving_over_http_resumes_the_parked_stream_and_runs_the_tool()
    {
        // The whole round trip, which is the only thing that proves HITL works on this transport:
        // frame out, verdict in over a second HTTP request, parked turn resumes on the first one.
        // The approval store is a singleton precisely so two requests can meet on one action id.
        await using var app = await ServerAppFactory.CreateAsync();
        app.Chat.CallTool("issue_refund", new { orderId = 7 }).Say("Refund issued.");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var received = new System.Text.StringBuilder();

        using var response = await app.Client.GetAsync(
            "/mentor/chat?message=" + Uri.EscapeDataString("refund order 7"),
            HttpCompletionOption.ResponseHeadersRead,
            cts.Token);

        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        var buffer = new byte[4096];
        var approved = false;

        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cts.Token);
                if (read == 0) break;
                received.Append(System.Text.Encoding.UTF8.GetString(buffer, 0, read));

                var text = received.ToString();
                if (!approved && text.Contains("\"type\":\"confirmationRequired\""))
                {
                    var actionId = ActionIdOf(text);
                    var verdict = await app.Client.PostAsync(
                        $"/mentor/approve?actionId={actionId}&approved=true", content: null, cts.Token);
                    verdict.IsSuccessStatusCode.Should().BeTrue();
                    approved = true;
                }

                if (text.Contains("\"type\":\"completed\"")) break;
            }
        }
        catch (OperationCanceledException) { /* deadline — assert on what arrived */ }

        var body = received.ToString();
        approved.Should().BeTrue("the confirmation frame has to arrive before it can be answered");
        body.Should().Contain("\"type\":\"completed\"",
            "once the verdict is in, the same stream carries the rest of the turn to its end");
    }

    /// <summary>Pulls the action id out of the confirmation frame, the way a client would.</summary>
    private static string ActionIdOf(string body)
    {
        var line = body.Split('\n').First(l => l.Contains("\"type\":\"confirmationRequired\""));
        return System.Text.Json.JsonDocument.Parse(line["data: ".Length..])
            .RootElement.GetProperty("actionId").GetString()!;
    }

    // ── The frames that were already right stay right ────────────────────────────

    [Fact]
    public async Task An_ordinary_turn_still_streams_chunks_and_completes()
    {
        await using var app = await ServerAppFactory.CreateAsync();
        app.Chat.Say("Hello from the server");

        var body = await StreamAsync(app, "hi");

        body.Should().Contain("\"type\":\"chunk\"");
        body.Should().Contain("\"type\":\"completed\"",
            "an ordinary turn is exactly the case where completed is the truth");
    }

    [Fact]
    public async Task A_client_that_ignores_unknown_frames_still_receives_the_whole_answer()
    {
        // The additive property every new frame has to keep: the text is complete on its own, so a
        // client written against the original three-frame protocol is not broken by any of this.
        await using var app = await ServerAppFactory.CreateAsync();
        app.Chat.CallTool("show_order_card", new { orderId = 7 }).Say("Here is the order.");

        var body = await StreamAsync(app, "show me order 7");

        var text = string.Concat(body
            .Split('\n')
            .Where(l => l.StartsWith("data: ") && l.Contains("\"type\":\"chunk\""))
            .Select(l => System.Text.Json.JsonDocument.Parse(l[6..]).RootElement
                .GetProperty("text").GetString()));

        text.Should().Contain("Here is the order.");
    }
}
