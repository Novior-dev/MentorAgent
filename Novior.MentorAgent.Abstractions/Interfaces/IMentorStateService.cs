namespace Novior.MentorAgent.Abstractions.Interfaces;

/// <summary>
/// Thread-safe event bus between the AI layer (Agent Framework) and the UI layer (Blazor).
///
/// Agent Framework executes on background threads; Blazor requires StateHasChanged()
/// to be called on the circuit's UI thread. MentorAgentUIMiddleware intercepts AI updates
/// and publishes them through this service. Razor components subscribe and call
/// InvokeAsync(StateHasChanged) to update the UI in a thread-safe manner.
/// </summary>
public interface IMentorStateService
{
    // -- Streaming text --------------------------------------------------------

    /// <summary>Event: new streaming text chunk from the AI.</summary>
    event Action<string>? OnStreamingChunk;

    /// <summary>Event: streaming completed.</summary>
    event Action? OnStreamingCompleted;

    // -- Tool Calls / Actions --------------------------------------------------

    /// <summary>Event: a tool call has started. Payload: tool name.</summary>
    event Action<string>? OnActionExecuting;

    /// <summary>Event: a tool call has completed successfully.</summary>
    event Action<string>? OnActionCompleted;

    /// <summary>Event: a tool call has failed. Payload: error message.</summary>
    event Action<string>? OnActionFailed;

    // -- Global state ----------------------------------------------------------

    /// <summary>Event: the AI is busy (true) or idle (false).</summary>
    event Action<bool>? OnBusyChanged;

    /// <summary>Event: critical error to display in the widget.</summary>
    event Action<string>? OnError;

    /// <summary>Event: confirmation required for a destructive action.</summary>
    event Action<ConfirmationRequest>? OnConfirmationRequired;

    /// <summary>
    /// Event: the user's answer to a confirmation dialog (approved or denied) reached the turn that was
    /// waiting for it. Not raised for an answer that reaches nothing — given after Stop, after the
    /// connection or circuit closed, or a second time — so it can be logged as a decision that was acted on.
    /// </summary>
    event Action<ConfirmationRequest, bool>? OnApprovalResponse;

    /// <summary>Event: automatic navigation after tool execution.</summary>
    event Action<string>? OnNavigationRequested;

    /// <summary>Event: UI action is executing.</summary>
    event Action<string>? OnUIActionExecuting;

    /// <summary>Event: UI action completed.</summary>
    event Action<string>? OnUIActionCompleted;

    // -- RAG -------------------------------------------------------------------

    /// <summary>
    /// Event: RAG sources available for the current response.
    /// Fired just before <see cref="OnStreamingCompleted"/>.
    /// </summary>
    event Action<IReadOnlyList<MentorRagResult>>? OnRagSourcesReady;

    // -- Hosted tools (#15) ----------------------------------------------------

    /// <summary>
    /// Event: a provider-hosted image generation tool produced one or more images for the current
    /// response. Payload: image sources ready for an <c>&lt;img src&gt;</c> — a <c>data:</c> URI when the
    /// provider returned bytes, a remote URL when it returned a link. Fired before
    /// <see cref="OnStreamingCompleted"/>, so the widget can attach them to the finished message.
    /// </summary>
    event Action<IReadOnlyList<string>>? OnGeneratedImages;

    /// <summary>
    /// Event: a tool returned one or more <see cref="MentorCard"/>s to render with the reply
    /// (roadmap #14, generative UI L2). Purely additive — a client that ignores it still gets the
    /// assistant's text, which describes the same data.
    /// </summary>
    event Action<IReadOnlyList<MentorCard>>? OnCardsReady;

    // -- MCP -------------------------------------------------------------------

    /// <summary>
    /// Event: an MCP server connection changed status.
    /// Payload: server name, connected (true) or disconnected (false).
    /// </summary>
    event Action<string, bool>? OnMcpServerStatusChanged;

    /// <summary>
    /// Event: the server declared which provider-hosted tools are actually active (roadmap #15).
    ///
    /// <para>
    /// Only a remote transport raises this. Hosted tools are a <b>server-side</b> capability, so a
    /// detached client (WASM, React, MAUI) cannot know them from its own configuration — mirroring the
    /// list by hand means the badge keeps claiming tools the server dropped, or missing ones it gained.
    /// The widget prefers this value over its local options as soon as it arrives.
    /// </para>
    /// </summary>
    event Action<MentorHostedTools>? OnHostedToolsDeclared;

    // -- Team (L3 GroupChat) ---------------------------------------------------

    /// <summary>
    /// Event: a team member has started speaking during a GroupChat discussion.
    /// Payload: team name, member role name.
    /// </summary>
    event Action<string, string>? OnTeamMemberSpeaking;

    // -- Notify (called by middleware / hub) -----------------------------------

    void NotifyStreamingChunk(string chunk);
    void NotifyStreamingCompleted();
    void NotifyActionExecuting(string toolName, string? displayName = null);
    void NotifyActionCompleted(string toolName);
    void NotifyActionFailed(string toolName, string error);
    void NotifyBusyChanged(bool isBusy);
    void NotifyError(string message);
    void NotifyConfirmationRequired(ConfirmationRequest request);
    void NotifyNavigationRequested(string url);
    void NotifyUIActionExecuting(string actionName);
    void NotifyUIActionCompleted(string actionName);
    void NotifyRagSourcesReady(IReadOnlyList<MentorRagResult> sources);
    void NotifyGeneratedImages(IReadOnlyList<string> imageSources);
    void NotifyCardsReady(IReadOnlyList<MentorCard> cards);
    void NotifyMcpServerStatusChanged(string serverName, bool connected);
    void NotifyHostedToolsDeclared(MentorHostedTools tools);
    void NotifyTeamMemberSpeaking(string teamName, string memberName);

    // -- Action confirmation ---------------------------------------------------

    Task ConfirmAsync(Guid actionId);
    void Cancel(Guid actionId);
}
