using System.Security.Claims;

namespace Novior.MentorAgent.Abstractions.Models;

/// <summary>
/// What an <see cref="Interfaces.IMentorActionPolicy"/> is asked about: one action, for one user, at one
/// <see cref="Stage"/>. <see cref="ToolName"/> means what it means on <see cref="MentorActionDecision"/>. More
/// properties may be added.
/// </summary>
public sealed record MentorActionRequest
{
    /// <summary>Whether MentorAgent is deciding what to offer, or a call is about to run.</summary>
    public required MentorActionStage Stage { get; init; }

    /// <summary>The tool's technical name, as the model or the MCP client calls it (for example <c>cancel_order</c>).</summary>
    public required string ToolName { get; init; }

    /// <summary>The action's description, as the model reads it; <see langword="null"/> when it has none.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// The arguments of the call, by parameter name, with the values the model or the MCP client sent — in general
    /// a <c>System.Text.Json.JsonElement</c> each. They come from the model and nothing has checked them: read them
    /// as you would read a request's body. <see langword="null"/> at <see cref="MentorActionStage.Offer"/>.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Arguments { get; init; }

    /// <summary>
    /// The signed-in user, or <see langword="null"/> when nobody is. Read for every action, those that require no
    /// role too. On the MCP server, the caller <c>McpCallerPrincipal</c> resolves, else the request's user.
    /// </summary>
    public ClaimsPrincipal? User { get; init; }

    /// <summary>Where the call comes from.</summary>
    public required MentorActionSurface Surface { get; init; }

    /// <summary>
    /// Which kind of agent asks for the action; <see langword="null"/> on the MCP server, where the caller names
    /// the tool itself.
    /// </summary>
    public MentorAgentKind? AgentKind { get; init; }

    /// <summary>The name of the agent that asks for the action, when it has one.</summary>
    public string? AgentName { get; init; }

    /// <summary>The session's opaque id, as on <see cref="MentorActionDecision.SessionId"/>.</summary>
    public string? SessionId { get; init; }

    /// <summary>The turn's opaque id, as on <see cref="MentorActionDecision.TurnId"/>; <see langword="null"/> on the MCP server.</summary>
    public string? TurnId { get; init; }

    /// <summary>The id of the call, as on <see cref="MentorActionDecision.CallId"/>; <see langword="null"/> at <see cref="MentorActionStage.Offer"/>.</summary>
    public string? CallId { get; init; }

    /// <summary>
    /// The page the user is on, as the page named it with <c>IMentorPageContext.SetPageName</c>;
    /// <see langword="null"/> when no page set one, on the MCP server and in a task from another agent.
    /// </summary>
    public string? PageName { get; init; }

    /// <summary>The roles the action requires, any one of which is enough; empty when it requires none.</summary>
    public IReadOnlyList<string> RequiredRoles { get; init; } = [];

    /// <summary>
    /// Whether the action asks for a confirmation of its own: <c>[MentorAction(RequiresConfirmation = true)]</c>, the
    /// <c>RequiresApproval</c> predicate, or an approval-required MCP server — with <c>RequireConfirmation</c> on.
    /// </summary>
    public bool RequiresConfirmation { get; init; }

    /// <summary>
    /// The services of the conversation's scope — the circuit, the hub connection, the request, the A2A task; on the
    /// MCP server, the request's — for a scoped service of the application. Never the application's root services.
    /// </summary>
    public required IServiceProvider Services { get; init; }
}
