using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace Novior.MentorAgent.Tests.Server;

/// <summary>
/// Whether a host can protect the transports that run turns, and whether it is told to (BUG-060).
///
/// <para>
/// <c>MapMentorAgentServer()</c> mapped the hub and the SSE endpoints and <b>discarded their
/// endpoint builders</b>, so a host had no supported way to require authentication on either. The
/// only route was to stop calling the method and wire every endpoint by hand.
/// </para>
///
/// <para>
/// That would be merely awkward if the default were harmless. It is not. <c>POST /mentor/chat</c>
/// runs a paid turn for an anonymous caller, and the memory key for an unauthenticated principal is
/// the literal string <c>"anonymous"</c> — so <b>every anonymous caller shares one memory
/// bucket</b>. Driven live: one anonymous request stored a private code and a second, entirely
/// separate anonymous request read it back. That is the same outcome BUG-046 was an S1 for, reached
/// by configuration rather than by a defect in the hub client.
/// </para>
///
/// <para>
/// Neither half is fixed by refusing to serve unauthenticated hosts — a single-user demo or a
/// desktop app is entitled to one. What was missing is the <i>handle</i> and the <i>warning</i>.
/// </para>
/// </summary>
public sealed class TransportProtectionTests
{
    [Fact]
    public async Task A_host_can_require_authentication_on_the_turn_running_transports()
    {
        // The handle. Without it a host that wants an authenticated hub has to abandon
        // MapMentorAgentServer entirely and re-map five endpoints by hand.
        await using var app = await ServerAppFactory.CreateAsync(
            configureTransports: e => e.RequireAuthorization());

        var res = await app.Client.PostAsJsonAsync("/mentor/chat", new { message = "ciao" });

        res.StatusCode.Should().BeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden],
            "the host asked for these endpoints to require authentication");
    }

    [Fact]
    public async Task Without_that_the_endpoints_stay_open_exactly_as_before()
    {
        // The control, and a compatibility guarantee: adding the handle must not change the default
        // for every host that does not use it.
        await using var app = await ServerAppFactory.CreateAsync();

        var res = await app.Client.PostAsJsonAsync("/mentor/chat", new { message = "ciao" });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Mapping_a_public_transport_with_memory_on_warns_about_the_shared_bucket()
    {
        // The warning. A host that never configures authentication gets one line at startup naming
        // the consequence — not an error, because the configuration is legitimate for a single-user
        // application, and not silence, because for a multi-user one it is a cross-user leak.
        await using var app = await ServerAppFactory.CreateAsync(o =>
        {
            o.UseMemoryContext = true;
            o.MemoryAutoCapture = true;
        });

        var warnings = string.Join("\n", app.Logs.Records
            .Where(r => r.Level >= Microsoft.Extensions.Logging.LogLevel.Warning)
            .Select(r => r.Message));

        // Default AnonymousIdentity is PerSession (BUG-064): nothing is shared, but every request is a
        // new key — so the warning is about cost and the rate limit, not a shared bucket (rc.11).
        warnings.Should().Contain("/mentor/chat runs a paid turn for anyone");
        warnings.Should().Contain("RateLimitPerUser does not bound them",
            "a host relying on the rate limit for anonymous callers has to be told it cannot");
        warnings.Should().NotContain("memory bucket", "with PerSession there is no shared bucket to warn about");
    }

    [Fact]
    public async Task With_a_shared_anonymous_identity_the_warning_names_the_shared_bucket()
    {
        await using var app = await ServerAppFactory.CreateAsync(o =>
        {
            o.UseMemoryContext = true;
            o.MemoryAutoCapture = true;
            o.AnonymousIdentity = Novior.MentorAgent.Options.MentorAnonymousIdentity.Shared;
        });

        var warnings = string.Join("\n", app.Logs.Records
            .Where(r => r.Level >= Microsoft.Extensions.Logging.LogLevel.Warning)
            .Select(r => r.Message));

        warnings.Should().Contain("\"anonymous\"",
            "the warning has to name the shared key, which is the thing that surprises people");
        warnings.Should().Contain("share one memory bucket",
            "and what is being shared, or the reader cannot judge whether it matters to them");
    }

    [Fact]
    public async Task A_host_with_authentication_configured_is_not_warned()
    {
        // Noise control. A warning that fires for correctly configured applications is a warning
        // people learn to filter out — the B16 skills-folder decision, applied again. "Correctly
        // configured" is what the warning reads: the endpoint itself requires authorization. A signed-in
        // caller alone is not that (this test used to set only a user, and its assertion searched for
        // "shares", a word the warning never contained, so it passed on nothing — rc.11).
        await using var app = await ServerAppFactory.CreateAsync(
            o => { o.UseMemoryContext = true; o.MemoryAutoCapture = true; },
            userId: "ada",
            configureTransports: e => e.RequireAuthorization());

        var warnings = string.Join("\n", app.Logs.Records
            .Where(r => r.Level >= Microsoft.Extensions.Logging.LogLevel.Warning)
            .Select(r => r.Message));

        warnings.Should().NotContain("/mentor/chat runs", "the warning must not fire here in either of its forms");
    }

    [Fact]
    public async Task Memory_off_is_not_warned_about_either()
    {
        // With memory off there is no bucket to share, so an open transport is a cost decision
        // rather than a privacy one — and this warning is about the privacy half.
        await using var app = await ServerAppFactory.CreateAsync(o =>
        {
            o.UseMemoryContext = false;
            o.MemoryAutoCapture = false;
        });

        var warnings = string.Join("\n", app.Logs.Records
            .Where(r => r.Level >= Microsoft.Extensions.Logging.LogLevel.Warning)
            .Select(r => r.Message));

        warnings.Should().NotContain("/mentor/chat runs", "the warning must not fire here in either of its forms");
    }
}
