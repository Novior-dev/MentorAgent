using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Server.Endpoints;
using Novior.MentorAgent.Server.Hubs;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Novior.MentorAgent.Server.Auth;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using System.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Novior.MentorAgent.Server.Extensions;

/// <summary>
/// Extension methods for mapping Novior.MentorAgent.Server endpoints.
/// </summary>
public static class ServerEndpointExtensions
{
    /// <summary>
    /// Maps all Novior.MentorAgent.Server endpoints:
    /// <list type="bullet">
    ///   <item><c>/mentor-hub</c> — SignalR hub (Blazor WASM, MAUI, console .NET)</item>
    ///   <item><c>GET /mentor/chat</c> — SSE streaming (React, Vue, fetch API, curl)</item>
    ///   <item><c>POST /mentor/chat</c> — SSE streaming with image attachments (roadmap #13)</item>
    ///   <item><c>POST /mentor/approve</c> — human-in-the-loop approve/deny (roadmap #16)</item>
    ///   <item><c>POST /mentor/cancel</c> — stop the in-flight turn of a SignalR connection</item>
    ///   <item><c>GET /mentor/admin/metrics</c> — token &amp; cost snapshot, role-gated (roadmap #9)</item>
    ///   <item><c>GET /mentor/tour</c> — onboarding tour steps (roadmap #18)</item>
    /// </list>
    ///
    /// MCP and A2A endpoints are mapped separately via <c>MapMentorAgentMcp()</c>
    /// and <c>MapMentorAgentA2A()</c>, also in this package.
    ///
    /// <code>
    /// app.MapMentorAgentServer();          // SignalR + SSE
    /// app.MapMentorAgentMcp();             // optional MCP server
    /// app.MapMentorAgentA2A();             // optional A2A agent
    /// </code>
    /// </summary>
    /// <param name="hubPath">SignalR hub path. Default: /mentor-hub</param>
    /// <param name="ssePath">SSE endpoint path. Default: /mentor/chat</param>
    /// <param name="configureTransports">
    /// Applied to the hub and both SSE endpoints — the three that <b>run a turn</b>. This is where
    /// a host requires authentication on them:
    /// <code>app.MapMentorAgentServer(configureTransports: e => e.RequireAuthorization());</code>
    ///
    /// <para>
    /// Optional, and omitting it changes nothing (BUG-060). But the default is worth understanding
    /// before leaving it: <c>POST /mentor/chat</c> runs a paid turn for an anonymous caller, and the
    /// memory key for an unauthenticated principal is the literal <c>"anonymous"</c> — so every
    /// anonymous caller shares one memory bucket, and one user's remembered facts are injected into
    /// another's turns. That is fine for a single-user demo and a cross-user leak for anything else.
    /// </para>
    /// </param>
    /// <param name="endpoints">The application's endpoint route builder.</param>
    public static IEndpointRouteBuilder MapMentorAgentServer(
        this IEndpointRouteBuilder endpoints,
        string hubPath = "/mentor-hub",
        string ssePath = "/mentor/chat",
        Action<IEndpointConventionBuilder>? configureTransports = null)
    {
        var hub = endpoints.MapHub<MentorHub>(hubPath);
        var sseGet = endpoints.MapGet(ssePath, MentorSseEndpoint.HandleAsync);
        // POST accepts a JSON body so image attachments (#13) can be sent — base64 does not
        // fit in a query string. Text-only clients can keep using GET unchanged.
        var ssePost = endpoints.MapPost(ssePath, MentorSseEndpoint.HandlePostAsync);

        if (configureTransports is not null)
        {
            configureTransports(hub);
            configureTransports(sseGet);
            configureTransports(ssePost);
        }

        // Added AFTER the host's conventions so it sees the finished metadata. Conventions run when
        // the endpoint is built, which is at application start — the right moment for a warning.
        ssePost.Add(builder => WarnIfPubliclyShared(endpoints, builder));

        // POST /mentor/approve?actionId=GUID&approved=true|false
        //
        // Resolves a pending HITL confirmation via HTTP instead of via the SignalR hub.
        // Using the hub for approval would deadlock: ASP.NET Core SignalR processes messages
        // sequentially per connection, so RespondToApproval can never run while SendMessage
        // is awaiting WaitForConfirmationAsync on the same connection.
        //
        // Only the principal the confirmation was RAISED for may answer it (BUG-058). Approving
        // runs a gated, destructive action its owner has not agreed to; denying is a denial of
        // service on the feature and needs no timing luck. An action id is 122 random bits, but the
        // documented client sends it in a query string, so it reaches access logs on the way back —
        // a value that leaks into logs cannot be the only thing guarding a gated action.
        //
        // 404 when no turn waits for the answer any more: answered already, stopped, or raised on a
        // connection that has closed (BUG-128). Up to rc.14 every answer got a 200, so a client could not
        // tell an approval that ran from one that reached nothing.
        endpoints.MapPost("/mentor/approve", async (
            Guid actionId, bool approved, IMentorStateService stateService, MentorApprovalStore store,
            MentorApprovalOwners owners, MentorHubScopes scopes, HttpContext http) =>
        {
            // No recorded owner means no hub connection raised it — an SSE client, or an in-process
            // Blazor Server host. There is nothing to compare, and failing closed would break HITL
            // for both.
            if (owners.ConnectionFor(actionId) is { } connectionId)
            {
                // The connection that raised it is gone, and its turn with it: the scope's end cancels
                // the wait. Answered before that cancellation landed, the approval would still run.
                if (scopes.TryGet(connectionId) is not { } ownerScope)
                {
                    owners.Forget(actionId);
                    return Results.NotFound();
                }

                if (OwnershipCheck(ownerScope, http) is { } denied)
                    return denied;
            }

            var answered = await AnswerAsync(stateService, store, actionId, approved);

            owners.Forget(actionId);
            return answered ? Results.Ok() : Results.NotFound();
        });

        // POST /mentor/cancel?connectionId=<SignalR connection id>
        //
        // Stops the in-flight AI turn of a SignalR connection. Like /mentor/approve this MUST be
        // HTTP and not a hub method: SignalR dispatches at most one hub invocation at a time per
        // connection (HubOptions.MaximumParallelInvocationsPerClient = 1), so CancelRequest would
        // sit in the queue behind the still-running SendMessage and only execute once the turn it
        // was meant to abort had already finished.
        //
        // Ownership-checked like its neighbours (BUG-058). Stopping someone else's turn is the
        // mildest of the three consequences, but the check is the same one and leaving it off would
        // make the rule "some endpoints, sometimes" — which is how the next one gets forgotten.
        endpoints.MapPost("/mentor/cancel", (string connectionId, MentorHubScopes scopes, HttpContext http) =>
        {
            var sp = scopes.TryGet(connectionId);
            if (sp is null) return Results.NotFound();
            if (OwnershipCheck(sp, http) is { } denied) return denied;
            sp.GetRequiredService<IMentorOrchestrator>().CancelCurrentRequest();
            return Results.Ok();
        });

        // GET  /mentor/session?connectionId=<SignalR connection id>  → the conversation snapshot
        // POST /mentor/session?connectionId=<SignalR connection id>  → restore one
        //
        // ROADMAP #34. A reconnect is a new connection id, therefore a new DI scope, therefore a
        // new conversation — deliberate, since that per-connection scope is exactly what keeps two
        // browsers apart. The documented remedy has always been SerializeSessionAsync /
        // RestoreSessionAsync, but those live on the orchestrator inside the server's scope: on
        // WebAssembly they throw NotSupportedException naming that reason, and a React, Vue or MAUI
        // client could not reach them at all. This is the transport the remedy needed.
        //
        // HTTP rather than a hub method for the same reason as approve and cancel: SignalR
        // dispatches one invocation at a time per connection, so a hub call would queue behind a
        // turn that may itself be waiting for the restore.
        //
        // A 404 immediately after connecting is not necessarily a wrong id: the client's StartAsync
        // completes on the handshake, and the server registers the connection's scope in
        // OnConnectedAsync, so a request that overtakes it finds nothing. Restore after the first
        // hub call, or treat an early 404 as "retry once".
        endpoints.MapGet("/mentor/session", async (
            string connectionId, MentorHubScopes scopes, HttpContext http, CancellationToken ct) =>
        {
            var sp = scopes.TryGet(connectionId);
            if (sp is null) return Results.NotFound();
            if (OwnershipCheck(sp, http) is { } denied) return denied;

            var snapshot = await sp.GetRequiredService<IMentorOrchestrator>().SerializeSessionAsync(ct);

            // No conversation yet is not an error, and it must not be answered with an empty
            // snapshot: a client that stores that overwrites a good saved conversation with
            // nothing (B22).
            if (snapshot is null) return Results.NoContent();

            // Opaque to the client, and bound to the connection's owner (BUG-110).
            return Results.Ok(SessionSnapshotProtection.Protect(
                snapshot.Value, SessionSnapshotProtection.For(http.RequestServices, OwnerOf(sp))));
        });

        endpoints.MapPost("/mentor/session", async (
            string connectionId, MentorHubScopes scopes, HttpContext http, CancellationToken ct) =>
        {
            var sp = scopes.TryGet(connectionId);
            if (sp is null) return Results.NotFound();

            // The owner is checked BEFORE the body is read (BUG-110): a JsonElement handler parameter
            // was bound by the framework first, so a stranger's malformed body got a 400 the ownership
            // check never saw.
            if (OwnershipCheck(sp, http) is { } denied) return denied;

            if (!http.Request.HasJsonContentType())
                return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);

            JsonElement body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<JsonElement>(http.Request.Body, cancellationToken: ct);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { error = "The body is not JSON." });
            }

