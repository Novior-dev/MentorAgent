using Novior.MentorAgent.Core;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace Novior.MentorAgent.Declarative;

/// <summary>
/// Builds Level-2 specialists from YAML definitions (roadmap #23), using the Agent Framework's
/// <see cref="ChatClientPromptAgentFactory"/>.
/// <para>
/// The factory takes the tool list MentorAgent supplies, so a name in the YAML resolves to a tool
/// the application already registered — <b>already wrapped in the gate</b>. A definition file can
/// therefore choose from what exists and nothing more: it cannot define a new capability, and it
/// cannot reach an existing one without passing the same role check and approval prompt a C#
/// specialist does.
/// </para>
/// </summary>
internal sealed class DeclarativeAgentSource : IMentorAgentSource
{
    private readonly MentorDeclarativeOptions _options;
    private readonly IConfiguration? _configuration;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<DeclarativeAgentSource> _logger;

    public DeclarativeAgentSource(
        MentorDeclarativeOptions options,
        ILoggerFactory loggerFactory,
        IConfiguration? configuration = null)
    {
        _options = options;
        _configuration = configuration;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<DeclarativeAgentSource>();
    }

    public async Task<IReadOnlyList<AIAgent>> GetAgentsAsync(
        MentorAgentSourceContext context, CancellationToken ct = default)
    {
        var definitions = LoadDefinitions();
        if (definitions.Count == 0)
        {
            _logger.LogInformation("[MentorAgent:Declarative] No agent definition found.");
            return [];
        }

        // The factory wants a mutable IList and holds on to it; hand it a copy so nothing it does
        // can reach back into the orchestrator's tool list.
        var tools = context.Tools.ToList();

        ChatClientPromptAgentFactory factory;
        try
        {
            factory = new ChatClientPromptAgentFactory(
                context.ChatClient,
                tools,
                configuration: ResolveConfiguration(),
                loggerFactory: _loggerFactory);
        }
        catch (ArgumentException ex)
        {
            // The factory loads the supplied configuration into the Power Fx engine as variables,
            // in its constructor, so a key that is not a valid Power Fx identifier throws here —
            // before any definition is even read, and with a message that says nothing about
            // configuration. Naming the real cause saves a long hunt.
            _logger.LogError(ex,
                "[MentorAgent:Declarative] The agent factory rejected the configuration exposed to " +
                "YAML ({Section}). Every key in it becomes a Power Fx variable and must be a valid " +
                "identifier. Narrow MentorDeclarativeOptions.ConfigurationSection, or leave it null.",
                _options.ConfigurationSection ?? "(none)");
            return [];
        }

        var agents = new List<AIAgent>();

        foreach (var (name, yaml) in definitions)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var agent = await factory.CreateFromYamlAsync(yaml, ct);
                if (agent is null)
                {
                    // The factory returns null for a definition kind it does not handle — a
                    // workflow, say, in a file that was meant to hold an agent.
                    _logger.LogWarning(
                        "[MentorAgent:Declarative] '{Name}' is not a supported agent definition — skipped.", name);
                    continue;
                }

                if (!WithoutHostedTools(agent, name))
                    continue;

                agents.Add(agent);
                _logger.LogInformation(
                    "[MentorAgent:Declarative] Agent '{Agent}' loaded from {Name} ({N} tool(s) available to it).",
                    agent.Name ?? "(unnamed)", name, tools.Count);
            }
            catch (Exception ex)
            {
                // A broken YAML file is a deployment mistake, not a reason for the assistant to be
                // unavailable. It is logged loudly and the rest still load.
                _logger.LogError(ex, "[MentorAgent:Declarative] '{Name}' could not be loaded — skipped.", name);
            }
        }

        return agents;
    }

    /// <summary>
    /// Removes every tool the definition created itself — <c>webSearch</c>, <c>codeInterpreter</c>,
    /// <c>fileSearch</c>, <c>mcp</c> — and keeps only the host's functions. Returns false (skip the
    /// agent) if they cannot be removed.
    ///
    /// <para>
    /// Found by the rc.11 README audit, which read the Agent Framework's factory: only <c>function</c>
    /// entries resolve against the list MentorAgent supplies. The other four kinds become
    /// provider-hosted tools configured by the file alone — an <c>mcp</c> entry names its own server
    /// and approval mode — outside the gate and outside the host's hosted-tool options. That broke the
    /// one promise this package makes: a definition file chooses from what the application already
    /// has and cannot add a capability. The AF exposes the agent's live <see cref="ChatOptions"/>
    /// through <c>GetService</c>, so the extra tools are taken out of the very list the agent runs with.
    /// </para>
    /// </summary>
    private bool WithoutHostedTools(AIAgent agent, string source)
    {
        if (agent.GetService(typeof(ChatOptions)) is not ChatOptions { Tools: { } list })
            return true;   // no tools at all

        // Functions are the host's: they came from the list MentorAgent supplied (a name that matches
        // nothing is left out by the factory itself). Anything else was defined by the file.
        var foreign = list.Where(t => t is not AIFunctionDeclaration).ToList();
        if (foreign.Count == 0)
            return true;

        var names = string.Join(", ", foreign.Select(t => t.Name));
        if (list.IsReadOnly)
        {
            _logger.LogError(
                "[MentorAgent:Declarative] '{Name}' declares tool(s) a definition file may not add ({Tools}), and they " +
                "could not be removed — the agent is not loaded. Only `kind: function` entries are allowed.", source, names);
            return false;
        }

        foreach (var tool in foreign)
            list.Remove(tool);

        _logger.LogWarning(
            "[MentorAgent:Declarative] '{Name}': removed {Tools}. A definition file can only use the application's own " +
            "tools (`kind: function`); web search, code interpreter, file search and MCP servers are configured by the " +
            "host, through MentorOptions.HostedTools and McpServers.", source, names);
        return true;
    }

    /// <summary>
    /// The configuration slice the YAML may reference — nothing by default. See
    /// <see cref="MentorDeclarativeOptions.ConfigurationSection"/> for why this is not simply the
    /// whole <see cref="IConfiguration"/>.
    /// </summary>
    private IConfiguration? ResolveConfiguration()
        => string.IsNullOrWhiteSpace(_options.ConfigurationSection) || _configuration is null
            ? null
            : _configuration.GetSection(_options.ConfigurationSection);

    /// <summary>Reads every definition as (source name, YAML). Inline ones first.</summary>
    private List<(string Name, string Yaml)> LoadDefinitions()
    {
        var result = new List<(string, string)>();

        for (var i = 0; i < _options.Definitions.Count; i++)
            if (!string.IsNullOrWhiteSpace(_options.Definitions[i]))
                result.Add(($"inline[{i}]", _options.Definitions[i]));

        if (string.IsNullOrWhiteSpace(_options.Directory)) return result;

        var dir = Path.IsPathRooted(_options.Directory)
            ? _options.Directory
            : Path.Combine(AppContext.BaseDirectory, _options.Directory);

        if (!System.IO.Directory.Exists(dir))
        {
            // Worth a warning rather than silence: a configured directory that is not there is
            // almost always a file that was not copied to the output folder.
            _logger.LogWarning("[MentorAgent:Declarative] Directory '{Dir}' does not exist.", dir);
            return result;
        }

        var option = _options.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        foreach (var path in System.IO.Directory.EnumerateFiles(dir, _options.SearchPattern, option).OrderBy(p => p))
        {
            try
            {
                result.Add((Path.GetFileName(path), File.ReadAllText(path)));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MentorAgent:Declarative] Could not read '{Path}'.", path);
            }
        }

        return result;
    }
}
