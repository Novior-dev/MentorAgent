using Novior.MentorAgent.Abstractions.Models;

namespace Novior.MentorAgent.Blazor.Models;

/// <summary>
/// Configuration for <c>AddMentorAgentBlazor()</c>.
/// Provides UI settings for the chat widget and the SignalR hub URL.
/// </summary>
public sealed class MentorAgentBlazorOptions
{
    /// <summary>
    /// URL of the Novior.MentorAgent.Server SignalR hub.
    /// Default: "/mentor-hub". A relative URL is resolved against the page's base address, as a
    /// browser does ("/mentor-hub" against the origin, "mentor-hub" under the app's base path): use it
    /// when the server also serves the client. For a server on another origin, set its absolute URL.
    /// </summary>
    public string HubUrl { get; set; } = "/mentor-hub";

    /// <summary>Bot name shown in the widget header.</summary>
    public string BotName { get; set; } = "Mentor AI";

    /// <summary>Welcome message shown when the chat is empty.</summary>
    public string? WelcomeMessage { get; set; }

    /// <summary>Custom avatar URL.</summary>
    public string? AvatarUrl { get; set; }

    /// <summary>Input box placeholder text. If null, uses the localized default in <see cref="Language"/>.</summary>
    public string? InputPlaceholder { get; set; }

    /// <summary>Visual theme.</summary>
    public MentorTheme Theme { get; set; } = MentorTheme.Default;

    /// <summary>Widget position on screen.</summary>
    public ChatPosition Position { get; set; } = ChatPosition.BottomRight;

    /// <summary>Custom accent color (hex, e.g. "#7c3aed").</summary>
    public string? PrimaryColor { get; set; }

    /// <summary>Language for widget UI strings.</summary>
    public MentorLanguage Language { get; set; } = MentorLanguage.English;

    /// <summary>Show microphone button for voice input.</summary>
    public bool EnableVoiceInput { get; set; }

    /// <summary>Enable text-to-speech for AI responses.</summary>
    public bool EnableVoiceOutput { get; set; }

    /// <summary>Speak each sentence as it streams instead of reading the answer back (roadmap #17). Default: true.</summary>
    public bool VoiceStreaming { get; set; } = true;

    /// <summary>Stop playback when the user presses the microphone or sends a message (roadmap #17). Default: true.</summary>
    public bool VoiceBargeIn { get; set; } = true;

    /// <summary>Hands-free loop — send on silence, reopen the microphone after the answer (roadmap #17). Default: false.</summary>
    public bool VoiceHandsFree { get; set; }

    /// <summary>Speech rate for <c>SpeechSynthesisUtterance</c> (0.5–2.0). Default: 1.0.</summary>
    public double VoiceRate { get; set; } = 1.0;

    /// <summary>Show suggestion chips in the welcome panel.</summary>
    public bool EnableSuggestions { get; set; }

    /// <summary>Show the guided tour on first run (roadmap #18). Default: false.</summary>
    public bool EnableOnboardingTour { get; set; }

    /// <summary>
    /// Where to fetch the tour steps from (roadmap #18). Default: <c>/mentor/tour</c>.
    /// <para>
    /// A relative value is resolved against <see cref="HubUrl"/>'s origin when that is absolute,
    /// because the tour is served by the same server as the hub — resolving it against the client
    /// app's own address instead would request it from a host that does not have it. Set an
    /// absolute URL here to override that entirely.
    /// </para>
    /// </summary>
    public string TourUrl { get; set; } = "/mentor/tour";

    /// <summary>
    /// Show the image-attachment button and enable paste / drag-and-drop / URL input
    /// (roadmap #13). Must also be enabled server-side (<c>MentorOptions.EnableImageInput</c>),
    /// which is where attachments are actually validated.
    /// </summary>
    public bool EnableImageInput { get; set; }

    /// <summary>Client-side size cap per image, in bytes. Default: 4 MB.</summary>
    public int MaxImageBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>Client-side cap on images per message. Default: 4.</summary>
    public int MaxImagesPerMessage { get; set; } = 4;

    /// <summary>Client-side MIME allow-list for attachments.</summary>
    public List<string> AllowedImageTypes { get; set; } =
        ["image/png", "image/jpeg", "image/gif", "image/webp"];

    /// <summary>Show RAG source citations below AI responses.</summary>
    public bool ShowRagSources { get; set; }

    /// <summary>
    /// Show the provider-hosted tools badge in the widget header (roadmap #15).
    /// Purely informational — the tools themselves are configured and executed server-side.
    /// </summary>
    public bool ShowHostedToolsStatus { get; set; }

    /// <summary>
    /// Hosted tools active on the server (roadmap #15), used to fill the badge. Mirror whatever
    /// <c>MentorOptions.HostedTools</c> is set to on the Novior.MentorAgent.Server project.
    /// </summary>
    public MentorHostedTools HostedTools { get; set; } = MentorHostedTools.None;

    /// <summary>Show MCP server connection status badge in the widget header.</summary>
    public bool ShowMcpStatus { get; set; }

    /// <summary>Whether the server has MCP servers configured (enables the MCP badge).</summary>
    public bool HasMcpServers { get; set; }

    /// <summary>Show A2A remote agent status badge in the widget header.</summary>
    public bool ShowA2AStatus { get; set; }

    /// <summary>Whether the server has remote A2A agents configured (enables the A2A badge).</summary>
    public bool HasRemoteAgents { get; set; }

    /// <summary>Names of MCP client servers configured on the server. Used to pre-populate the MCP status badge.</summary>
    public List<string> McpServerNames { get; set; } = [];

    /// <summary>Remote A2A agents to display in the A2A badge and detail bar.</summary>
    public List<AgentDisplayInfo> RemoteAgentDisplays { get; set; } = [];

    /// <summary>
    /// Supplies the bearer token for the hub connection, called on every connect and reconnect.
    ///
    /// <b>Without this the hub sees an anonymous caller</b>, whatever your application has signed
    /// the user in as — which means every <c>RequiredRoles</c> action is blocked and the user is keyed
    /// as anonymous: with the server's <c>AnonymousIdentity = PerSession</c>, the default, memory and
    /// <c>RateLimitPerUser</c> last only as long as the connection; with <c>Shared</c> every user shares
    /// the single memory key <c>"anonymous"</c> and one global limit. This is the WASM equivalent of the JavaScript client's <c>accessTokenFactory</c>, and
    /// the server reads the resulting principal off <c>HubCallerContext.User</c>.
    ///
    /// Return <see langword="null"/> while nobody is signed in; the connection then behaves exactly
    /// as it did before a provider was set.
    ///
    /// <code>
    /// builder.Services.AddSingleton&lt;TokenStore&gt;();
    /// builder.Services.AddMentorAgentBlazor(options =>
    /// {
    ///     options.HubUrl = "https://api.example.com/mentor-hub";
    ///     options.AccessTokenProvider = () => Task.FromResult(tokenStore.Token);
    /// });
    /// </code>
    /// </summary>
    public Func<Task<string?>>? AccessTokenProvider { get; set; }

    /// <summary>
    /// Escape hatch applied to the hub's <c>HttpConnectionOptions</c> after
    /// <see cref="AccessTokenProvider"/> — custom headers, a transport restriction, cookies,
    /// <c>UseDefaultCredentials</c>. Use it for anything the options above do not name.
    ///
    /// <code>
    /// options.ConfigureConnection = o => o.Headers["X-Tenant"] = tenantId;
    /// </code>
    /// </summary>
    public Action<HttpConnectionOptions>? ConfigureConnection { get; set; }
}
