using System.Collections.Concurrent;
using System.Security.Claims;
using Novior.MentorAgent.Evaluation;
using Novior.MentorAgent.Middleware;

namespace Novior.MentorAgent.Core;

/// <summary>
/// What the gate's decisions say about the turn they belong to (story 180). Scoped: one per circuit, hub
/// connection, request or A2A task.
///
/// <para>
/// In Native mode a call is decided in two places: <c>MentorOrchestrator</c> checks the roles and asks for the
/// confirmation, then the approved call comes back through the gate in <c>MentorAgentUIMiddleware</c>, which
/// checks the roles again and runs it. The confirmed calls are kept here by their call id, so the gate knows
/// the call was confirmed and the decisions read the same as in Blocking mode.
/// </para>
/// </summary>
internal sealed class MentorTurnContext
{
    private readonly ConcurrentDictionary<string, GateCall> _confirmed = new(StringComparer.Ordinal);

    /// <summary>The calls of this turn that reached the gate, by the model's id (story 526).</summary>
    private readonly ConcurrentDictionary<string, byte> _gated = new(StringComparer.Ordinal);

    /// <summary>
    /// The actions the application's policies did not offer in this turn, by agent (BUG-233): a call to one is refused
    /// at the gate, whatever the policies answer about the call.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _notOffered = new(StringComparer.Ordinal);

    /// <summary>Where this scope's turns come from. Set by the transport; in-process when none runs.</summary>
    internal MentorActionSurface Surface { get; set; } = MentorActionSurface.InProcess;

    /// <summary>The current turn's id, new for every message; <see langword="null"/> before the first one.</summary>
    internal string? TurnId { get; private set; }

    /// <summary>
    /// Story 171 — told each of the gate's decisions in this scope as it is taken (tool name, outcome, reason code),
    /// whether or not an <see cref="IMentorActionObserver"/> is registered and whatever <c>EnableActionFeedback</c>
    /// says: how an evaluated conversation knows the tools its turn called. Set by the evaluator only.
    /// </summary>
    internal Action<string, MentorActionOutcome, string>? Decided { get; set; }

    /// <summary>
    /// Story 1623 — told when the turn is refused instead of answered, with the refusal and the text shown, just before
    /// that text goes to <c>OnError</c>: how an evaluated conversation tells a refusal from an error. Only the two
    /// judgements on the message call it, never a safety check that could not run. Set by the evaluator only.
    /// </summary>
    internal Action<EvalRefusal, string>? Refused { get; set; }

    /// <summary>
    /// Whether the user asked to stop this turn. A cancelled token without it is the session ending — the
    /// circuit, the hub connection or the request closing.
    /// </summary>
    internal bool StopRequested { get; set; }

    /// <summary>
    /// The turn's own token: cancelled by Stop and by the end of the session. The gate reads it right before an
    /// action runs (BUG-204): the token a specialist's call carries is the handoff workflow's, which Stop reaches a
    /// moment later.
    /// </summary>
    internal CancellationToken Cancellation { get; private set; }

    /// <summary>
    /// Whether an agent under the coordinator — a specialist, a team member, an agent of an
    /// <c>IMentorAgentSource</c> — holds a tool that needs a role, so that every turn decides whom its tools are
    /// offered to (BUG-205). Set when those agents are built.
    /// </summary>
    internal bool AgentsHoldRoleGatedTools { get; set; }

    /// <summary>
    /// Whether this turn decided whom its tools are offered to (BUG-200, BUG-205): false on a turn that read nobody
    /// for it, because no action needs a role, and every tool is offered.
    /// </summary>
    internal bool OfferDecided { get; private set; }

    /// <summary>The user this turn's tools are offered to, read once as the turn starts; null when nobody is signed in.</summary>
    internal ClaimsPrincipal? OfferedTo { get; private set; }

    /// <summary>
    /// Whether a person on this turn can sign in: nobody is signed in, the application has authentication, and the
    /// turn is not a task from another agent or a call to the MCP server.
    /// </summary>
    internal bool CanSignIn { get; private set; }