            // Unprotected with the OWNER's purpose: a snapshot saved for another user, edited, or issued
            // by another application does not open, and all of them get the same answer.
            if (!SessionSnapshotProtection.TryUnprotect(
                    body, SessionSnapshotProtection.For(http.RequestServices, OwnerOf(sp)),
                    out var snapshot, out var error))
                return Results.BadRequest(new { error });

            try
            {
                await sp.GetRequiredService<IMentorOrchestrator>().RestoreSessionAsync(snapshot, ct);
                return Results.Ok();
            }
            catch (ArgumentException ex)
            {
                // BUG-026: the framework's deserializer answers arbitrary JSON with a valid *empty*
                // session, so MentorAgent stamps its snapshots and refuses anything else before
                // touching the live conversation. A snapshot this host cannot use — history kept on the
                // service vs locally, another history provider — is refused the same way (BUG-110). The
                // caller's input being wrong is a 400, not a 500, and never a wiped conversation.
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // GET /mentor/admin/metrics — admin token & cost snapshot (roadmap #9) for headless
        // dashboards (React/Vue). Gated by MentorOptions.DashboardRole; set it to "" to leave the
        // endpoint open (development only). Never expose this to end users.
        var role = endpoints.ServiceProvider.GetService<IOptions<MentorOptions>>()?.Value.DashboardRole ?? "Admin";
        // A registered IMentorMetricsStore may serve an external aggregate (Prometheus/Azure Monitor);
        // otherwise we return the live in-RAM snapshot (roadmap #9 durable dashboard).
        var metricsEndpoint = endpoints.MapGet("/mentor/admin/metrics",
            async (IMentorMetrics metrics, IMentorMetricsStore store, CancellationToken ct) =>
                Results.Ok(await store.QueryAsync(ct) ?? metrics.GetSnapshot()));
        if (!string.IsNullOrEmpty(role))
            metricsEndpoint.RequireAuthorization(new AuthorizeAttribute { Roles = role });

        // GET /mentor/tour — onboarding tour steps (roadmap #18) for headless and WASM clients,
        // which have no access to the discovery data the tour is generated from.
        //
        // Deliberately unauthenticated and safe to be: the payload is the same public screen names
        // and capability descriptions the assistant already states in conversation, it is identical
        // for every user, and it carries no metrics, no counts and no per-user data. Generation
        // happens once per process, so this cannot be used to run up model cost either.
        endpoints.MapGet("/mentor/tour", async (IMentorTour tour, CancellationToken ct) =>
            Results.Ok(await tour.GetStepsAsync(ct)));

        return endpoints;
    }

