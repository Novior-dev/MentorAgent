using System.Security.Claims;

namespace Novior.MentorAgent.Abstractions.Models;

/// <summary>
/// One decision of MentorAgent's tool gate, as <see cref="Interfaces.IMentorActionObserver"/> receives it.
/// <see cref="ToolName"/> and <see cref="DisplayName"/> mean what they mean on <see cref="MentorActionStarted"/>.
/// The call's arguments are never part of it. More properties may be added.
/// </summary>
public sealed record MentorActionDecision
{
    /// <summary>What the gate decided.</summary>
    public required MentorActionOutcome Outcome { get; init; }

    /// <summary>
    /// Why, as a code that stays the same from one version to the next: one of the constants of
    /// <see cref="MentorActionReasons"/>.
    /// </summary>
    public required string ReasonCode { get; init; }

    /// <summary>The tool's technical name, as the model or the MCP client called it (for example <c>cancel_order</c>).</summary>
    public required string ToolName { get; init; }

    /// <summary>The text shown to the user for the tool: the one <see cref="Interfaces.IMentorStateService.OnActionExecuting"/> carries.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Where the call came from.</summary>
    public required MentorActionSurface Surface { get; init; }

    /// <summary>
    /// Which kind of agent asked for the action, or <see langword="null"/> on the MCP server, where the caller
    /// names the tool itself.
    /// </summary>
    public MentorAgentKind? AgentKind { get; init; }

    /// <summary>The name of the agent that asked for the action, when it has one.</summary>
    public string? AgentName { get; init; }

    /// <summary>
    /// An opaque id of the session the turn runs in — the circuit, the MAUI app's scope, the hub connection, the
    /// <c>/mentor/chat</c> request, the A2A task — the same for every decision taken in it. On the MCP server, a
    /// fingerprint of the MCP session id, never the id itself; <see langword="null"/> when the call has no session.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// An opaque id of the user turn, new for every message: every call the model makes while answering it has
    /// the same one. <see langword="null"/> on the MCP server, where there is no turn.
    /// </summary>
    public string? TurnId { get; init; }

    /// <summary>The model's id for the call; on the MCP server, the id of the JSON-RPC request.</summary>
    public string? CallId { get; init; }

    /// <summary>
    /// The <see cref="ConfirmationRequest.ActionId"/> of the confirmation, on the decisions that belong to one:
    /// it links <see cref="MentorActionOutcome.ConfirmationRequested"/> to the answer.
    /// </summary>
    public Guid? ConfirmationId { get; init; }

    /// <summary>Who asked for the action, or <see langword="null"/> when nobody is signed in.</summary>
    public ClaimsPrincipal? User { get; init; }

    /// <summary>The roles the action requires, any one of which is enough; empty when it requires none.</summary>
    public IReadOnlyList<string> RequiredRoles { get; init; } = [];

    /// <summary>
    /// On <see cref="MentorActionOutcome.Confirmed"/>, who confirmed: the caller of <c>POST /mentor/approve</c>;
    /// in a circuit, in the MAUI app and through the hub's <c>RespondToApproval</c>, the session's signed-in user.
    /// </summary>
    public ClaimsPrincipal? ConfirmedBy { get; init; }

    /// <summary>When the gate decided, in UTC.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// On <see cref="MentorActionOutcome.Failed"/> and <see cref="MentorActionOutcome.PolicyFailed"/>, the full name of
    /// the exception's type. Never its message.
    /// </summary>
    public string? ExceptionType { get; init; }

    /// <summary>
    /// The application's own words for the decision: the reason an <c>IMentorActionPolicy</c> gave for stopping the
    /// call or asking for a confirmation, on <see cref="MentorActionOutcome.Blocked"/>,
    /// <see cref="MentorActionOutcome.ConfirmationRequested"/> and <see cref="MentorActionOutcome.Denied"/>
    /// (<see cref="MentorActionReasons.NobodyToConfirm"/>). <see langword="null"/> otherwise.
    /// </summary>
    public string? Detail { get; init; }
}