    /// <summary>
    /// The language of this turn, as the orchestrator read it when the turn started: the agents under the coordinator
    /// write their notice of a tool left out in it, as the coordinator does (BUG-224).
    /// </summary>
    internal MentorLanguage Language { get; private set; }

    /// <summary>Starts a turn: a new id, and nothing left of the one before.</summary>
    internal void BeginTurn(CancellationToken cancellation = default)
    {
        TurnId = Guid.NewGuid().ToString("N");
        StopRequested = false;
        Cancellation = cancellation;
        OfferDecided = false;
        OfferedTo = null;
        CanSignIn = false;
        Language = default;
        _confirmed.Clear();
        _gated.Clear();
        _notOffered.Clear();
    }

    /// <summary>
    /// Records that the application's policies did not offer <paramref name="toolName"/> to this agent in this turn
    /// (BUG-233). A specialist and a team member are told apart by their name; the coordinator and the agents of the
    /// agent sources, which share one client, by their kind.
    /// </summary>
    internal void NotOfferedByPolicies(MentorAgentKind agentKind, string? agentName, string toolName)
        => _notOffered[NotOfferedKey(agentKind, agentName, toolName)] = 0;

    /// <summary>Whether the application's policies did not offer <paramref name="toolName"/> to this agent in this turn.</summary>
    internal bool WasNotOfferedByPolicies(MentorAgentKind agentKind, string? agentName, string toolName)
        => _notOffered.ContainsKey(NotOfferedKey(agentKind, agentName, toolName));

    private static string NotOfferedKey(MentorAgentKind agentKind, string? agentName, string toolName)
        => string.Concat(((int)agentKind).ToString(System.Globalization.CultureInfo.InvariantCulture), "\u001f",
            agentKind is MentorAgentKind.Specialist or MentorAgentKind.TeamMember ? agentName : null, "\u001f", toolName);

    /// <summary>
    /// Records whom this turn's tools are offered to, as the orchestrator read the user when the turn started, and the
    /// turn's language.
    /// </summary>
    internal void DecideOffer(ClaimsPrincipal? user, bool canSignIn, MentorLanguage language)
    {
        OfferedTo = user;
        CanSignIn = canSignIn;
        Language = language;
        OfferDecided = true;
    }

    /// <summary>The reason a cancelled call stopped: the user's Stop, or the end of the session.</summary>
    internal string StopReason => StopRequested ? MentorActionReasons.Stop : MentorActionReasons.SessionEnded;

    /// <summary>
    /// Records that the call <paramref name="callId"/> reached the gate. Every function the model calls and its agent
    /// finds reaches it, on both paths; one the agent does not have — not offered in this turn — answers "not found"
    /// without reaching it (story 526).
    /// </summary>
    internal void Gated(string? callId)
    {
        if (callId is not null) _gated[callId] = 0;
    }

    /// <summary>Whether the call <paramref name="callId"/> of this turn reached the gate.</summary>
    internal bool WasGated(string callId) => _gated.ContainsKey(callId);

    /// <summary>Records a call the user confirmed before it reached the gate (Native mode).</summary>
    internal void Confirmed(GateCall call)
    {
        if (call.CallId is { } id) _confirmed[id] = call;
    }

    /// <summary>The confirmed call with <paramref name="callId"/>, taken by the gate that runs it; null when it was not confirmed.</summary>
    internal GateCall? TakeConfirmed(string? callId)
        => callId is not null && _confirmed.TryRemove(callId, out var call) ? call : null;

    /// <summary>
    /// The confirmed calls that never reached the gate, taken: the run ended before it ran them. Their last
    /// decision is the orchestrator's to give.
    /// </summary>
    internal List<GateCall> TakeConfirmedLeft()
    {
        var left = new List<GateCall>();
        foreach (var id in _confirmed.Keys)
            if (_confirmed.TryRemove(id, out var call)) left.Add(call);
        return left;
    }
}
