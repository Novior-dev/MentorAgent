using Novior.MentorAgent.Core;
using Novior.MentorAgent.Core.Mcp;
using Novior.MentorAgent.Memory;
using Novior.MentorAgent.Middleware;
using Novior.MentorAgent.Observability;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Providers;
using Novior.MentorAgent.Rag;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Novior.MentorAgent.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers MentorAgent in the DI container.
    ///
    /// Usage in Program.cs:
    /// <code>
    /// builder.Services.AddMentorAgent(options =>
    /// {
    ///     options.ChatClient  = new OpenAIClient("sk-...").GetChatClient("gpt-4o").AsIChatClient();
    ///     options.AppName     = "MyApp";
    ///     options.Language    = MentorLanguage.Italian;
    ///     options.ScanAssemblies = [typeof(Program).Assembly];
    /// });
    /// </code>
    ///
    /// Optional MCP server (with the Novior.MentorAgent.Server package):
    /// <code>
    /// options.McpServerEnabled = true;
    /// // then: builder.Services.AddMentorAgentMcp();  and  app.MapMentorAgentMcp();
    /// </code>
    ///
    /// Optional A2A server (with the Novior.MentorAgent.Server package):
    /// <code>
    /// options.A2AServerEnabled = true;
    /// // then: builder.Services.AddMentorAgentA2A();  and  app.MapMentorAgentA2A();
    /// </code>
    ///
    /// <para>
    /// <paramref name="configure"/> runs <b>twice</b>: once now, on a snapshot, for the options that
    /// decide what gets registered — <c>EnableObservability</c>, <c>ObservabilitySourceName</c>,
    /// <c>McpServerEnabled</c>, <c>A2AServerEnabled</c>, and <c>AppName</c> as the MCP server's name —
    /// and once more when the options are first built. Set those options in this callback: a
    /// <c>Configure&lt;MentorOptions&gt;</c> or <c>PostConfigure</c> registered afterwards does not reach
    /// them. The A2A agent card is built from the real options, when <c>MapMentorAgentA2A</c> runs. Keep
    /// the callback free of side effects that must happen once. If it throws on the snapshot, those
    /// options keep their defaults and a warning reports the exception when the host starts.
    /// </para>
    ///
    /// <para>
    /// The options are validated when a generic host (ASP.NET Core, a worker) starts: an invalid
    /// configuration stops it with the reason. Without one (a MAUI app) they are validated the first
    /// time they are read.
    /// </para>
    /// </summary>
    public static IServiceCollection AddMentorAgent(
        this IServiceCollection services,
        Action<MentorOptions> configure)
    {
        // ── Read options early (feature flags needed at registration time) ─────
        // We snapshot the options to decide which optional services to register.
        // The formal registration via AddOptions<MentorOptions>() follows below, so the callback
        // runs twice: here, and on the real options. A callback that throws here leaves the
        // registration-time options at their defaults; the exception is kept and reported as a
        // warning when the host starts (MentorWarmUpService), not swallowed.
#pragma warning disable CS8618 // required props set below
        var snapshot = new MentorOptions { AppName = "_preview_", ScanAssemblies = [] };
#pragma warning restore CS8618
        Exception? snapshotError = null;
        try { configure(snapshot); } catch (Exception ex) { snapshotError = ex; }

        // ── Options ───────────────────────────────────────────────────────────
        // ValidateOnStart: a generic host (ASP.NET Core, a worker) that starts with an invalid
        // configuration fails then, with Validate()'s message — not at the first visitor. Without
        // a generic host (a MAUI app, a bare ServiceProvider) the options are still validated
        // the first time they are read.
        services.AddOptions<MentorOptions>()
            .Configure(configure)
            .PostConfigure(o => o.Validate())
            .ValidateOnStart();

        // ── Core Singleton ────────────────────────────────────────────────────
        services.AddSingleton<MentorDiscoveryService>();
        services.AddSingleton<MentorPromptBuilder>();
        services.AddSingleton<MentorRateLimiter>();
        services.AddSingleton<MentorTokenUsage>();   // Wave 1 #1 — token measurement
        services.AddScoped<MentorSessionTokenUsage>(); // story 171 — the same calls, counted for one session
        services.AddSingleton<MentorMetrics>();       // Wave 3 #9 — dashboard metrics sink
        services.AddSingleton<Novior.MentorAgent.Abstractions.Interfaces.IMentorMetrics>(
            sp => sp.GetRequiredService<MentorMetrics>());

        // Wave 3 #9 — durable dashboard: a custom IMentorMetricsStore registered before AddMentorAgent()
        // makes the dashboard survive restarts (local file/DB) or read an external aggregate
        // (Prometheus/Azure Monitor). TryAdd keeps that custom store; only then run the persistence
        // service (no timer overhead in the default RAM-only setup).
        bool hasMetricsStore = services.Any(
            d => d.ServiceType == typeof(Novior.MentorAgent.Abstractions.Interfaces.IMentorMetricsStore));
        services.TryAddSingleton<Novior.MentorAgent.Abstractions.Interfaces.IMentorMetricsStore, NullMetricsStore>();
        if (hasMetricsStore)
            services.AddHostedService<MentorMetricsPersistenceService>();

        // Wave 3 #8 — OpenTelemetry source/meter (only when observability is enabled). "MentorAgent" is the
        // documented default name, not a namespace: it stays when the namespace changes (ContractNameTests).
        if (snapshot.EnableObservability)
            services.AddSingleton(new MentorTelemetry(
                string.IsNullOrWhiteSpace(snapshot.ObservabilitySourceName)
                    ? "MentorAgent" : snapshot.ObservabilitySourceName));

        // Wave 4 #12 — typed/structured generation.
        services.AddSingleton<Novior.MentorAgent.Abstractions.Interfaces.IMentorStructured, MentorStructuredService>();
        // Wave 4 #10 — prompt/regression evaluation harness.
        services.AddSingleton<Novior.MentorAgent.Evaluation.MentorEvaluator>();
        // #18 — onboarding tour. TryAdd: an IMentorTour registered before AddMentorAgent() is a
        // hand-written tour and must win over the generated one.
        services.TryAddSingleton<Novior.MentorAgent.Abstractions.Interfaces.IMentorTour, MentorTourService>();
        services.AddSingleton<Novior.MentorAgent.Abstractions.Core.MentorLocalizer>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<MentorOptions>>();
            // Read on every call: a change of MentorOptions.Language while the application runs applies to every text.
            return new Novior.MentorAgent.Abstractions.Core.MentorLocalizer(() => opts.Value.Language);
        });

        // ── Middleware ────────────────────────────────────────────────────────
        services.AddScoped<MentorAgentUIMiddleware>(sp =>
        {
            // The user is read through MentorUserAuthentication, like memory and the context providers (BUG-131).
            var middleware = MentorUserAuthentication.For(sp) is { } auth
                ? ActivatorUtilities.CreateInstance<MentorAgentUIMiddleware>(sp, auth)
                : ActivatorUtilities.CreateInstance<MentorAgentUIMiddleware>(sp);
            middleware.Origin = sp.GetRequiredService<MentorTurnOrigin>();   // BUG-097
            middleware.TurnLanguage = sp.GetRequiredService<MentorTurnLanguage>();
            // The gate's decisions for the action observers (story 180).
            middleware.Decisions = sp.GetRequiredService<MentorActionDecisions>();
            middleware.Turn      = sp.GetRequiredService<MentorTurnContext>();
            middleware.Session   = sp.GetRequiredService<MentorSessionIdentity>();
            // The application's action policies (stories 311, 153).
            middleware.Policies  = sp.GetRequiredService<MentorActionPolicies>();
            // The application's governor of its actions (story 1549).
            middleware.Governance = sp.GetRequiredService<MentorActionGovernance>();
            return middleware;
        });

        // Story 180 — the gate's decisions for every IMentorActionObserver of the process, and the turn they
        // belong to. TryAdd: a test hands its own, with a shorter timeout.
        services.TryAddSingleton<MentorActionDecisions>();
        services.AddScoped<MentorTurnContext>();

        // Stories 311, 153 — every IMentorActionPolicy of the process, asked by one rule on every surface; and the
        // application's actions as MentorAgent sees them.
        services.TryAddSingleton<MentorActionPolicies>();
        services.TryAddSingleton<MentorActionGovernance>();
        services.TryAddSingleton<IMentorActionCatalog, MentorActionCatalog>();

        // Wave 5 #16 — single source of truth for "does this tool need approval, and what does the
        // banner say?", shared by the middleware (blocking flow), the orchestrator (native AF flow)
        // and McpToolsBuilder (which registers gated MCP tool names).
        services.AddScoped<MentorConfirmationBuilder>();

        // ── Memory ────────────────────────────────────────────────────────────
        // TryAdd — a custom IMentorMemoryStore registered before AddMentorAgent()
        // will not be overwritten.
        services.TryAddSingleton<IMentorMemoryStore, InMemoryMentorMemoryStore>();
        // One per process: it counts sessions that reach the shared "anonymous" bucket (BUG-064).
        services.TryAddSingleton<AnonymousMemoryWatch>();

        // ── Embeddings: one vector per text, not per consumer ─────────────────
        // Stable texts (tool descriptions, routing exemplars) are identical for every user and are
        // embedded once per process; the user's message is embedded once per turn and shared by
        // routing, tool filtering, memory relevance and — through IMentorQueryEmbedding — a host's
        // own RAG source.
        services.TryAddSingleton<MentorEmbeddingCache>();
        // The configuration summary belongs to the application, not to the visitor (E2).
        services.TryAddSingleton<CoordinatorAnnouncement>();
        // Remote agent cards are metadata, identical for every visitor: fetched once per
        // process, with a backoff for the ones that do not answer (C2).
        services.TryAddSingleton<A2ACardCache>();
        // One connection per shared MCP server for the whole process instead of one per
        // visitor (C1). TryAdd on the factory is the seam tests replace.
        services.TryAddSingleton<IMcpSessionFactory>(sp =>
            new McpSessionFactory(sp.GetRequiredService<ILoggerFactory>()));
        services.TryAddSingleton<McpClientPool>();

        // C3 — one coordinator built at start-up so the first visitor does not pay for the
        // catalogue embeddings, the MCP connections and the agent cards. Registered always and
        // decided on the real options when the host starts: read from the snapshot, a callback
        // that threw before `WarmUpAtStartup = true` lost the warm-up without a word.
        services.AddHostedService<MentorWarmUpService>();
        services.AddScoped<TurnQueryEmbedding>();
        services.AddScoped<Novior.MentorAgent.Rag.IMentorQueryEmbedding>(sp => sp.GetRequiredService<TurnQueryEmbedding>());

        // One id per session (circuit / connection / app run): the identity of a visitor who has not
        // signed in, unless the host asked for the shared bucket.
        services.AddScoped<MentorSessionIdentity>();

        // Who is asking: a person at the widget, or another agent over A2A (set by the A2A handler).
        services.AddScoped<MentorTurnOrigin>();

        // The language of the turn: the page's, when the widget sent one, else MentorOptions.Language (set by the orchestrator).
        services.AddScoped<MentorTurnLanguage>();

        // Where the context of each request goes when it is the user's last message (SessionContextPlacement).
        services.AddScoped<MentorSessionContext>();

        // One stack trace per cause per turn: a single provider failure used to be met, and
        // logged, by six components in a row (E1).
        services.AddScoped<TurnFaultLog>();

        // Who called through a MentorAgent transport (hub, /mentor/chat, A2A), read before the host's
        // AuthenticationStateProvider — see MentorUserAuthentication (BUG-131).
        services.AddScoped<MentorTransportUser>();

        // Story 155 — the tenant of each scope, and of each MCP call: the application's resolver, or one tenant as before.
        services.TryAddSingleton<IMentorTenantResolver, MentorSingleTenantResolver>();
        services.AddScoped<MentorTenantContext>();
        services.AddScoped<IMentorTenant>(sp => sp.GetRequiredService<MentorTenantContext>());

        services.AddScoped<MentorMemoryService>(sp => new MentorMemoryService(
            sp.GetRequiredService<IMentorMemoryStore>(),
            sp.GetRequiredService<IOptions<MentorOptions>>(),
            sp.GetRequiredService<ILogger<MentorMemoryService>>(),
            MentorUserAuthentication.For(sp))
        {
            AnonymousWatch  = sp.GetRequiredService<AnonymousMemoryWatch>(),
            QueryEmbedding  = sp.GetRequiredService<TurnQueryEmbedding>(),
            SessionIdentity = sp.GetRequiredService<MentorSessionIdentity>(),
            Origin          = sp.GetRequiredService<MentorTurnOrigin>(),
            Tenant          = sp.GetRequiredService<MentorTenantContext>(),
        });

        // ── Page Context ──────────────────────────────────────────────────────
        services.AddScoped<IMentorPageContext, MentorPageContext>();

        // ── Providers ─────────────────────────────────────────────────────────
        services.AddScoped<AppContextProvider>(sp =>
        {
            var options    = sp.GetRequiredService<IOptions<MentorOptions>>();
            var pageCtx    = sp.GetRequiredService<IMentorPageContext>();
            var memory     = sp.GetRequiredService<MentorMemoryService>();
            var nav        = sp.GetService<NavigationManager>();
            var auth       = MentorUserAuthentication.For(sp);
            return new AppContextProvider(options, pageCtx, memory, nav, auth, sp.GetService<TimeProvider>())
            {
                Origin         = sp.GetRequiredService<MentorTurnOrigin>(),
                SessionContext = sp.GetRequiredService<MentorSessionContext>(),
            };
        });

        // ── Builders ──────────────────────────────────────────────────────────
        services.AddScoped<HandoffBuilder>();
        services.AddScoped<GroupChatBuilder>();

        // ── State Service + Approval Store ───────────────────────────────────
        // MentorApprovalStore is Singleton so that the TCS registered in the SendMessage
        // hub scope survives into the RespondToApproval hub scope (SignalR creates a new
        // DI scope per hub method invocation — Scoped dictionaries would be empty).
        // MentorStateService stays Scoped (per-connection state, events, etc.).
        services.AddSingleton<MentorApprovalStore>();
        services.AddScoped<MentorStateService>();
        services.AddScoped<IMentorStateService>(sp => sp.GetRequiredService<MentorStateService>());
        services.AddScoped<IApprovalStore>(sp => sp.GetRequiredService<MentorStateService>());

        // ── Session Manager ───────────────────────────────────────────────────
        services.AddScoped<IMentorSessionManager, MentorSessionManager>();

        // ── Orchestrator ──────────────────────────────────────────────────────
        services.AddScoped<MentorOrchestrator>();
        services.AddScoped<IMentorOrchestrator>(sp => sp.GetRequiredService<MentorOrchestrator>());

        // ── Widget Options (UI-only subset — used by ChatWidget in Abstractions) ─
        services.AddOptions<MentorWidgetOptions>()
            .Configure<IOptions<MentorOptions>>((wOpts, mOpts) =>
            {
                var o = mOpts.Value;
                wOpts.BotName               = o.BotName;
                wOpts.WelcomeMessage        = o.WelcomeMessage;
                wOpts.ShowAiDisclosure      = o.ShowAiDisclosure;
                wOpts.AiDisclosureMessage   = o.AiDisclosureMessage;
                wOpts.AvatarUrl             = o.AvatarUrl;
                wOpts.InputPlaceholder      = o.InputPlaceholder;
                wOpts.Theme                 = o.Theme;
                wOpts.Position              = o.Position;
                wOpts.PrimaryColor          = o.PrimaryColor;
                wOpts.Language              = o.Language;
                wOpts.EnableVoiceInput      = o.EnableVoiceInput;
                wOpts.EnableVoiceOutput     = o.EnableVoiceOutput;
                wOpts.VoiceStreaming        = o.VoiceStreaming;
                wOpts.VoiceBargeIn          = o.VoiceBargeIn;
                // #17 — hands-free needs both halves: without playback there is nothing to wait for
                // before reopening the microphone, and the loop would talk over itself.
                wOpts.VoiceHandsFree        = o.VoiceHandsFree && o.EnableVoiceInput && o.EnableVoiceOutput;
                wOpts.VoiceRate             = o.VoiceRate;
                wOpts.EnableSuggestions     = o.EnableSuggestions;
                wOpts.EnableOnboardingTour  = o.EnableOnboardingTour;
                wOpts.EnableImageInput      = o.EnableImageInput;
                wOpts.MaxImageBytes         = o.MaxImageBytes;
                wOpts.MaxImagesPerMessage   = o.MaxImagesPerMessage;
                wOpts.AllowedImageTypes     = o.AllowedImageTypes.ToList();
                wOpts.ShowRagSources        = o.ShowRagSources;
                wOpts.ShowHostedToolsStatus = o.ShowHostedToolsStatus;
                // #15 — the badge must only claim what the model actually got: HostedToolsBuilder
                // drops file search with no vector store id, and hosted MCP with no server.
                wOpts.HostedTools           = MaskUnconfiguredHostedTools(o);
                wOpts.ShowMcpStatus         = o.ShowMcpStatus;
                wOpts.HasMcpServers         = o.McpServers is { Length: > 0 };
                wOpts.ShowA2AStatus         = o.ShowA2AStatus;
                wOpts.HasRemoteAgents       = o.RemoteAgents is { Length: > 0 };
                wOpts.MaxMessageLength      = o.MaxMessageLength;
                wOpts.RemoteAgentDisplays   = o.RemoteAgents?
                    .Select(r => new AgentDisplayInfo { Name = r.Name, AgentCardUrl = r.AgentCardUrl })
                    .ToList() ?? [];
                wOpts.McpServerNames        = o.McpServers?
                    .Select(s => s.Name)
                    .ToList() ?? [];
            });

        // ── RAG ───────────────────────────────────────────────────────────────
        // TryAdd — a custom IMentorRagSource registered before AddMentorAgent()
        // will not be overwritten. When UseRag is true, developers register their
        // own IMentorRagSource implementation (e.g. Azure AI Search, Qdrant, etc.)
        // before calling AddMentorAgent():
        //
        //   builder.Services.AddScoped<IMentorRagSource, MyVectorDbRagSource>();
        //   builder.Services.AddMentorAgent(options => { options.UseRag = true; });
        //
        // A collection of Microsoft.Extensions.VectorData needs no source of its own: AddMentorVectorStoreRag.
        // If no custom source is registered, NullRagSource is used (no-op).
        services.TryAddScoped<IMentorRagSource, NullRagSource>();

        services.AddScoped<RagContextProvider>(sp => new RagContextProvider(
            sp.GetRequiredService<IMentorRagSource>(),
            sp.GetRequiredService<IMentorStateService>(),
            sp.GetRequiredService<IOptions<MentorOptions>>(),
            sp.GetRequiredService<ILogger<RagContextProvider>>())
        {
            Faults      = sp.GetRequiredService<TurnFaultLog>(),
            PageContext = sp.GetRequiredService<IMentorPageContext>(),
            Auth        = MentorUserAuthentication.For(sp),
            TurnLanguage = sp.GetRequiredService<MentorTurnLanguage>(),
            Telemetry   = sp.GetService<Observability.MentorTelemetry>(),   // story 195: a failed search, counted
        });

        // ── Agent Skills ─────────────────────────────────────────────────────
        services.AddScoped<MentorSkillsBuilder>();
        services.AddSingleton<MentorSharedSkillSource>();   // SkillsRefreshInterval's cache, one per process

        // ── MCP Client (consume external MCP servers) ─────────────────────────
        services.AddScoped<McpToolsBuilder>();

        // ── A2A Consumer (connect to remote A2A agents) ───────────────────────
        services.AddScoped<A2AAgentBuilder>();

        // ── MCP and A2A servers (expose MentorAgent to other clients and agents) ──
        // Registered by Novior.MentorAgent.Server, not here (story 105): their transports need ASP.NET Core,
        // which this package does not reference so that a MAUI app compiles for every target. The
        // registration there decides from McpServerEnabled / A2AServerEnabled as read now, at
        // registration time, as this method always did.
        services.AddSingleton(new MentorRegistrationSnapshot(snapshot, snapshotError));

        return services;
    }

    /// <summary>
    /// Registers an <see cref="IMentorActionObserver"/>: it receives every decision of the tool gate, for every
    /// session and every surface of the process. A singleton, resolved from the application's root services;
    /// every observer registered is called, in the order of registration. Registering the same type twice
    /// registers it once.
    /// </summary>
    /// <typeparam name="TObserver">The observer.</typeparam>
    public static IServiceCollection AddMentorActionObserver<TObserver>(this IServiceCollection services)
        where TObserver : class, IMentorActionObserver
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMentorActionObserver, TObserver>());
        return services;
    }

    /// <summary>
    /// Registers <paramref name="observer"/> as an <see cref="IMentorActionObserver"/>: it receives every decision
    /// of the tool gate, for every session and every surface of the process.
    /// </summary>
    /// <param name="services">The services.</param>
    /// <param name="observer">The observer, already built.</param>
    public static IServiceCollection AddMentorActionObserver(this IServiceCollection services, IMentorActionObserver observer)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(observer);
        services.AddSingleton(observer);
        return services;
    }

    /// <summary>
    /// Registers an <see cref="IMentorAgentSourceObserver"/>: every time a coordinator is built, it receives what each
    /// <see cref="IMentorAgentSource"/> loaded and what it left out, for every session of the process. A singleton,
    /// resolved from the application's root services; every observer registered is called, in the order of
    /// registration. Registering the same type twice registers it once.
    /// </summary>
    /// <typeparam name="TObserver">The observer.</typeparam>
    public static IServiceCollection AddMentorAgentSourceObserver<TObserver>(this IServiceCollection services)
        where TObserver : class, IMentorAgentSourceObserver
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMentorAgentSourceObserver, TObserver>());
        return services;
    }

    /// <summary>
    /// Registers an <see cref="IMentorActionPolicy"/>: it decides whether a user may use each of the application's
    /// actions, and whether each call may run, on every surface. A singleton, resolved from the application's root
    /// services; what it needs from the conversation's scope it resolves from <c>MentorActionRequest.Services</c>.
    /// Every policy registered is asked, in the order of registration. Registering the same type twice registers it
    /// once.
    /// </summary>
    /// <typeparam name="TPolicy">The policy.</typeparam>
    public static IServiceCollection AddMentorActionPolicy<TPolicy>(this IServiceCollection services)
        where TPolicy : class, IMentorActionPolicy
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMentorActionPolicy, TPolicy>());
        return services;
    }

    /// <summary>
    /// Registers <paramref name="policy"/> as an <see cref="IMentorActionPolicy"/>: it decides whether a user may use
    /// each of the application's actions, and whether each call may run, on every surface.
    /// </summary>
    /// <param name="services">The services.</param>
    /// <param name="policy">The policy, already built.</param>
    public static IServiceCollection AddMentorActionPolicy(this IServiceCollection services, IMentorActionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(policy);
        services.AddSingleton(policy);
        return services;
    }

    /// <summary>
    /// Registers the <see cref="IMentorActionGovernor"/>: it decides from outside the code whether each of the
    /// application's actions is on, on every surface, for every session. A singleton, resolved from the application's
    /// root services; what it needs from the conversation's scope it resolves from <c>MentorActionRequest.Services</c>.
    /// Registering it gives whoever implements it the power to widen what the code allows: an action the code ships
    /// off runs once the governor turns it on.
    /// </summary>
    /// <typeparam name="TGovernor">The governor.</typeparam>
    /// <exception cref="InvalidOperationException">An <see cref="IMentorActionGovernor"/> is already registered: there can be one.</exception>
    public static IServiceCollection AddMentorActionGovernor<TGovernor>(this IServiceCollection services)
        where TGovernor : class, IMentorActionGovernor
    {
        ArgumentNullException.ThrowIfNull(services);
        // Two governors that disagree about widening an action have no answer that is obviously the stricter.
        if (services.FirstOrDefault(d => d.ServiceType == typeof(IMentorActionGovernor) && !d.IsKeyedService) is { } registered)
            throw new InvalidOperationException(
                $"An IMentorActionGovernor is already registered ({registered.ImplementationType?.FullName ?? registered.ServiceType.Name}): " +
                "there can be one per application.");
        services.AddSingleton<IMentorActionGovernor, TGovernor>();
        return services;
    }

    /// <summary>
    /// Registers the <see cref="IMentorTenantResolver"/>: it names the tenant every turn and every call to the MCP server
    /// belongs to, which <see cref="IMentorTenant"/> then reads. A singleton, resolved from the application's root services;
    /// what it needs from the request's scope it resolves from <c>MentorTenantRequest.Services</c>. Before or after
    /// <c>AddMentorAgent</c>: it takes the place of the single tenant that one registers.
    /// </summary>
    /// <typeparam name="TResolver">The resolver.</typeparam>
    /// <exception cref="InvalidOperationException">An <see cref="IMentorTenantResolver"/> is already registered: there can be one.</exception>
    public static IServiceCollection AddMentorTenantResolver<TResolver>(this IServiceCollection services)
        where TResolver : class, IMentorTenantResolver
    {
        ArgumentNullException.ThrowIfNull(services);
        // Two resolvers could name two tenants for one request: there is no answer to choose.
        if (services.FirstOrDefault(d => d.ServiceType == typeof(IMentorTenantResolver) && !d.IsKeyedService
                                         && d.ImplementationType != typeof(MentorSingleTenantResolver)) is { } registered)
            throw new InvalidOperationException(
                $"An IMentorTenantResolver is already registered ({registered.ImplementationType?.FullName ?? registered.ServiceType.Name}): " +
                "there can be one per application.");
        services.RemoveAll<IMentorTenantResolver>();
        services.AddSingleton<IMentorTenantResolver, TResolver>();
        return services;
    }

    /// <summary>
    /// Registers a <see cref="MentorVectorStoreRagSource{TRecord}"/> as the <see cref="IMentorRagSource"/>: the knowledge
    /// base is a collection of <c>Microsoft.Extensions.VectorData</c>. Scoped, so the message is embedded once per turn
    /// and shared with routing, tool filtering and memory; before or after <c>AddMentorAgent</c>. Turn
    /// <c>UseRag</c> on as well.
    /// </summary>
    /// <param name="services">The services.</param>
    /// <param name="collection">The collection to search, resolved for each session.</param>
    /// <param name="options">What of a record is the document and its citation, and what the collection's scores mean.</param>
    /// <exception cref="InvalidOperationException">
    /// The distance function is unknown or has no fixed scale, or another <see cref="IMentorRagSource"/> is already
    /// registered: there can be one.
    /// </exception>
    public static IServiceCollection AddMentorVectorStoreRag<TRecord>(
        this IServiceCollection services,
        Func<IServiceProvider, Microsoft.Extensions.VectorData.IVectorSearchable<TRecord>> collection,
        MentorVectorStoreRagOptions<TRecord> options)
        where TRecord : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(options);

        // At registration, so that a collection whose scores would be read backwards stops the application at startup.
        var scale = MentorVectorStoreRagSource<TRecord>.ScaleOf(options);
        if (services.FirstOrDefault(d => d.ServiceType == typeof(IMentorRagSource) && !d.IsKeyedService
                                         && d.ImplementationType != typeof(NullRagSource)) is { } registered)
            throw new InvalidOperationException(
                $"An IMentorRagSource is already registered ({registered.ImplementationType?.FullName ?? registered.ServiceType.Name}): " +
                "there can be one per application.");

        var warnings = new VectorStoreRagWarnings();
        services.RemoveAll<IMentorRagSource>();
        services.AddScoped<IMentorRagSource>(sp => new MentorVectorStoreRagSource<TRecord>(
            collection(sp),
            options,
            sp.GetService<ILogger<MentorVectorStoreRagSource<TRecord>>>(),
            scale,
            warnings,
            sp.GetService<IOptions<MentorOptions>>()?.Value.EmbeddingGenerator,
            sp.GetService<IMentorQueryEmbedding>()));
        return services;
    }

    /// <summary>
    /// Clears the hosted-tool flags whose required configuration is missing, so the widget badge lists
    /// exactly what <c>HostedToolsBuilder</c> handed to the model — never a capability it silently
    /// dropped.
    /// </summary>
    private static MentorHostedTools MaskUnconfiguredHostedTools(MentorOptions o)
    {
        var tools = o.HostedTools;

        if (o.FileSearchVectorStoreIds is not { Count: > 0 })
            tools &= ~MentorHostedTools.FileSearch;

        if (o.HostedMcpServers is not { Count: > 0 })
            tools &= ~MentorHostedTools.HostedMcp;

        return tools;
    }
}
