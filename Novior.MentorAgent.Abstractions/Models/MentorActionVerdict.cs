namespace Novior.MentorAgent.Abstractions.Models;

/// <summary>What <see cref="Interfaces.IMentorActionPolicy.BeforeActionAsync"/> answers for one call.</summary>
public sealed record MentorActionVerdict
{
    /// <summary>No objection.</summary>
    public static MentorActionVerdict Proceed { get; } = new() { Kind = MentorActionVerdictKind.Proceed };

    /// <summary>What the policy decided.</summary>
    public required MentorActionVerdictKind Kind { get; init; }

    /// <summary>
    /// Why, in the application's words. The model reads it when the call is stopped, and the action observers
    /// receive it as <see cref="MentorActionDecision.Detail"/>. Never a secret: it reaches the model and the logs
    /// of whoever observes the actions.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>Asks the user to confirm the call first; where nobody can confirm, the call is refused.</summary>
    /// <param name="reason">Why, in the application's words; optional.</param>
    public static MentorActionVerdict Confirm(string? reason = null)
        => new() { Kind = MentorActionVerdictKind.Confirm, Reason = reason };

    /// <summary>Stops the call: the action does not run, and the model reads <paramref name="reason"/>.</summary>
    /// <param name="reason">Why, in the application's words.</param>
    public static MentorActionVerdict Stop(string reason)
        => new() { Kind = MentorActionVerdictKind.Stop, Reason = reason };
}
