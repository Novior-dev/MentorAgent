namespace Novior.MentorAgent.Abstractions.Models;

/// <summary>
/// A tool call that started: the tool's technical name, as the model called it, and the text shown to the user for it.
/// Raised by <see cref="Interfaces.IMentorStateService.OnActionStarted"/>. More properties may be added.
/// </summary>
public sealed record MentorActionStarted
{
    /// <summary>The tool's technical name, as the model called it (for example <c>cancel_order</c>).</summary>
    public required string ToolName { get; init; }

    /// <summary>
    /// The text shown to the user while the tool runs — the one <see cref="Interfaces.IMentorStateService.OnActionExecuting"/>
    /// carries: the action's description, else a readable form of its name, or the routing, team or hosted-tool text.
    /// </summary>
    public required string DisplayName { get; init; }
}
