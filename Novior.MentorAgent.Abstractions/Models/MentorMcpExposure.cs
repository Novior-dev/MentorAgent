namespace Novior.MentorAgent.Abstractions.Models;

/// <summary>Whether MentorAgent's MCP server publishes an action, by its rules, as <see cref="MentorActionInfo.McpExposure"/> says.</summary>
public enum MentorMcpExposure
{
    /// <summary>In every caller's <c>tools/list</c>.</summary>
    Published = 0,

    /// <summary>In the <c>tools/list</c> of the callers <c>McpCallerPrincipal</c> identifies as holding one of its roles.</summary>
    PublishedToRoles = 1,

    /// <summary>
    /// Never published: it asks for confirmation, the <c>RequiresApproval</c> predicate keeps it back, it
    /// requires roles and no <c>McpCallerPrincipal</c> is set, or the code ships it off
    /// (<see cref="MentorActionInfo.EnabledByDefault"/>), unless an <c>IMentorActionGovernor</c> turns it on.
    /// </summary>
    Withheld = 2,

    /// <summary>
    /// The MCP server does not serve it: the host does not map the server (<c>MapMentorAgentMcp</c>), the action
    /// belongs to a specialist or a team, or its class is not registered in the container.
    /// </summary>
    NotServed = 3,
}