    /// <summary>
    /// Warns, once at startup, when the transports are open to anyone <b>and</b> memory is on
    /// (BUG-060).
    ///
    /// <para>
    /// The memory key for an unauthenticated principal is the literal string <c>"anonymous"</c>, so
    /// with no authentication configured every caller shares one bucket and
    /// <c>MemoryAutoCapture</c> — on by default — keeps writing into it. Measured live: one
    /// anonymous request stored a private code and a separate anonymous request read it back. It is
    /// the outcome BUG-046 was an S1 for, reached by configuration rather than by a defect.
    /// </para>
    ///
    /// <para>
    /// A warning rather than a refusal, because a single-user demo or a desktop host is entitled to
    /// exactly this configuration. And only when memory is on: with it off an open transport is a
    /// cost decision rather than a privacy one, and a warning that fires for correctly configured
    /// applications is one people learn to filter out.
    /// </para>
    /// </summary>
    /// <summary>
    /// Guards the warning below, <b>per application</b>. ASP.NET Core may rebuild its endpoint data
    /// source, which runs every convention again — a startup warning that appears twice reads like
    /// two problems.
    ///
    /// <para>
    /// Keyed on the service provider rather than a plain static: a process-wide flag would mean the
    /// first application to map a server silences every later one, which is wrong for a host with
    /// two, and made this warning's own test depend on which test ran first. A weak table also lets
    /// the entry go when the application does.
    /// </para>
    /// </summary>
    private static readonly ConditionalWeakTable<IServiceProvider, object> _sharedBucketWarned = new();

