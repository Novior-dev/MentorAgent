namespace Novior.MentorAgent.Core;

/// <summary>
/// Asks the application's <see cref="IMentorActionPolicy"/> instances (stories 311, 153): whether an action is
/// offered, and whether a call may run. One place for the order, the timeout and the failures, so the gate, the
/// Native flow, the coordinator's turn and the MCP server decide by the same rule.
///
/// <para>
/// The policies are asked one after the other, in the order of registration. <see cref="IMentorActionPolicy.IsAllowedAsync"/>
/// stops at the first no. <see cref="IMentorActionPolicy.BeforeActionAsync"/> asks every policy, and the strictest
/// answer wins — Stop, then Confirm, then Proceed — with the reason of the first policy that gave it.
/// </para>
///
/// <para>
/// A policy that throws, returns nothing or does not answer within <see cref="MentorOptions.ActionPolicyTimeout"/>
/// fails closed: the call does not run, and an action being offered is left out. The log has the exception's type,
/// never its message: the policy is the application's code, and its message can carry anything (BUG-148). A
/// cancelled turn is not a failure: its <see cref="OperationCanceledException"/> goes on to the caller.
/// </para>
/// </summary>
internal sealed class MentorActionPolicies
{
    private readonly Lazy<IMentorActionPolicy[]> _policies;
    private readonly MentorOptions _options;
    private readonly ILogger _logger;

    public MentorActionPolicies(IServiceProvider services, IOptions<MentorOptions> options, ILogger<MentorActionPolicies> logger)
    {
        _options = options.Value;
        _logger  = logger;
        // Read at the first question, not at construction: a policy registered after AddMentorAgent() counts.
        _policies = new(() => [.. services.GetServices<IMentorActionPolicy>()], LazyThreadSafetyMode.ExecutionAndPublication);
    }

    private IMentorActionPolicy[] Policies
    {
        get
        {
            try { return _policies.Value; }
            catch (ObjectDisposedException) { return []; }   // the application is shutting down
        }
    }

    /// <summary>Whether the application registers a policy. Without one nothing is asked, and nobody is read for it.</summary>
    internal bool Any => Policies.Length > 0;

    /// <summary>
    /// Whether every policy offers the action (<see cref="MentorActionStage.Offer"/>): false at the first no, and for
    /// a policy that fails — leaving the action out is the safe answer.
    /// </summary>
    internal async ValueTask<bool> IsOfferedAsync(MentorActionRequest request, CancellationToken ct)
    {
        foreach (var policy in Policies)
        {
            var (allowed, failure) = await AskAsync(policy, request, static (p, r, t) => p.IsAllowedAsync(r, t), ct);
            if (failure is not null || !allowed) return false;
        }
        return true;
    }

    /// <summary>
    /// The policies' decision about one call (<see cref="MentorActionStage.Call"/>): <see cref="IMentorActionPolicy.IsAllowedAsync"/>
    /// of each, then — unless <paramref name="allowedOnly"/>, for a call the user has just confirmed —
    /// <see cref="IMentorActionPolicy.BeforeActionAsync"/> of each.
    /// </summary>
    internal async ValueTask<PolicyCheck> CheckCallAsync(MentorActionRequest request, bool allowedOnly, CancellationToken ct)
    {
        var policies = Policies;

        foreach (var policy in policies)
        {
            var (allowed, failure) = await AskAsync(policy, request, static (p, r, t) => p.IsAllowedAsync(r, t), ct);
            if (failure is { } f) return f;
            if (!allowed) return new(PolicyResult.NotAllowed, ReasonCode: MentorActionReasons.NotAuthorized);
        }

        if (allowedOnly) return PolicyCheck.Proceed;

        MentorActionVerdict? strictest = null;
        foreach (var policy in policies)
        {
            var (verdict, failure) = await AskAsync(policy, request, static (p, r, t) => p.BeforeActionAsync(r, t), ct);
            if (failure is { } f) return f;
            if (verdict is null)
            {
                _logger.LogWarning(
                    "[MentorAgent] Action policy {Policy} returned no verdict for '{Tool}' — the call is not run.",
                    Name(policy), request.ToolName);
                return new(PolicyResult.Failed, ReasonCode: MentorActionReasons.PolicyException);
            }
            if (strictest is null || Rank(verdict.Kind) > Rank(strictest.Kind)) strictest = verdict;
        }

        return strictest is null || Rank(strictest.Kind) == 0
            ? PolicyCheck.Proceed
            : new(Rank(strictest.Kind) == 1 ? PolicyResult.Confirm : PolicyResult.Stop, strictest.Reason,
                  Rank(strictest.Kind) == 1 ? null : MentorActionReasons.Policy);
    }

