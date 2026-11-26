using Novior.MentorAgent.Abstractions.Core;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Blazor.Hub;
using Novior.MentorAgent.Blazor.Services;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Novior.MentorAgent.Blazor.Extensions;

/// <summary>
/// Extension methods for registering Novior.MentorAgent.Blazor services in Blazor WASM.
/// </summary>
public static class ClientServiceExtensions
{
    /// <summary>
    /// Registers MentorAgent client services for Blazor WASM.
    /// Install this in your Blazor WASM / Blazor Auto <b>client</b> project.
    /// Your <b>server</b> project must have <c>AddMentorAgent()</c> + <c>AddMentorAgentServer()</c>.
    ///
    /// <code>
    /// // Client project Program.cs
    /// builder.Services.AddMentorAgentBlazor(options =>
    /// {
    ///     options.HubUrl       = "/mentor-hub";
    ///     options.BotName      = "My Assistant";
    ///     options.Language     = MentorLanguage.English;
    ///     options.PrimaryColor = "#7c3aed";
    /// });
    /// </code>
    /// </summary>
    public static IServiceCollection AddMentorAgentBlazor(
        this IServiceCollection services,
        Action<MentorAgentBlazorOptions> configure)
    {
        var options = new MentorAgentBlazorOptions();
        configure(options);

        services.AddSingleton(options);

        // Hub client (singleton — one connection per app lifetime)
        services.AddSingleton<MentorHubClient>();

        // Page context (scoped per Blazor component tree)
        services.AddScoped<WasmMentorPageContext>();
        services.AddScoped<IMentorPageContext>(sp => sp.GetRequiredService<WasmMentorPageContext>());

        // State service (scoped — receives hub events)
        services.AddScoped<WasmMentorStateService>();
        services.AddScoped<IMentorStateService>(sp => sp.GetRequiredService<WasmMentorStateService>());

        // Story 1640 — what the widget observes of the pages goes to the hub (ReportObservedPage).
        services.TryAddScoped<IMentorPageObserver, WasmPageObserver>();

        // Story 1638, slice C — the recipes the server asks this browser to run, and the page they are offered for.
        services.TryAddScoped<WasmRecipeClient>();

        // Orchestrator (scoped — delegates to hub). With a JavaScript runtime, the recipes' client goes with it: it
        // listens from the first turn, and the orchestrator sends the page's path with the page context.
        services.AddScoped<WasmMentorOrchestrator>(sp =>
        {
            var orchestrator = ActivatorUtilities.CreateInstance<WasmMentorOrchestrator>(sp);
            if (sp.GetService<Microsoft.JSInterop.IJSRuntime>() is not null)
                orchestrator.Recipes = sp.GetRequiredService<WasmRecipeClient>();
            // Story 1290 — no turn while the server's application has the assistant off.
            if (sp.GetService<IMentorAssistantStatus>() is WasmAssistantStatus status)
                orchestrator.Status = status;
            return orchestrator;
        });
        services.AddScoped<IMentorOrchestrator>(sp => sp.GetRequiredService<WasmMentorOrchestrator>());

        // Localizer (singleton — uses language from options)
        services.AddSingleton<MentorLocalizer>(_ => new MentorLocalizer(() => options.Language));

        // #18 — onboarding tour, fetched from the server (the client has no discovery data).
        // TryAdd so a hand-written IMentorTour registered by the app still wins.
        services.TryAddSingleton<IMentorTour, WasmMentorTour>();

        // Story 1290 — the assistant turned off or on by the server's application (GetAssistantStatus, AssistantStatus):
        // the widget disables its input as in-process. TryAdd, as the tour.
        services.TryAddSingleton<IMentorAssistantStatus, WasmAssistantStatus>();

        // Widget options (used by ChatWidget from Novior.MentorAgent.Abstractions)
        services.AddOptions<MentorWidgetOptions>()
            .Configure<MentorAgentBlazorOptions>((wOpts, cOpts) =>
            {
                wOpts.BotName          = cOpts.BotName;
                wOpts.WelcomeMessage   = cOpts.WelcomeMessage;
                wOpts.ShowAiDisclosure    = cOpts.ShowAiDisclosure;
                wOpts.AiDisclosureMessage = cOpts.AiDisclosureMessage;
                wOpts.AvatarUrl        = cOpts.AvatarUrl;
                wOpts.InputPlaceholder = cOpts.InputPlaceholder;
                wOpts.Theme            = cOpts.Theme;
                wOpts.Position         = cOpts.Position;
                wOpts.PrimaryColor     = cOpts.PrimaryColor;
                wOpts.Language         = cOpts.Language;
                wOpts.EnableVoiceInput = cOpts.EnableVoiceInput;
                wOpts.EnableVoiceOutput = cOpts.EnableVoiceOutput;
                wOpts.VoiceStreaming   = cOpts.VoiceStreaming;
                wOpts.VoiceBargeIn     = cOpts.VoiceBargeIn;
                // #17 — hands-free needs both halves, same as the Blazor Server mapping.
                wOpts.VoiceHandsFree   = cOpts.VoiceHandsFree && cOpts.EnableVoiceInput && cOpts.EnableVoiceOutput;
                wOpts.VoiceRate        = cOpts.VoiceRate;
                wOpts.EnableSuggestions = cOpts.EnableSuggestions;
                wOpts.EnableOnboardingTour = cOpts.EnableOnboardingTour;
                wOpts.EnableImageInput    = cOpts.EnableImageInput;
                wOpts.MaxImageBytes       = cOpts.MaxImageBytes;
                wOpts.MaxImagesPerMessage = cOpts.MaxImagesPerMessage;
                wOpts.AllowedImageTypes   = cOpts.AllowedImageTypes;
                wOpts.ShowRagSources   = cOpts.ShowRagSources;
                wOpts.ShowHostedToolsStatus = cOpts.ShowHostedToolsStatus;
                wOpts.HostedTools           = cOpts.HostedTools;
                wOpts.ShowMcpStatus    = cOpts.ShowMcpStatus;
                wOpts.HasMcpServers    = cOpts.HasMcpServers;
                wOpts.ShowA2AStatus    = cOpts.ShowA2AStatus;
                wOpts.HasRemoteAgents  = cOpts.HasRemoteAgents;
                wOpts.McpServerNames      = cOpts.McpServerNames;
                wOpts.RemoteAgentDisplays = cOpts.RemoteAgentDisplays;
            });

        return services;
    }
}
