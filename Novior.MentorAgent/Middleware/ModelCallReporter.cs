using System.Security.Claims;
using Novior.MentorAgent.Core;

namespace Novior.MentorAgent.Middleware;

/// <summary>Story 1289 — whom a client's calls to a model are attributed to: an agent, or a check of MentorAgent's own.</summary>
/// <param name="Kind">The agent's kind, or <see langword="null"/> for a check.</param>
/// <param name="Name">
/// The agent's name, read as each call starts: the coordinator is named after its client is built, and the agents of
/// an <see cref="IMentorAgentSource"/> share one client, so theirs is the agent running the call.
/// </param>
/// <param name="Purpose">What the calls are for.</param>
internal sealed record ModelCallAttribution(MentorAgentKind? Kind, Func<string?> Name, MentorModelCallPurpose Purpose)
{
    internal static ModelCallAttribution Check(MentorModelCallPurpose purpose) => new(null, static () => null, purpose);

    /// <summary>An agent under the coordinator, named when it is built.</summary>
    internal static ModelCallAttribution Agent(MentorAgentKind kind, string name) => new(kind, () => name, MentorModelCallPurpose.Reply);

    /// <summary>The agents of the sources: the one running the call, as for their decisions.</summary>
    internal static ModelCallAttribution RunningAgent(MentorAgentKind kind)
        => new(kind, static () => AIAgent.CurrentRunContext?.Agent?.Name, MentorModelCallPurpose.Reply);
}

/// <summary>
/// Story 1289 — hands each call to a model to the action observers that take them (<see cref="IMentorActionObserver.OnModelCallAsync"/>),
/// with the session, the turn, the surface, the user and the tenant of the session it serves. Those are read in the
/// call's own flow, before the call, as the gate reads them for a decision; and only when an observer takes the calls,
/// so a host without one pays nothing.
/// </summary>
/// <param name="gate">The gate of the scope the calls are made in.</param>
/// <param name="outsideSessions">
/// For the calls that serve nobody — the warm-up: no session, no turn, no user, no tenant, whatever the scope holds.
/// </param>
internal sealed class ModelCallReporter(MentorAgentUIMiddleware gate, bool outsideSessions = false)
{
    /// <summary>What is known of a call when it starts.</summary>
    internal sealed record Start(string? SessionId, string? TurnId, MentorActionSurface Surface, ClaimsPrincipal? User, string? TenantId,
        string? AgentName);

    /// <summary>The call's context, or <see langword="null"/> when no observer takes the calls.</summary>
    internal async ValueTask<Start?> BeginAsync(ModelCallAttribution who, CancellationToken cancellationToken)
    {
        if (gate.Decisions is not { HasCallObservers: true }) return null;
        // Before anything is awaited: the flow is the call's own.
        var agentName = who.Name();
        if (outsideSessions) return new(null, null, MentorActionSurface.InProcess, null, null, agentName);
        ClaimsPrincipal? user;
        try
        {
            user = await gate.ReadUserAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            user = null;   // the call is cancelled too, and says so
        }
        catch (Exception)
        {
            // Never into the call: outside a turn — the warm-up, a capture after it — there may be nobody to read.
            user = null;
        }
        return new(gate.Session?.Id, gate.Turn?.TurnId, gate.Turn?.Surface ?? MentorActionSurface.InProcess, user,
            MentorTenantContext.Ambient, agentName);
    }

    internal void Publish(Start start, ModelCallAttribution who, MentorModelCallOutcome outcome, string? modelId,
        long inputTokens, long outputTokens, long? cachedInputTokens, long? reasoningTokens, bool usageMeasured, TimeSpan duration)
    {
        if (gate.Decisions is not { } decisions) return;
        decisions.Publish(new MentorModelCall
        {
            Purpose           = who.Purpose,
            Outcome           = outcome,
            Surface           = start.Surface,
            AgentKind         = who.Kind,
            AgentName         = start.AgentName,
            SessionId         = start.SessionId,
            TurnId            = start.TurnId,
            User              = start.User,
            TenantId          = start.TenantId,
            ModelId           = modelId,
            InputTokens       = usageMeasured ? inputTokens : 0,
            OutputTokens      = usageMeasured ? outputTokens : 0,
            CachedInputTokens = usageMeasured ? cachedInputTokens : null,
            ReasoningTokens   = usageMeasured ? reasoningTokens : null,
            UsageMeasured     = usageMeasured,
            Duration          = duration,
            Timestamp         = decisions.Time.GetUtcNow(),
        });
    }
}