    /// <summary>Proceed 0, Confirm 1, Stop 2; a value this version does not know counts as Stop.</summary>
    private static int Rank(MentorActionVerdictKind kind) => kind switch
    {
        MentorActionVerdictKind.Proceed => 0,
        MentorActionVerdictKind.Confirm => 1,
        _                               => 2,
    };

    /// <summary>
    /// One question to one policy, bounded by <see cref="MentorOptions.ActionPolicyTimeout"/>. The failure, when the
    /// policy threw or did not answer in time; the turn's own cancellation is thrown on.
    /// </summary>
    private async ValueTask<(T? Value, PolicyCheck? Failure)> AskAsync<T>(IMentorActionPolicy policy,
        MentorActionRequest request, Func<IMentorActionPolicy, MentorActionRequest, CancellationToken, ValueTask<T>> ask,
        CancellationToken ct)
    {
        var timeout = _options.ActionPolicyTimeout;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout > TimeSpan.Zero) cts.CancelAfter(timeout);

        try
        {
            var answer = ask(policy, request, cts.Token).AsTask();
            return (timeout > TimeSpan.Zero ? await answer.WaitAsync(timeout, ct) : await answer.WaitAsync(ct), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // Stop, or the session closing: the caller's to report
        }
        catch (Exception ex) when (ex is TimeoutException || (ex is OperationCanceledException && cts.IsCancellationRequested))
        {
            _logger.LogWarning(
                "[MentorAgent] Action policy {Policy} did not answer within {Timeout} s for '{Tool}' — {What}.",
                Name(policy), timeout.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                request.ToolName, What(request));
            return (default, new(PolicyResult.Failed, ReasonCode: MentorActionReasons.PolicyTimeout));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "[MentorAgent] Action policy {Policy} threw ({Type}) for '{Tool}' — {What}.",
                Name(policy), ex.GetType().Name, request.ToolName, What(request));
            _logger.LogDebug(ex, "[MentorAgent] Action policy {Policy} threw.", Name(policy));
            return (default, new(PolicyResult.Failed, ReasonCode: MentorActionReasons.PolicyException,
                ExceptionType: ex.GetType().FullName));
        }
    }

    private static string What(MentorActionRequest request)
        => request.Stage == MentorActionStage.Offer ? "the action is not offered" : "the call is not run";

    private static string Name(IMentorActionPolicy policy) => policy.GetType().FullName ?? policy.GetType().Name;
}

/// <summary>What the policies decided about one call.</summary>
internal enum PolicyResult
{
    /// <summary>No objection.</summary>
    Proceed,
    /// <summary>A policy asks the user to confirm first.</summary>
    Confirm,
    /// <summary>A policy stopped the call (<see cref="MentorActionOutcome.Blocked"/>).</summary>
    Stop,
    /// <summary>A policy does not allow the user the action (<see cref="MentorActionOutcome.Denied"/>).</summary>
    NotAllowed,
    /// <summary>A policy threw or did not answer (<see cref="MentorActionOutcome.PolicyFailed"/>).</summary>
    Failed,
}

/// <summary>
/// The policies' decision about one call: what, the policy's own reason (Stop, Confirm), the reason code of the
/// gate's decision, and the exception's type for a failure.
/// </summary>
internal readonly record struct PolicyCheck(PolicyResult Result, string? Reason = null, string? ReasonCode = null, string? ExceptionType = null)
{
    internal static PolicyCheck Proceed { get; } = new(PolicyResult.Proceed);
}
