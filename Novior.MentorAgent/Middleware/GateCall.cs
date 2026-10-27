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
