namespace Novior.MentorAgent.Declarative;

/// <summary>
/// Where <c>AddMentorAgentDeclarative()</c> looks for YAML agent definitions.
/// </summary>
public sealed class MentorDeclarativeOptions
{
    /// <summary>
    /// Directory to scan, relative to the application's base directory or absolute.
    /// <para>
    /// <b>This is a deploy-time path.</b> A definition file chooses the model, writes the system
    /// instructions and names the tools the agent may call — it is code, not configuration. Point
    /// this at a folder that ships with the application; never at an upload folder, a user profile
    /// directory, or anything assembled from request input.
    /// </para>
    /// </summary>
    public string? Directory { get; set; }

    /// <summary>File pattern inside <see cref="Directory"/>. Default: <c>*.agent.yaml</c>.</summary>
    public string SearchPattern { get; set; } = "*.agent.yaml";

    /// <summary>Scan subdirectories as well. Default: <see langword="false"/>.</summary>
    public bool Recursive { get; set; }

    /// <summary>
    /// YAML definitions supplied directly, for agents that come from a database, a configuration
    /// service or a test. Loaded in addition to <see cref="Directory"/>.
    /// </summary>
    public IList<string> Definitions { get; } = [];

    /// <summary>
    /// Name of the configuration section whose values the YAML may reference. Default:
    /// <see langword="null"/> — no configuration is exposed.
    /// <para>
    /// <b>Do not be tempted to widen this.</b> The Agent Framework's factory loads the
    /// configuration you give it into the Power Fx engine <i>as variables</i>, one per key, in its
    /// constructor. Every key therefore has to be a valid Power Fx identifier, and a single one that
    /// is not takes the whole factory down before a single definition is read — this was found the
    /// hard way, with a key named <c>${name}</c> that came from an unrelated configuration provider
    /// and produced <c>ArgumentException: Invalid name: ${name}</c>. Naming one section bounds the
    /// blast radius to keys you control.
    /// </para>
    /// <example>
    /// <code>
    /// o.ConfigurationSection = "AgentSettings";   // only AgentSettings:* reaches the YAML
    /// </code>
    /// </example>
    /// </summary>
    public string? ConfigurationSection { get; set; }
}