    private static void WarnIfPubliclyShared(IEndpointRouteBuilder endpoints, EndpointBuilder builder)
    {
        var options = endpoints.ServiceProvider.GetService<IOptions<MentorOptions>>()?.Value;
        if (options is null) return;
        if (!options.UseMemoryContext && !options.MemoryAutoCapture) return;

        // The question is not "did the host register an authentication scheme" — plenty of
        // applications register one and still leave this endpoint open, which is exactly the
        // dangerous case. It is whether THIS endpoint ends up requiring authorization.
        if (builder.Metadata.OfType<IAuthorizeData>().Any()) return;
        if (!_sharedBucketWarned.TryAdd(endpoints.ServiceProvider, new object())) return;

        var logger = endpoints.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("Novior.MentorAgent.Server");

        // What an open endpoint risks depends on how anonymous callers are keyed (BUG-064): with
        // Shared they all write one memory; with PerSession (the default) nothing is shared, but every
        // request is a new key, so neither memory nor RateLimitPerUser follows a person.
        if (options.AnonymousIdentity == MentorAnonymousIdentity.Shared)
            logger?.LogWarning(
                "[MentorAgent] /mentor/chat runs a turn for anyone who can reach it, memory is enabled, and " +
                "AnonymousIdentity = Shared: every unauthenticated caller is keyed as \"anonymous\" and they all " +
                "share one memory bucket — one user's remembered facts reach another's turns. Fine for a " +
                "single-user application; a cross-user leak for anything else. Require authentication — " +
                "MapMentorAgentServer(configureTransports: e => e.RequireAuthorization()) — or use PerSession.");
        else
            logger?.LogWarning(
                "[MentorAgent] /mentor/chat runs a paid turn for anyone who can reach it. Unauthenticated callers " +
                "get a new key per request or connection (AnonymousIdentity = PerSession), so memory does not " +
                "follow them and RateLimitPerUser does not bound them. Require authentication — " +
                "MapMentorAgentServer(configureTransports: e => e.RequireAuthorization()) — if the endpoint is public.");
    }

    /// <summary>
    /// Answers a confirmation and reports whether a turn was still waiting for it (BUG-128, BUG-129).
    /// </summary>
    private static async Task<bool> AnswerAsync(
        IMentorStateService stateService, MentorApprovalStore store, Guid actionId, bool approved)
    {
        if (stateService is MentorStateService local)
            return local.Answer(actionId, approved);

        // A state service of the host's own answers through the interface, which says nothing back;
        // the store says whether anybody was waiting.
        var waiting = store.IsPending(actionId);
        if (approved) await stateService.ConfirmAsync(actionId);
        else stateService.Cancel(actionId);
        return waiting;
    }

    /// <summary>The principal that opened the connection, or null when the host has no authentication.</summary>
    private static ClaimsPrincipal? OwnerOf(IServiceProvider connectionScope)
        => connectionScope.GetService<MentorServerUserContext>()?.User;

    /// <summary>
    /// Refuses a <c>/mentor/session</c> request that is not made by the principal who opened the
    /// connection, or <see langword="null"/> when it may proceed (BUG-056).
    ///
    /// <para>
    /// This endpoint is unlike its neighbours. <c>/mentor/approve</c> and <c>/mentor/cancel</c>
    /// <i>act</i> on a turn; this one <b>returns the conversation</b>, and on <c>POST</c> replaces
    /// it. The connection id is 128 bits of randomness but it is not a secret — it travels in a
    /// query string, so it reaches access logs, proxy logs and browser history, and the owning page
    /// can read it from its own client. A capability that leaks into logs cannot be the only thing
    /// between a stranger and someone's transcript.
    /// </para>
    ///
    /// <para>
    /// The rule is <b>ownership</b>, not merely authentication: a signed-in stranger is still a
    /// stranger, which is what a plain <c>RequireAuthorization()</c> would have let through. When
    /// the connection carries no authenticated principal — a host with no authentication at all —
    /// there is nothing to compare and behaviour is unchanged, because the connection id is already
    /// the only notion of "whose" such an application has.
    /// </para>
    /// </summary>
    private static IResult? OwnershipCheck(IServiceProvider connectionScope, HttpContext http)
    {
        var owner = OwnerOf(connectionScope);
        if (owner?.Identity?.IsAuthenticated != true)
            return null;   // nothing to compare against

        var caller = http.User;
        if (caller?.Identity?.IsAuthenticated != true)
            return Results.Unauthorized();

        // NameIdentifier, then sub, oid and the name (BUG-130): a Windows principal has no
        // NameIdentifier, and up to rc.14 its owner was refused its own confirmations, Stop and session.
        var ownerId = PrincipalKey.Of(owner);

        // A missing id on either side is not a match. Treating "no id" as equal would make two
        // anonymous-but-authenticated principals indistinguishable.
        return ownerId is not null && ownerId == PrincipalKey.Of(caller)
            ? null
            : Results.Forbid();
    }
}
