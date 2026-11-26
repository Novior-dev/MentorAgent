namespace Novior.MentorAgent.Abstractions.Models;

/// <summary>
/// Who provides the tool a call or an offer is about: <see cref="MentorActionRequest.Provenance"/> and
/// <see cref="MentorActionDecision.Provenance"/>, with <see cref="MentorActionRequest.ProvenanceName"/> naming the
/// provider when there is one. A policy can be stricter with what the application does not provide itself, for example
/// with the tools of an MCP server. Later versions may add members: a policy treats a value it does not know as
/// <see cref="Unknown"/>, that is as not trusted.
/// </summary>
public enum MentorActionProvenance
{
    /// <summary>
    /// MentorAgent could not say who provides the tool. Every request and decision MentorAgent makes says it; this value
    /// is the default of a request or a decision built by hand. Treat it as not trusted.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// The application: one of its actions (a method, an endpoint, a command or a query), whichever agent it is given
    /// to, and on MentorAgent's MCP server; a team; a UI action, a form or a recipe of the page.
    /// </summary>
    Application = 1,

    /// <summary>
    /// An MCP server the application consumes (<c>MentorOptions.McpServers</c>): the provenance name is the server's
    /// <c>Name</c>.
    /// </summary>
    McpServer = 2,

    /// <summary>
    /// MentorAgent itself: <c>navigate_to</c>, <c>invoke_ui_action</c>, <c>remember</c>, <c>forget_all</c>,
    /// <c>load_skill</c>, <c>read_skill_resource</c> and <c>route_to_specialist</c>, which also reaches the remote and
    /// the external agents. The action policies are not asked about these; the observers receive their decisions.
    /// </summary>
    Library = 3,
}
