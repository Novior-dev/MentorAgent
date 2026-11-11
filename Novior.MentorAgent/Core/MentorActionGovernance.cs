namespace Novior.MentorAgent.Core;

/// <summary>
/// Asks the application's <see cref="IMentorActionGovernor"/> about an action (stories 1549, 1546, 1547, 1548): whether
/// it is on, the roles it requires, whether the user confirms it and the description the model reads — with what the
/// code says when there is no governor or it leaves the code's value. One
/// place for the timeout and the failures, so the session's tools, the gate, the Native flow and the MCP server decide
/// by the same rule.
///
/// <para>
/// A governor that throws, or does not answer within <see cref="MentorOptions.ActionPolicyTimeout"/>, fails closed:
/// the action is off for that question, with <see cref="MentorActionReasons.GovernorFailed"/>. Going back to the code
/// would widen again what the governor narrowed. The log has the exception's type, never its message: the governor is
/// the application's code (BUG-148). A cancelled turn is not a failure: its <see cref="OperationCanceledException"/>
/// goes on to the caller.
/// </para>
/// </summary>
internal sealed class MentorActionGovernance
{
    private readonly Lazy<IMentorActionGovernor[]> _governors;
    private readonly MentorOptions _options;
    private readonly ILogger _logger;
    private int _manyLogged;

    public MentorActionGovernance(IServiceProvider services, IOptions<MentorOptions> options, ILogger<MentorActionGovernance> logger)
    {
        _options = options.Value;
        _logger  = logger;
        // Read at the first question, not at construction: a governor registered after AddMentorAgent() counts.
        _governors = new(() => [.. services.GetServices<IMentorActionGovernor>()], LazyThreadSafetyMode.ExecutionAndPublication);
    }

    private IMentorActionGovernor[] Governors
    {
        get
        {
            try { return _governors.Value; }
            catch (ObjectDisposedException) { return []; }   // the application is shutting down
        }
    }

    /// <summary>Whether the application registers a governor. Without one the code decides, and nobody is read for it.</summary>
    internal bool Any => Governors.Length > 0;

    /// <summary>
    /// Whether the action of <paramref name="request"/> is on: the governor's answer, or
    /// <paramref name="enabledByDefault"/> — the code's — when it has none or there is no governor.
    /// </summary>
    internal async ValueTask<GovernorAnswer> IsEnabledAsync(MentorActionRequest request, bool enabledByDefault, CancellationToken ct)
    {
        var (answer, failure) = await AskAsync(request, static (g, r, t) => g.IsEnabledAsync(r, t), ct);
        return failure ?? new(answer ?? enabledByDefault);
    }

    /// <summary>
    /// The roles the action of <paramref name="request"/> requires, when the governor gives them in place of the
    /// code's (story 1546): <see cref="GovernorRoles.Roles"/> null leaves the code's, which the request carries.
    /// </summary>
    internal async ValueTask<GovernorRoles> RequiredRolesAsync(MentorActionRequest request, CancellationToken ct)
    {
        var (roles, failure) = await AskAsync(request, static (g, r, t) => g.GetRequiredRolesAsync(r, t), ct);
        return failure is { } f
            ? new(null, true, f.ExceptionType)
            : new(roles is null ? null : [.. roles.Where(r => !string.IsNullOrWhiteSpace(r))]);
    }

    /// <summary>
    /// Whether the user confirms the action of <paramref name="request"/>, when the governor decides it in place of the
    /// code (story 1547): <see cref="MentorConfirmationChoice.Code"/> leaves the code's, which the request carries. An
    /// answer that is not one of the choices is the code's.
    /// </summary>
    internal async ValueTask<GovernorConfirmation> ConfirmationAsync(MentorActionRequest request, CancellationToken ct)
    {
        var (choice, failure) = await AskAsync(request, static (g, r, t) => g.GetConfirmationAsync(r, t), ct);
        return failure is { } f
            ? new(MentorConfirmationChoice.Code, true, f.ExceptionType)
            : new(Enum.IsDefined(choice) ? choice : MentorConfirmationChoice.Code);
    }

