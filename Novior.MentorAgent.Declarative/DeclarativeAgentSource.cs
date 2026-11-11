using Novior.MentorAgent.Core;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.PowerFx;
using Microsoft.PowerFx.Types;

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
        => (await LoadAsync(context, ct)).Agents;

    /// <summary>
    /// Story 1274 — every definition that does not load, and every tool taken away from an agent that does, is in the
    /// result as well as in the log: whoever governs the agents sees it without reading the server's log.
    /// </summary>
    public async Task<MentorAgentSourceResult> LoadAsync(
        MentorAgentSourceContext context, CancellationToken ct = default)
    {
        var definitions = LoadDefinitions();
        if (definitions.Count == 0)
        {
            _logger.LogInformation("[MentorAgent:Declarative] No agent definition found.");
            return new([], []);
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
                engine: EngineWithConfiguration(),
                loggerFactory: _loggerFactory);
        }
        catch (ArgumentException ex)
        {
            // The configuration is loaded into the Power Fx engine as variables before the factory
            // is built, so a key that is not a valid Power Fx identifier throws here — before any
            // definition is even read, and with a message that says nothing about configuration.
            // Naming the real cause saves a long hunt.
            _logger.LogError(ex,
                "[MentorAgent:Declarative] The agent factory rejected the configuration exposed to " +
                "YAML ({Section}). Every key in it becomes a Power Fx variable and must be a valid " +
                "identifier. Narrow MentorDeclarativeOptions.ConfigurationSection, or leave it null.",
                _options.ConfigurationSection ?? "(none)");
            var detail = $"Every key of the configuration section '{_options.ConfigurationSection}' becomes a Power Fx " +
                         "variable and must be a valid identifier.";
            return new([], [.. definitions.Select(d =>
                new MentorAgentRejection(d.Name, MentorAgentRejectionReasons.InvalidConfiguration, detail))]);
        }

        var agents = new List<AIAgent>();
        var rejected = new List<MentorAgentRejection>();

        foreach (var (name, yaml) in definitions)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var agent = await factory.CreateFromYamlAsync(yaml, ct);
                if (agent is null)
                {
                    // A factory may answer null for a definition kind it does not handle. The Agent
                    // Framework's throws instead from 1.27 ("Unsupported root element"), which is
                    // reported as invalid-definition; this stays as the guard for a null.
                    _logger.LogWarning(
                        "[MentorAgent:Declarative] '{Name}' is not a supported agent definition — skipped.", name);
                    rejected.Add(new(name, MentorAgentRejectionReasons.NotAnAgent, null));
                    continue;
                }

                if (!WithTheSessionsToolsOnly(agent, name, rejected))
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
                // The factory's message describes the text, and is what it takes to correct it.
                rejected.Add(new(name, MentorAgentRejectionReasons.InvalidDefinition, ex.Message));
            }
        }

        return new(agents, rejected);
    }

    /// <summary>
    /// Removes every tool the definition created itself — <c>webSearch</c>, <c>codeInterpreter</c>,
    /// <c>fileSearch</c>, <c>mcp</c>, and a function no action of the session matches — and keeps only
    /// the host's functions, adding each one removed to <paramref name="rejected"/>. Returns false (skip
    /// the agent, rejected as a whole) if they cannot be removed.
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
    ///
    /// <para>
    /// Story 1274, from the bridge's probe: for a <c>kind: function</c> name that matches none of the tools
    /// MentorAgent supplies, the factory creates a declaration-only function in its place. The model could
    /// call it and nothing would run: the agent stopped, and the coordinator read NOT DELEGATED with no log
    /// to say why. The tools supplied are the session's, so this is also how an action that is off for the
    /// session reaches no model through a definition that names it (<c>enabled.off-by-default</c>).
    /// </para>
    /// </summary>
    private bool WithTheSessionsToolsOnly(AIAgent agent, string source, List<MentorAgentRejection> rejected)
    {
        if (agent.GetService(typeof(ChatOptions)) is not ChatOptions { Tools: { } list })
            return true;   // no tools at all

        // The functions MentorAgent supplied stay, and they are the only ones with code behind them. Anything else
        // that is not a function was defined by the file; a function with no code is a name nothing matched.
        var foreign = list.Where(t => t is not AIFunctionDeclaration).ToList();
        var unbound = list.Where(t => t is AIFunctionDeclaration and not AIFunction).ToList();
        if (foreign.Count == 0 && unbound.Count == 0)
            return true;

        if (list.IsReadOnly)
        {
            var all = string.Join(", ", foreign.Concat(unbound).Select(t => t.Name));
            _logger.LogError(
                "[MentorAgent:Declarative] '{Name}' declares tool(s) a definition file may not add ({Tools}), and they " +
                "could not be removed — the agent is not loaded. Only `kind: function` entries naming one of the " +
                "application's actions are allowed.", source, all);
            rejected.Add(new(source, MentorAgentRejectionReasons.ForeignTools, all));
            return false;
        }

        foreach (var tool in foreign.Concat(unbound))
            list.Remove(tool);

        if (foreign.Count > 0)
        {
            _logger.LogWarning(
                "[MentorAgent:Declarative] '{Name}': removed {Tools}. A definition file can only use the application's own " +
                "tools (`kind: function`); web search, code interpreter, file search and MCP servers are configured by the " +
                "host, through MentorOptions.HostedTools and McpServers.", source, string.Join(", ", foreign.Select(t => t.Name)));
            rejected.AddRange(foreign.Select(t => new MentorAgentRejection(source, MentorAgentRejectionReasons.HostedToolRemoved,
                "A definition file can only use the application's own tools; hosted tools are configured by the host.")
                { Tool = t.Name }));
        }

        if (unbound.Count > 0)
        {
            _logger.LogWarning(
                "[MentorAgent:Declarative] '{Name}': removed {Tools}. No action of this application has that name in this " +
                "session — unknown, or off for this session — so nothing would run behind it. A `kind: function` entry " +
                "must name one of the application's actions exactly.", source, string.Join(", ", unbound.Select(t => t.Name)));
            rejected.AddRange(unbound.Select(t => new MentorAgentRejection(source, MentorAgentRejectionReasons.ToolNotFound,
                "No action of this application has this name in this session: it is unknown, or off for this session.")
                { Tool = t.Name }));
        }

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

    /// <summary>
    /// The Power Fx engine the definitions are evaluated in: one variable per key of the named section,
    /// named by its full configuration path, as the README describes. Null, so the factory builds its
    /// own, when no section is named. From Agent Framework 1.23 the factory loads only the keys a
    /// definition names through <c>Env</c> and the host lists as allowed, so the variables are loaded
    /// here, as the factory loaded them up to 1.22. The limits are the factory's own: Power Fx V1 and
    /// expressions of at most 10,000 characters.
    /// </summary>
    private RecalcEngine? EngineWithConfiguration()
    {
        if (ResolveConfiguration() is not { } section) return null;

        var engine = new RecalcEngine(new PowerFxConfig(Features.PowerFxV1) { MaximumExpressionLength = 10_000 });
        foreach (var (key, value) in section.AsEnumerable())
            engine.UpdateVariable(key, FormulaValue.New(value ?? string.Empty));
        return engine;
    }

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
