namespace Novior.MentorAgent.Options;

/// <summary>
/// What a safety check does when it cannot reach a verdict — the classifier timed out
/// (<see cref="MentorOptions.SafetyCheckTimeout"/>), the provider returned an error, or a custom
/// guardrail threw. Applies to the input check (<see cref="MentorOptions.EnableSafetyCheck"/>,
/// <see cref="MentorOptions.InputGuardrail"/>) and to the output check
/// (<see cref="MentorOptions.EnableOutputSafetyCheck"/>, <see cref="MentorOptions.OutputGuardrail"/>).
///
/// <para>
/// A verdict of UNSAFE is not a failure: it blocks under both settings. Neither is a check the user
/// cancelled with Stop — the turn simply ends.
/// </para>
/// </summary>
public enum MentorSafetyCheckFailure
{
    /// <summary>
    /// Fail open (default): the message is processed, the reply is shown, and a warning is logged.
    /// A classifier outage does not turn into an assistant that refuses everything.
    /// </summary>
    Allow = 0,

    /// <summary>
    /// Fail closed: the message is refused ("the check is unavailable, try again"), or the reply is
    /// withheld, and a warning is logged. For deployments where an unchecked message is worse than
    /// no answer. Counted as a guardrail block on the dashboard.
    /// </summary>
    Block = 1,
}
