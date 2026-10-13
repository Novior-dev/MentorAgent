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
            return new Novior.MentorAgent.Abstractions.Core.MentorLocalizer(opts.Value.Language);
        });

        // ── Middleware ────────────────────────────────────────────────────────
        services.AddScoped<MentorAgentUIMiddleware>(sp =>
        {
            // The user is read through MentorUserAuthentication, like memory and the context providers (BUG-131).
            var middleware = MentorUserAuthentication.For(sp) is { } auth
                ? ActivatorUtilities.CreateInstance<MentorAgentUIMiddleware>(sp, auth)
                : ActivatorUtilities.CreateInstance<MentorAgentUIMiddleware>(sp);
            middleware.Origin = sp.GetRequiredService<MentorTurnOrigin>();   // BUG-097
            return middleware;
        });

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

        // One stack trace per cause per turn: a single provider failure used to be met, and
        // logged, by six components in a row (E1).
        services.AddScoped<TurnFaultLog>();

        // Who called through a MentorAgent transport (hub, /mentor/chat, A2A), read before the host's
        // AuthenticationStateProvider — see MentorUserAuthentication (BUG-131).
        services.AddScoped<MentorTransportUser>();

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
            return new AppContextProvider(options, pageCtx, memory, nav, auth)
            {
                Origin = sp.GetRequiredService<MentorTurnOrigin>(),
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
