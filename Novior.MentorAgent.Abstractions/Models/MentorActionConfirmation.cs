using System.Security.Claims;

namespace Novior.MentorAgent.Abstractions.Models;

/// <summary>
/// The confirmation of the action that is running: who confirmed it and when, for the action's own code — a
/// ledger of the writes, for one. Read <see cref="Current"/> inside the method of a
/// <c>[MentorAction(RequiresConfirmation = true)]</c>. More properties may be added.
/// </summary>
public sealed record MentorActionConfirmation
{
    private static readonly AsyncLocal<MentorActionConfirmation?> CurrentValue = new();

    /// <summary>
    /// The confirmation of the action running on this call, or <see langword="null"/> when nobody confirmed it: the
    /// action asks for no confirmation, an auto-approval rule let it run (Native mode), or it was called over the
    /// MCP server. Set by MentorAgent's gate for the time the action runs, in Blocking and in Native mode alike.
    /// </summary>
    public static MentorActionConfirmation? Current
    {
        get => CurrentValue.Value;
        internal set => CurrentValue.Value = value;
    }

    /// <summary>
    /// The confirmation's id: the <see cref="ConfirmationRequest.ActionId"/> the banner carried, and the
    /// <see cref="MentorActionDecision.ConfirmationId"/> of the gate's decisions about this call.
    /// </summary>
    public required Guid ConfirmationId { get; init; }

    /// <summary>The tool's technical name, as the model called it.</summary>
    public required string ToolName { get; init; }

    /// <summary>When the user confirmed, in UTC.</summary>
    public required DateTimeOffset ConfirmedAt { get; init; }

    /// <summary>
    /// Who confirmed: the caller of <c>POST /mentor/approve</c>; in a circuit and in the MAUI app, the session's
    /// signed-in user. <see langword="null"/> when nobody is signed in.
    /// </summary>
    public ClaimsPrincipal? ConfirmedBy { get; init; }
}
