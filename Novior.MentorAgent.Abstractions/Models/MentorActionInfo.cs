namespace Novior.MentorAgent.Abstractions.Models;

/// <summary>
/// One of the application's actions as MentorAgent sees it, from <see cref="Interfaces.IMentorActionCatalog"/>: who
/// may run it, whether it asks for a confirmation, and where it is published. More properties may be added.
/// </summary>
public sealed record MentorActionInfo
{
    /// <summary>The tool's technical name, as the model or the MCP client calls it (for example <c>cancel_order</c>).</summary>
    public required string ToolName { get; init; }

    /// <summary>The action's description, as the model reads it; <see langword="null"/> when it has none.</summary>
    public string? Description { get; init; }

    /// <summary>Which kind of agent offers it: the coordinator, a specialist or a team member.</summary>
    public required MentorAgentKind AgentKind { get; init; }

    /// <summary>The specialist's name, or the team member's role; <see langword="null"/> for the coordinator.</summary>
    public string? AgentName { get; init; }

    /// <summary>The roles the action requires, any one of which is enough; empty when it requires none.</summary>
    public IReadOnlyList<string> RequiredRoles { get; init; } = [];

    /// <summary>
    /// Whether a call asks for a confirmation now: <c>[MentorAction(RequiresConfirmation = true)]</c> or the
    /// <c>RequiresApproval</c> predicate, with <c>RequireConfirmation</c> on. Read again at every call to
    /// <see cref="Interfaces.IMentorActionCatalog.GetActions"/>, as the predicate is at every call of the action.
    /// </summary>
    public bool RequiresConfirmation { get; init; }

    /// <summary>
    /// Whether the application declares that the action only reads (<c>[MentorAction(ReadOnly = true)]</c>,
    /// <c>IsReadOnlyAction</c>) and nothing declares it confirmation-gated.
    /// </summary>
    public bool ReadOnly { get; init; }

    /// <summary>Whether, and to whom, MentorAgent's MCP server publishes it now.</summary>
    public required MentorMcpExposure McpExposure { get; init; }

    /// <summary>
    /// Whether the A2A agent card lists it as a skill. The card is built once, when the host maps the A2A endpoint;
    /// <see langword="false"/> when the host does not serve A2A.
    /// </summary>
    public bool OnAgentCard { get; init; }
}
