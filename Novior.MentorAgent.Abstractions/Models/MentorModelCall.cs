using System.Security.Claims;

namespace Novior.MentorAgent.Abstractions.Models;

/// <summary>
/// One call to a model, as <see cref="Interfaces.IMentorActionObserver.OnModelCallAsync"/> receives it: the tokens it
/// used, the agent it is attributed to, and for whom. One for each call — each round trip of an agent, each check of a
/// classifier — so a turn has several. Never the text of the request or of the answer. More properties may be added.
/// </summary>
/// <remarks>
/// The calls and the decisions (<see cref="MentorActionDecision"/>) reach an observer on two queues of their own, so
/// the order between a call and a decision is not kept. Both carry <see cref="SessionId"/>, <see cref="TurnId"/> and
/// <see cref="Timestamp"/>: sort by them to put a turn back together. The totals per model of the dashboard are
/// <see cref="MentorModelUsage"/>.
/// </remarks>
public sealed record MentorModelCall
{
    /// <summary>What the call was for: the agent's reply, or one of MentorAgent's own checks.</summary>
    public required MentorModelCallPurpose Purpose { get; init; }

    /// <summary>How the call ended.</summary>
    public required MentorModelCallOutcome Outcome { get; init; }

    /// <summary>Where the turn came from.</summary>
    public required MentorActionSurface Surface { get; init; }

    /// <summary>
    /// The kind of agent the call is attributed to, or <see langword="null"/> for a check MentorAgent makes on its own
    /// (<see cref="Purpose"/> says which).
    /// </summary>
    public MentorAgentKind? AgentKind { get; init; }

    /// <summary>The name of the agent the call is attributed to, when it has one.</summary>
    public string? AgentName { get; init; }

    /// <summary>An opaque id of the session, the same as <see cref="MentorActionDecision.SessionId"/>.</summary>
    public string? SessionId { get; init; }

    /// <summary>An opaque id of the turn, the same as <see cref="MentorActionDecision.TurnId"/>; none outside a turn.</summary>
    public string? TurnId { get; init; }

    /// <summary>The signed-in user who asked, as <see cref="MentorActionDecision.User"/>; <see langword="null"/> when nobody is.</summary>
    public ClaimsPrincipal? User { get; init; }

    /// <summary>The tenant of the turn, as <see cref="MentorActionDecision.TenantId"/>.</summary>
    public string? TenantId { get; init; }

    /// <summary>The model that answered, as the provider named it, else the deployment the client points at.</summary>
    public string? ModelId { get; init; }

    /// <summary>The tokens of the request; 0 when <see cref="UsageMeasured"/> is false.</summary>
    public long InputTokens { get; init; }

    /// <summary>The tokens of the answer; 0 when <see cref="UsageMeasured"/> is false.</summary>
    public long OutputTokens { get; init; }

    /// <summary>Of <see cref="InputTokens"/>, those the provider read from its cache, when it says so.</summary>
    public long? CachedInputTokens { get; init; }

    /// <summary>Of <see cref="OutputTokens"/>, those spent on reasoning, when the provider says so.</summary>
    public long? ReasoningTokens { get; init; }

    /// <summary>
    /// Whether the provider said how many tokens the call used. A stream without a usage part, a call that failed or
    /// was stopped: the counts are unknown, not zero.
    /// </summary>
    public bool UsageMeasured { get; init; }

    /// <summary>How long the call took.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>When the call ended.</summary>
    public required DateTimeOffset Timestamp { get; init; }
}

/// <summary>What a call to a model was for (<see cref="MentorModelCall.Purpose"/>).</summary>
public enum MentorModelCallPurpose
{
    /// <summary>A round trip of an agent's reply, tool calls included.</summary>
    Reply = 0,

    /// <summary>The check of the user's message (<c>EnableSafetyCheck</c>), with the question of the scope when it asks both.</summary>
    SafetyCheck = 1,

    /// <summary>The check of the reply before it is shown (<c>EnableOutputSafetyCheck</c>).</summary>
    OutputSafetyCheck = 2,

    /// <summary>Whether a message belongs to the application, before a hosted tool runs.</summary>
    ScopeCheck = 3,

    /// <summary>Whether the user asks to change something (<c>RequireActionForChangeRequests</c>).</summary>
    RequestCheck = 4,

    /// <summary>Whether a reply announces an action, or reports one as done, that no tool ran (<c>CheckAnnouncedActions</c>).</summary>
    AnnouncedActionCheck = 5,

    /// <summary>Whether a refusal of the reply was right.</summary>
    RefusalCheck = 6,

    /// <summary>Whether a reply written without a tool states a value of the application's records (A2A).</summary>
    GroundingCheck = 7,

    /// <summary>The facts about the user kept by the memory (<c>MemoryAutoCapture</c>).</summary>
    MemoryExtraction = 8,

    /// <summary>Another call MentorAgent makes on its own.</summary>
    Other = 9,

    /// <summary>
    /// Whether a message needs the strong model, or whether the cheap model's answer was complete
    /// (<c>RoutingStrategy</c> <c>Classifier</c> or <c>Cascade</c>).
    /// </summary>
    RoutingCheck = 10,

    /// <summary>
    /// The request that warms the model up when the application starts (<c>WarmUpChatModel</c>): outside any session, with
    /// no user and no turn.
    /// </summary>
    WarmUp = 11,
}

/// <summary>How a call to a model ended (<see cref="MentorModelCall.Outcome"/>).</summary>
public enum MentorModelCallOutcome
{
    /// <summary>The model answered.</summary>
    Completed = 0,

    /// <summary>The call was stopped: the user pressed Stop, a limit of time, or the reader stopped reading.</summary>
    Cancelled = 1,

    /// <summary>The call failed.</summary>
    Failed = 2,
}
