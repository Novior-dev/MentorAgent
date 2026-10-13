namespace Novior.MentorAgent.Abstractions.Models;

/// <summary>
/// UI-only options for the MentorAgent chat widget.
/// Registered in DI by both <c>AddMentorAgent()</c> (Blazor Server)
/// and <c>AddMentorAgentBlazor()</c> (Blazor WASM).
/// <c>ChatWidget</c> injects <c>IOptions&lt;MentorWidgetOptions&gt;</c>.
/// </summary>
public sealed class MentorWidgetOptions
{
    /// <summary>Bot name shown in the widget header. Default: "Mentor AI".</summary>
    public string BotName { get; set; } = "Mentor AI";

    /// <summary>Welcome message shown when the chat is empty.</summary>
    public string? WelcomeMessage { get; set; }

    /// <summary>Custom avatar URL. Falls back to the built-in MentorAgent icon.</summary>
    public string? AvatarUrl { get; set; }

    /// <summary>Input box placeholder text. If null, uses the localized default in <see cref="Language"/>.</summary>
    public string? InputPlaceholder { get; set; }

    /// <summary>Visual theme. Default: Default.</summary>
    public MentorTheme Theme { get; set; } = MentorTheme.Default;

    /// <summary>Widget position on screen. Default: BottomRight.</summary>
    public ChatPosition Position { get; set; } = ChatPosition.BottomRight;

    /// <summary>Custom accent color (hex, e.g. "#7c3aed").</summary>
    public string? PrimaryColor { get; set; }

    /// <summary>Language for widget UI strings. Default: English.</summary>
    public MentorLanguage Language { get; set; } = MentorLanguage.English;

    /// <summary>Show microphone button for voice input.</summary>
    public bool EnableVoiceInput { get; set; }

    /// <summary>Enable text-to-speech for AI responses.</summary>
    public bool EnableVoiceOutput { get; set; }

    /// <summary>
    /// Speak each sentence as it streams instead of reading the answer back once it is complete
    /// (roadmap #17). Default: <c>true</c>. Turning it off restores the previous behaviour, which
    /// is worth doing only if your answers are consistently one line long.
    /// </summary>
    public bool VoiceStreaming { get; set; } = true;

    /// <summary>
    /// Stop playback as soon as the user takes the floor — presses the microphone or sends a
    /// message (roadmap #17). Default: <c>true</c>. This is press-to-interrupt barge-in, not
    /// acoustic: the browser cannot listen through its own playback without echo cancellation.
    /// </summary>
    public bool VoiceBargeIn { get; set; } = true;

    /// <summary>
    /// Keep the conversation going by voice alone: the transcript is sent on silence and the
    /// microphone reopens once the spoken answer ends (roadmap #17). Default: <c>false</c> —
    /// it holds the microphone open across turns, which is not a reasonable default.
    /// Requires <see cref="EnableVoiceInput"/>.
    /// </summary>
    public bool VoiceHandsFree { get; set; }

    /// <summary>
    /// Speech rate passed to <c>SpeechSynthesisUtterance.rate</c>. 1.0 is the browser default;
    /// the useful range is roughly 0.5–2.0. Default: 1.0.
    /// </summary>
    public double VoiceRate { get; set; } = 1.0;

    /// <summary>Show suggestion chips in the welcome panel.</summary>
    public bool EnableSuggestions { get; set; }

    /// <summary>
    /// Show the guided tour the first time this user opens the widget (roadmap #18).
    /// Mirrors <c>MentorOptions.EnableOnboardingTour</c>.
    /// </summary>
    public bool EnableOnboardingTour { get; set; }

    /// <summary>
    /// Show the image-attachment button and enable paste / drag-and-drop / URL input
    /// (roadmap #13 — multimodal). Mirrors <c>MentorOptions.EnableImageInput</c>; the server
    /// re-validates every attachment, so this only drives the UI.
    /// </summary>
    public bool EnableImageInput { get; set; }

    /// <summary>Client-side size cap per image, in bytes. Mirrors <c>MentorOptions.MaxImageBytes</c>. Default: 4 MB.</summary>
    public int MaxImageBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>Client-side cap on images per message. Mirrors <c>MentorOptions.MaxImagesPerMessage</c>. Default: 4.</summary>
    public int MaxImagesPerMessage { get; set; } = 4;

    /// <summary>Client-side MIME allow-list. Mirrors <c>MentorOptions.AllowedImageTypes</c>.</summary>
    public List<string> AllowedImageTypes { get; set; } =
        ["image/png", "image/jpeg", "image/gif", "image/webp"];

    /// <summary>Show RAG source citations below AI responses.</summary>
    public bool ShowRagSources { get; set; }

    /// <summary>
    /// Show the provider-hosted tools badge in the widget header (roadmap #15).
    /// Mirrors <c>MentorOptions.ShowHostedToolsStatus</c>.
    /// </summary>
    public bool ShowHostedToolsStatus { get; set; }

    /// <summary>
    /// Hosted tools actually active on the server (roadmap #15) — drives the badge contents.
    /// A tool the server had to drop (e.g. file search with no vector store) is not reported here,
    /// so the badge never claims a capability the model does not have.
    /// </summary>
    public MentorHostedTools HostedTools { get; set; } = MentorHostedTools.None;

    /// <summary>Show MCP server connection badge.</summary>
    public bool ShowMcpStatus { get; set; }

    /// <summary>True if at least one MCP server is configured (controls badge visibility).</summary>
    public bool HasMcpServers { get; set; }

    /// <summary>Show A2A remote agent connection badge.</summary>
    public bool ShowA2AStatus { get; set; }

    /// <summary>True if at least one remote agent is configured (controls badge visibility).</summary>
    public bool HasRemoteAgents { get; set; }

    /// <summary>Maximum message length in the input box. 0 = no limit (defaults to 4000).</summary>
    public int MaxMessageLength { get; set; } = 4000;

    /// <summary>Remote agent display info for the A2A detail bar.</summary>
    public List<AgentDisplayInfo> RemoteAgentDisplays { get; set; } = [];

    /// <summary>
    /// Names of configured MCP client servers. Used by <c>ChatWidget</c> to
    /// pre-populate the MCP status badge in "connecting" state before the first
    /// message triggers lazy initialisation.
    /// </summary>
    public List<string> McpServerNames { get; set; } = [];
}

/// <summary>Display information for a remote A2A agent shown in the A2A detail bar.</summary>
public sealed class AgentDisplayInfo
{
    public string Name { get; set; } = string.Empty;
    public string? AgentCardUrl { get; set; }
}
