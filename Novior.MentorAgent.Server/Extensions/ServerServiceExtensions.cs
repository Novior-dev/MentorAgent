using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Server.Auth;
using Novior.MentorAgent.Server.Hubs;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Novior.MentorAgent.Server.Extensions;

/// <summary>
/// Extension methods for registering Novior.MentorAgent.Server services.
/// </summary>
public static class ServerServiceExtensions
{
    /// <summary>
    /// Adds the MentorAgent SignalR hub and server-side infrastructure.
    /// Call this after <c>AddMentorAgent()</c> in Program.cs.
    /// Then call <c>app.MapMentorAgentServer()</c> to expose the endpoints.
    ///
    /// <code>
    /// builder.Services.AddMentorAgent(options => { ... });
    /// builder.Services.AddMentorAgentServer();
    /// </code>
    ///
    /// <para>
    /// It also calls <c>AddMentorAgentMcp()</c> and <c>AddMentorAgentA2A()</c>, which register the MCP
    /// server and the A2A agent when <c>McpServerEnabled</c> / <c>A2AServerEnabled</c> are set, for
    /// <c>app.MapMentorAgentMcp()</c> and <c>app.MapMentorAgentA2A()</c>.
    /// </para>
    /// </summary>
    public static IServiceCollection AddMentorAgentServer(this IServiceCollection services)
    {
        // Until 1.0.0-rc.15 AddMentorAgent registered both; they moved here with their ASP.NET Core
        // transports (story 105). Each does nothing when its flag is off.
        services.AddMentorAgentMcp();
        services.AddMentorAgentA2A();

        // AddHubOptions<MentorHub> is what flips the internal "user has set values" flag on
        // HubOptions<MentorHub>. Without it SignalR ignores the per-hub options entirely and
        // falls back to the global ones, so the size raised below would never be applied.
        services.AddSignalR().AddHubOptions<MentorHub>(_ => { });
        services.AddSingleton<HubPageContextStore>();
        services.AddScoped<HubStateServiceDecorator>();

        // ── Message size for image attachments (roadmap #13) ──────────────────
        // Image attachments ride inside the SendMessage hub invocation as base64. SignalR's
        // default MaximumReceiveMessageSize is 32 KB — smaller than any real photo — and when a
        // message exceeds it the server ABORTS THE CONNECTION. The symptom is brutal to diagnose:
        // the widget echoes the user bubble and then nothing happens at all — no reply, no error,
        // no busy indicator — because the invocation never reached the server.
        //
        // So size the limit from the image options the developer actually configured
        // (base64 inflates bytes by 4/3) plus headroom for the text and page-context payload.
        // Scoped to HubOptions<MentorHub> so the host's own hubs keep their limits, and only ever
        // raised — a host that deliberately set a larger value (or null = unlimited) is respected.
        services.AddOptions<HubOptions<MentorHub>>()
            .Configure<IOptions<MentorOptions>>((hub, mentorOptions) =>
            {
                var o = mentorOptions.Value;
                if (!o.EnableImageInput) return;

                var needed = (long)o.MaxImageBytes * Math.Max(1, o.MaxImagesPerMessage) * 4 / 3
                             + 512 * 1024;
                if (hub.MaximumReceiveMessageSize is { } current && current < needed)
                    hub.MaximumReceiveMessageSize = needed;
            });

        // One DI scope per SignalR connection (built once, reused per message) — see MentorHubScopes.
        services.AddSingleton<MentorHubScopes>();

        // /mentor/session hands snapshots to clients protected and bound to their owner (BUG-110).
        // Idempotent: a host that configured Data Protection (a persisted key ring) keeps its setup.
        services.AddDataProtection();

        // Which connection raised each pending confirmation, so POST /mentor/approve has something
        // to check a caller against (BUG-058). Singleton, like the approval store it complements:
        // the confirmation is raised in one DI scope and answered from another.
        services.AddSingleton<MentorApprovalOwners>();

        // ── User identity bridge ──────────────────────────────────────────────
        // Surfaces the ASP.NET Core authenticated principal (SignalR HubCallerContext.User
        // or HttpContext.User) to the MentorAgent core via the standard AuthenticationStateProvider,
        // so per-user memory, per-user rate limiting and RequiredRoles work for headless
        // (React/Vue/Angular/WASM) clients — not only in Blazor.
        //
        // TryAddScoped: never overrides an AuthenticationStateProvider the host already
        // registered (e.g. a Blazor server), so it only fills the gap when none exists.
        //
        // MentorAgent itself does not depend on winning that race (BUG-131): the hub and /mentor/chat
        // record their caller for the scope, and the core reads it before the host's provider — which,
        // in a Blazor Web App, works only inside a circuit. See MentorUserAuthentication.
        services.AddHttpContextAccessor();
        services.AddScoped<MentorServerUserContext>();
        services.TryAddScoped<AuthenticationStateProvider, ServerAuthenticationStateProvider>();
        HttpContextMentorUser.Register(services);

        return services;
    }
}
