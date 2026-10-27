namespace Novior.MentorAgent.Abstractions.Models;

/// <summary>
/// The reason codes of <see cref="MentorActionDecision.ReasonCode"/>. They stay the same from one version to
/// the next; a new one comes only in a minor version, and the CHANGELOG says so.
/// </summary>
public static class MentorActionReasons
{
    /// <summary>
    /// <see cref="MentorActionOutcome.Allowed"/>: the action requires no role, or the user holds one, and every
    /// <c>IMentorActionPolicy</c> lets it through.
    /// </summary>
    public const string Allowed = "allowed";

    /// <summary><see cref="MentorActionOutcome.Allowed"/>: in Native mode, an auto-approval rule let an action that asks for confirmation run without asking.</summary>
    public const string AutoApproved = "auto-approved";

    /// <summary><see cref="MentorActionOutcome.Denied"/>: the user holds none of the roles.</summary>
    public const string RoleMissing = "role-missing";

    /// <summary><see cref="MentorActionOutcome.Denied"/>: the action requires a role and nobody is signed in.</summary>
    public const string LoginRequired = "login-required";

    /// <summary><see cref="MentorActionOutcome.Denied"/>: the action requires a role and the host has no <c>AuthenticationStateProvider</c>.</summary>
    public const string AuthNotConfigured = "auth-not-configured";

    /// <summary><see cref="MentorActionOutcome.Denied"/>: reading the user threw.</summary>
    public const string AuthCheckFailed = "auth-check-failed";

    /// <summary><see cref="MentorActionOutcome.Denied"/>: an <c>IMentorActionPolicy</c> does not allow this user the action.</summary>
    public const string NotAuthorized = "not-authorized";

    /// <summary>
    /// <see cref="MentorActionOutcome.Denied"/>: the action, or a policy, asks for confirmation and nobody can give
    /// it — a task from another agent, a call to the MCP server. A policy's reason is in
    /// <see cref="MentorActionDecision.Detail"/>.
    /// </summary>
    public const string NobodyToConfirm = "nobody-to-confirm";

    /// <summary><see cref="MentorActionOutcome.Denied"/>: the MCP server does not publish the action — it asks for confirmation, a predicate keeps it back, or it requires roles and no <c>McpCallerPrincipal</c> is set.</summary>
    public const string Withheld = "withheld";

    /// <summary><see cref="MentorActionOutcome.Denied"/>: the caller is over <c>RateLimitPerUser</c> (MCP server).</summary>
    public const string RateLimited = "rate-limited";

    /// <summary>
    /// <see cref="MentorActionOutcome.ConfirmationRequested"/>, <see cref="MentorActionOutcome.Confirmed"/>,
    /// <see cref="MentorActionOutcome.Rejected"/>: the user was asked, and answered.
    /// </summary>
    public const string Operator = "operator";

    /// <summary>
    /// <see cref="MentorActionOutcome.Rejected"/>: nobody answered the confirmation within <c>ConfirmationTimeout</c>,
    /// so the action did not run.
    /// </summary>
    public const string ConfirmationTimeout = "confirmation-timeout";

    /// <summary>
    /// <see cref="MentorActionOutcome.Blocked"/>: an <c>IMentorActionPolicy</c> stopped the call.
    /// <see cref="MentorActionOutcome.ConfirmationRequested"/>: a policy asked for the confirmation, which the action
    /// does not ask for itself; the answer is then <see cref="Operator"/>'s, as for any other.
    /// </summary>
    public const string Policy = "policy";

    /// <summary><see cref="MentorActionOutcome.PolicyFailed"/>: an <c>IMentorActionPolicy</c> threw.</summary>
    public const string PolicyException = "policy-exception";

    /// <summary><see cref="MentorActionOutcome.PolicyFailed"/>: an <c>IMentorActionPolicy</c> did not answer within <c>ActionPolicyTimeout</c>.</summary>
    public const string PolicyTimeout = "policy-timeout";

    /// <summary>
    /// <see cref="MentorActionOutcome.Stopped"/>: Stop, <c>/mentor/cancel</c>, the MCP client cancelled the call, or the
    /// caller cancelled the A2A task — also after the confirmation, before the action started.
    /// </summary>
    public const string Stop = "stop";

    /// <summary><see cref="MentorActionOutcome.Stopped"/>: the session ended — the circuit, the hub connection or the request closed.</summary>
    public const string SessionEnded = "session-ended";

    /// <summary>
    /// <see cref="MentorActionOutcome.Stopped"/>: in Native mode, the turn ended before the action could run — the
    /// run failed after the confirmation, or the turn gave up after too many rounds of approvals.
    /// </summary>
    public const string TurnEnded = "turn-ended";

    /// <summary><see cref="MentorActionOutcome.Executed"/>: the action ran.</summary>
    public const string Completed = "completed";

    /// <summary>
    /// <see cref="MentorActionOutcome.Executed"/>: the action ran, and <c>OnToolResult</c> threw, so its result was
    /// withheld. The MCP server's caller gets an error result.
    /// </summary>
    public const string ResultWithheld = "result-withheld";

    /// <summary><see cref="MentorActionOutcome.Failed"/>: the action threw.</summary>
    public const string Exception = "exception";
}