    /// <summary>
    /// The description the model reads for the action of <paramref name="request"/>, when the governor gives one in
    /// place of the code's (story 1548): <see cref="GovernorDescription.Description"/> null leaves the code's, which
    /// the request carries. A blank answer is the code's.
    /// </summary>
    internal async ValueTask<GovernorDescription> DescriptionAsync(MentorActionRequest request, CancellationToken ct)
    {
        var (description, failure) = await AskAsync(request, static (g, r, t) => g.GetDescriptionAsync(r, t), ct);
        return failure is { } f
            ? new(null, true, f.ExceptionType)
            : new(string.IsNullOrWhiteSpace(description) ? null : description.Trim());
    }

    /// <summary>
    /// One question to the governor, bounded by <see cref="MentorOptions.ActionPolicyTimeout"/>: its answer, or the
    /// failure when it threw or did not answer in time. Without a governor the answer is the default: the code's.
    /// </summary>
    private async ValueTask<(T? Answer, GovernorAnswer? Failure)> AskAsync<T>(MentorActionRequest request,
        Func<IMentorActionGovernor, MentorActionRequest, CancellationToken, ValueTask<T>> ask, CancellationToken ct)
    {
        var governors = Governors;
        if (governors.Length == 0) return (default, null);

        // AddMentorActionGovernor refuses a second one; two registered by hand have no answer that is obviously the
        // stricter, so neither is followed.
        if (governors.Length > 1)
        {
            if (Interlocked.Exchange(ref _manyLogged, 1) == 0)
                _logger.LogError(
                    "[MentorAgent] {Count} IMentorActionGovernor services are registered; there can be one. Every action " +
                    "the governor decides is off until only one is.", governors.Length);
            return (default, GovernorAnswer.Failure(null));
        }

        var governor = governors[0];
        var timeout = _options.ActionPolicyTimeout;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout > TimeSpan.Zero) cts.CancelAfter(timeout);

        try
        {
            var answer = ask(governor, request, cts.Token).AsTask();
            return (timeout > TimeSpan.Zero ? await answer.WaitAsync(timeout, ct) : await answer.WaitAsync(ct), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // Stop, or the session closing: the caller's to report
        }
        catch (Exception ex) when (ex is TimeoutException || (ex is OperationCanceledException && cts.IsCancellationRequested))
        {
            _logger.LogWarning(
                "[MentorAgent] Action governor {Governor} did not answer within {Timeout} s for '{Tool}' — {What}.",
                Name(governor), timeout.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                request.ToolName, What(request));
            return (default, GovernorAnswer.Failure(null));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "[MentorAgent] Action governor {Governor} threw ({Type}) for '{Tool}' — {What}.",
                Name(governor), ex.GetType().Name, request.ToolName, What(request));
            _logger.LogDebug(ex, "[MentorAgent] Action governor {Governor} threw.", Name(governor));
            return (default, GovernorAnswer.Failure(ex.GetType().FullName));
        }
    }

    private static string What(MentorActionRequest request)
        => request.Stage == MentorActionStage.Offer ? "the action is not offered" : "the call is not run";

    private static string Name(IMentorActionGovernor governor) => governor.GetType().FullName ?? governor.GetType().Name;
}

/// <summary>
/// What the governance decided about whether one action is on: on or off, and whether the governor failed — off then,
/// with the exception's type when it threw.
/// </summary>
internal readonly record struct GovernorAnswer(bool Enabled, bool Failed = false, string? ExceptionType = null)
{
    internal static GovernorAnswer Failure(string? exceptionType) => new(false, true, exceptionType);
}

/// <summary>
/// The roles the governor gives one action (story 1546): null for the code's, and whether the governor failed — the
/// action is not offered and the call does not run then, with the exception's type when it threw.
/// </summary>
internal readonly record struct GovernorRoles(string[]? Roles, bool Failed = false, string? ExceptionType = null);

/// <summary>
/// The governor's answer about one action's confirmation (story 1547), and whether it failed — the action is not
/// offered and the call does not run then, with the exception's type when it threw.
/// </summary>
internal readonly record struct GovernorConfirmation(MentorConfirmationChoice Choice, bool Failed = false, string? ExceptionType = null);

/// <summary>
/// The description the governor gives one action (story 1548): null for the code's, and whether the governor failed —
/// the action is not offered then, with the exception's type when it threw.
/// </summary>
internal readonly record struct GovernorDescription(string? Description, bool Failed = false, string? ExceptionType = null);
