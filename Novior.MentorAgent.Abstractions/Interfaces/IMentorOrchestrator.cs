using Novior.MentorAgent.Abstractions.Models;

namespace Novior.MentorAgent.Abstractions.Interfaces;

/// <summary>
/// Public contract for the MentorAgent orchestrator.
/// Implemented by <c>MentorOrchestrator</c> (Blazor Server) and
/// <c>WasmMentorOrchestrator</c> (Blazor WASM via SignalR).
/// Injected into <c>ChatWidget</c> — the component works identically in both contexts.
/// </summary>
public interface IMentorOrchestrator
{
    /// <summary>Sends a user message and starts the AI response.</summary>
    Task SendMessageAsync(string userMessage, CancellationToken ct = default);

    /// <summary>
    /// Sends a user message with optional image attachments (roadmap #13) and starts the AI response.
    /// Attachments are ignored unless <c>MentorOptions.EnableImageInput</c> is set and the chat model is
    /// vision-capable. Passing <see langword="null"/>/empty is equivalent to the text-only overload.
    /// </summary>
    Task SendMessageAsync(string userMessage, IReadOnlyList<MentorAttachment>? attachments, CancellationToken ct = default);

    /// <summary>Cancels the current AI request.</summary>
    void CancelCurrentRequest();

    /// <summary>Resets the conversation session.</summary>
    Task ResetSessionAsync(CancellationToken ct = default);

    /// <summary>
    /// Saves the current conversation as a snapshot. The snapshot is the conversation <b>in clear</b>:
    /// keep it on the server, or protected (e.g. <c>ProtectedLocalStorage</c>) — never as text a user can
    /// read or edit, because a restored snapshot is history the model trusts. Novior.MentorAgent.Server's
    /// <c>/mentor/session</c> hands clients a protected, user-bound form instead. Returns
    /// <see langword="null"/> when there is no conversation yet —
    /// which a caller must tell apart from a snapshot, or it will overwrite a good stored one with
    /// nothing.
    /// <para>
    /// This lives here rather than on <c>IMentorSessionManager</c> because every method there takes
    /// the coordinator <c>AIAgent</c> as its first argument, and the coordinator is deliberately not
    /// handed out to application code — running it directly would bypass the guardrails, the rate
    /// limiter and both gates. The orchestrator already owns it, and already carries the third
    /// session operation (<see cref="ResetSessionAsync"/>).
    /// </para>
    /// </summary>
    Task<System.Text.Json.JsonElement?> SerializeSessionAsync(CancellationToken ct = default);

    /// <summary>
    /// Restores a conversation from a snapshot produced by <see cref="SerializeSessionAsync"/>.
    /// <para>
    /// Throws <see cref="ArgumentException"/> if the snapshot is not one of ours or was written by
    /// an incompatible version, leaving the current conversation untouched. That is deliberate: the
    /// Agent Framework's own deserializer accepts unrelated JSON and returns an empty session, so
    /// without this check a stale <c>localStorage</c> entry would silently replace the conversation
    /// the user is having.
    /// </para>
    /// </summary>
    Task RestoreSessionAsync(System.Text.Json.JsonElement snapshot, CancellationToken ct = default);

    /// <summary>Responds to a pending HITL confirmation dialog.</summary>
    Task RespondToApprovalAsync(ConfirmationRequest req, bool approved, CancellationToken ct = default);
}
