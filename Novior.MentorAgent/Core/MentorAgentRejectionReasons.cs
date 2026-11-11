namespace Novior.MentorAgent.Core;

/// <summary>
/// The reason codes of <see cref="MentorAgentRejection.ReasonCode"/>. They stay the same from one version to the next;
/// a new one comes only in a minor version, and the CHANGELOG says so.
/// </summary>
public static class MentorAgentRejectionReasons
{
    /// <summary>
    /// The agent factory refused the definition: the YAML does not parse, does not describe a valid agent, or describes
    /// something else — a workflow, say, in a file meant to hold an agent.
    /// </summary>
    public const string InvalidDefinition = "invalid-definition";

    /// <summary>
    /// The definition gave no agent, and no error to say why. The Agent Framework's factory refuses a definition of
    /// another kind (<see cref="InvalidDefinition"/>); this is for a factory or a source that answers with nothing.
    /// </summary>
    public const string NotAnAgent = "not-an-agent";

    /// <summary>
    /// The definition adds tools a definition may not add (<c>webSearch</c>, <c>codeInterpreter</c>, <c>fileSearch</c>,
    /// <c>mcp</c>), or names a function the session does not have, and they could not be taken away: the agent is not
    /// loaded.
    /// </summary>
    public const string ForeignTools = "foreign-tools";

    /// <summary>
    /// The configuration exposed to the definitions is not valid for the expressions they are evaluated with. Every
    /// definition of the source is left out.
    /// </summary>
    public const string InvalidConfiguration = "invalid-configuration";

    /// <summary>
    /// The whole source threw. Added by MentorAgent, with the source's type as the name and the exception's type, never
    /// its message, as the detail; the other sources still load.
    /// </summary>
    public const string SourceFailed = "source-failed";

    /// <summary>
    /// With <see cref="MentorAgentRejection.Tool"/>: a <c>kind: function</c> entry names no action this session has —
    /// unknown to the application, or off for this session. The agent loads without it.
    /// </summary>
    public const string ToolNotFound = "tool-not-found";

    /// <summary>
    /// With <see cref="MentorAgentRejection.Tool"/>: a tool the definition added itself — web search, code interpreter,
    /// file search, an MCP server — was taken away. The agent loads with the application's own tools.
    /// </summary>
    public const string HostedToolRemoved = "hosted-tool-removed";
}
