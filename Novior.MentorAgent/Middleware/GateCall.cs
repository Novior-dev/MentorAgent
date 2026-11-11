using System.Diagnostics;
using System.Security.Claims;

namespace Novior.MentorAgent.Middleware;

/// <summary>
/// One call at the gate, as its decisions describe it (story 180). Built where the call reaches the gate —
/// the middleware, or in Native mode the orchestrator — and filled in as the gate decides.
/// </summary>
internal sealed class GateCall
{
    internal required string ToolName { get; init; }
    internal MethodInfo? Method { get; init; }
    internal string? CallId { get; init; }
    internal MentorAgentKind? AgentKind { get; init; }
    internal string? AgentName { get; init; }

    /// <summary>Read once, at the first decision.</summary>
    internal string? DisplayName { get; set; }

    internal ClaimsPrincipal? User { get; set; }
    internal string[] Roles { get; set; } = [];

    /// <summary>Whether <see cref="Roles"/> are the governor's, in place of the code's (story 1546).</summary>
    internal bool RolesFromOutside { get; set; }

    /// <summary>Whether the governor changed, from the code's, whether the call is confirmed (story 1547).</summary>
    internal bool ConfirmationFromOutside { get; set; }
    internal Guid? ConfirmationId { get; set; }

    /// <summary>Who confirmed and when, once the user has: what the action reads as its confirmation (story 177).</summary>
    internal ClaimsPrincipal? ConfirmedBy { get; set; }
    internal DateTimeOffset? ConfirmedAt { get; set; }

    /// <summary>The span's <c>mentoragent.tool.confirmation</c>: none, requested, confirmed, rejected, auto-approved, unavailable.</summary>
    internal string Confirmation { get; set; } = "none";

    /// <summary>The call's <c>mentor.tool.*</c> span, when there is one.</summary>
    internal Activity? Span { get; set; }
}

/// <summary>
/// What the role check found: the refusal's text for the model (null when allowed), the reason code, the user it
/// checked, the roles, and whether the check has logged the refusal already.
/// </summary>
internal readonly record struct RoleCheck(string? Error, string Reason, ClaimsPrincipal? User, string[] Roles, bool Logged)
{
    /// <summary>An action that requires no role.</summary>
    internal static RoleCheck Open { get; } = new(null, MentorActionReasons.Allowed, null, [], false);

    internal bool Allowed => Error is null;
}

/// <summary>
/// A call refused before the roles because its action is off (story 1549): its last decision, what the model reads,
/// and the governor's exception type when it threw.
/// </summary>
/// <summary>
/// Whether a call is confirmed (story 1547), and whether the governor required it: then no auto-approval rule of Native
/// mode lets the call through unconfirmed.
/// </summary>
internal readonly record struct ConfirmationAtTheCall(bool Asks, bool Required);

internal readonly record struct OffCall(MentorActionOutcome Outcome, string Reason, string Text, string? ExceptionType = null);
