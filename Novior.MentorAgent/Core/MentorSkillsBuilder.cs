using Novior.MentorAgent.Attributes;
using Novior.MentorAgent.Options;
using Microsoft.Extensions.Hosting;

namespace Novior.MentorAgent.Core;

/// <summary>
/// An Agent Skill discovered and loaded by <see cref="MentorSkillsBuilder"/>.
/// </summary>
internal sealed record SkillRegistration(
    string Name,
    string Description,
    string InstructionsMarkdown,
    IReadOnlyDictionary<string, string>? Resources = null,
    // Set only for a skill that came from a host-registered AgentSkillsSource. The framework's
    // AgentSkill exposes GetResourceAsync(name) and no way to enumerate what it holds, so such a
    // skill carries the instance and read_skill_resource resolves through it rather than through
    // the dictionary above. See MentorNativeSkillSources.
    Microsoft.Agents.AI.AgentSkill? Native = null
);

/// <summary>
/// Discovers and builds Agent Skills for the MentorAgent coordinator.
///
/// Skills implement progressive disclosure:
/// <list type="bullet">
///   <item>The skill catalogue (name + description, ~100 tokens/skill) is injected into
///   the system prompt via <see cref="SkillsContextProvider"/> before every LLM call.</item>
///   <item>Full instructions are loaded on demand when the AI calls the
///   <c>load_skill</c> tool.</item>
///   <item>Attached files are accessible via <c>read_skill_resource</c>.</item>
/// </list>
///
/// Sources (in order — class-based overrides file-based for the same name):
/// <list type="number">
///   <item>File-based: <c>{SkillsFolder}/{name}/SKILL.md</c> + any side-car files.</item>
///   <item>Class-based: classes decorated with <see cref="MentorSkillAttribute"/> in
///   <see cref="MentorOptions.ScanAssemblies"/>.</item>
/// </list>
/// </summary>
internal sealed class MentorSkillsBuilder
{
    private readonly MentorOptions _options;
    private readonly string _contentRoot;
    private readonly ILogger<MentorSkillsBuilder> _logger;
    private readonly ILoggerFactory _loggerFactory;

    public MentorSkillsBuilder(
        IOptions<MentorOptions> options,
        IHostEnvironment env,
        ILogger<MentorSkillsBuilder> logger,
        ILoggerFactory loggerFactory,
        // Optional so the builder can still be constructed by hand outside DI (several suites do,
        // and so may a host). Without a shared sentinel there is nothing to be quiet about.
        CoordinatorAnnouncement? announcement = null,
        // Optional for the same reason: without it each builder composes its own sources.
        MentorSharedSkillSource? sharedSource = null)
    {
        _sharedSource = sharedSource ?? new MentorSharedSkillSource();
        _options = options.Value;
        _contentRoot = ResolveContentRoot(env, logger);
        _logger = logger;
        _loggerFactory = loggerFactory;
        _announcement = announcement ?? new CoordinatorAnnouncement();
    }

    private readonly CoordinatorAnnouncement _announcement;
    private readonly MentorSharedSkillSource _sharedSource;

    /// <summary>
    /// The catalogue is the application's, not the visitor's: after the first coordinator has
    /// announced it, later circuits keep the same line at Debug (BUG-068).
    /// </summary>
    private ILogger BuildLog => QuietBuildLogger.Wrap(_logger, _announcement.Quiet);

    /// <summary>
    /// The content root, or the application directory when the host has not got one.
    ///
    /// <para>
    /// Every web host implements <see cref="IHostEnvironment.ContentRootPath"/>. <b>.NET MAUI does
    /// not</b> — <c>MauiHostEnvironment.ContentRootPath</c> throws
    /// <see cref="NotImplementedException"/>, because a packaged desktop application has no content
    /// directory to point at. Reading it in the constructor therefore threw while the DI container
    /// was building the widget, which killed the whole Blazor renderer and left the app showing
    /// "An unhandled error has occurred" — with skills switched <i>off</i>, since this type is
    /// constructed either way (BUG-054).
    /// </para>
    ///
    /// <para>
    /// <see cref="AppContext.BaseDirectory"/> is the right fallback rather than merely a safe one:
    /// it is where a MAUI app's <c>Content</c> and <c>CopyToOutputDirectory</c> files actually land,
    /// so a relative <c>SkillsFolder</c> resolves to the folder the developer put it in. The
    /// exception is swallowed deliberately and reported once at Debug — a host that has no content
    /// root has not misconfigured anything, and a warning on every startup is how people learn to
    /// ignore logs.
    /// </para>
    /// </summary>
    private static string ResolveContentRoot(IHostEnvironment env, ILogger logger)
    {
        try
        {
            var root = env.ContentRootPath;
            if (!string.IsNullOrWhiteSpace(root)) return root;
        }
        catch (NotImplementedException)
        {
            // MAUI, and any other host that does not model a content root.
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex,
                "[MentorAgent] The host's ContentRootPath could not be read; " +
                "falling back to the application directory.");
        }

        logger.LogDebug(
            "[MentorAgent] This host has no content root. Relative skill paths resolve against " +
            "the application directory ({Dir}) instead.", AppContext.BaseDirectory);

        return AppContext.BaseDirectory;
    }

    // ── Build ────────────────────────────────────────────────────────────────────

    public async Task<List<SkillRegistration>> BuildAsync(CancellationToken ct = default)
    {
        if (!_options.EnableSkills)
            return [];

        var skills = new Dictionary<string, SkillRegistration>(StringComparer.OrdinalIgnoreCase);

        DiscoverFileSkills(skills);
        DiscoverClassSkills(skills);   // class-based overrides file-based

        // Host-registered AgentSkillsSource(s), composed with the framework's own decorators.
        // Added last and only under a name nothing has claimed, so an application skill always
        // wins a collision — the composition's own de-duplication keeps the first occurrence for
        // the same reason. Nothing here runs when the host registered no sources.
        var composed = _sharedSource.Get(_options, _loggerFactory);
        if (composed is not null)
        {
            // AgentSkillsSourceContext rejects a null agent, and skills are discovered BEFORE the
            // coordinator exists — so there is no coordinator to hand over. Use the host's
            // pre-built agent when there is one, otherwise a plain agent over the configured chat
            // client, which is what the coordinator will itself be built on. With neither, the
            // sources are skipped with a warning rather than throwing out of coordinator
            // construction — the same degradation every other MentorOptions constraint uses.
            var contextAgent = _options.Agent
                ?? (_options.ChatClient is { } chat ? new ChatClientAgent(chat) : null);

            if (contextAgent is null)
            {
                _logger.LogWarning(
                    "[MentorAgent:Skills] {N} AgentSkillsSource(s) are registered but neither " +
                    "ChatClient nor Agent is set, and the framework requires an agent to query a " +
                    "source. Their skills are skipped; skills MentorAgent discovered itself are " +
                    "unaffected.", _options.SkillSources.Count);
                composed = null;
            }

            var context = contextAgent is null ? null : new AgentSkillsSourceContext(contextAgent, session: null);
            foreach (var native in composed is null || context is null
                         ? []
                         : await MentorNativeSkillSources
                             .LoadAsync(composed, context, BuildLog, ct).ConfigureAwait(false))
            {
                if (!skills.TryAdd(native.Name, native))
                    _logger.LogWarning(
                        "[MentorAgent:Skills] A registered source offered '{Name}', which an " +
                        "application skill already defines. The application's skill is kept.",
                        native.Name);
            }
        }

        var list = skills.Values.OrderBy(s => s.Name).ToList();
        BuildLog.LogInformation("[MentorAgent:Skills] {N} skill(s) registered: {Names}",
            list.Count, string.Join(", ", list.Select(s => s.Name)));

        return list;
    }

    // ── File-based discovery ────────────────────────────────────────────────────

    private void DiscoverFileSkills(Dictionary<string, SkillRegistration> skills)
    {
        var folderPath = _options.SkillsFolder;
        if (string.IsNullOrWhiteSpace(folderPath)) return;

        var fullPath = Path.IsPathRooted(folderPath)
            ? folderPath
            : Path.Combine(_contentRoot, folderPath);

        if (!Directory.Exists(fullPath))
        {
            _logger.LogDebug("[MentorAgent:Skills] Skills folder not found: {Path}", fullPath);
            return;
        }

        foreach (var skillDir in Directory.GetDirectories(fullPath))
        {
            var skillFile = Path.Combine(skillDir, "SKILL.md");
            if (!File.Exists(skillFile)) continue;

            var name = Path.GetFileName(skillDir);
            try
            {
                var content = File.ReadAllText(skillFile);
                var description = ExtractDescription(name, content);

                // Side-car resources — every non-SKILL.md file in the skill directory
                Dictionary<string, string>? resources = null;
                foreach (var resFile in Directory.GetFiles(skillDir))
                {
                    if (Path.GetFileName(resFile).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
                        continue;
                    resources ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    try { resources[Path.GetFileName(resFile)] = File.ReadAllText(resFile); }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[MentorAgent:Skills] Cannot read resource: {File}", resFile);
                    }
                }

                skills[name] = new SkillRegistration(name, description, content, resources);
                _logger.LogDebug("[MentorAgent:Skills] File skill loaded: {Name}", name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[MentorAgent:Skills] Failed to load skill from: {Dir}", skillDir);
            }
        }
    }

    // ── Class-based discovery ───────────────────────────────────────────────────

    private void DiscoverClassSkills(Dictionary<string, SkillRegistration> skills)
    {
        foreach (var asm in _options.ScanAssemblies)
        {
            IEnumerable<Type> types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t is not null)!; }

            foreach (var type in types)
            {
                // Skip non-concrete types — same guard used in MentorDiscoveryService.ScanAssembly.
                // An abstract class or interface decorated with [MentorSkill] would produce
                // a SkillRegistration with no usable type instance and mislead the coordinator.
                if (!type.IsClass || type.IsAbstract) continue;

                var attr = type.GetCustomAttribute<MentorSkillAttribute>(inherit: false);
                if (attr is null) continue;

                var instructions = ResolveInstructions(attr);
                skills[attr.Name] = new SkillRegistration(attr.Name, attr.Description, instructions);
                _logger.LogDebug("[MentorAgent:Skills] Class skill registered: {Name}", attr.Name);
            }
        }
    }

    private string ResolveInstructions(MentorSkillAttribute attr)
    {
        // 1. Inline instructions
        if (!string.IsNullOrWhiteSpace(attr.Instructions))
            return attr.Instructions!;

        // 2. Explicit file path
        if (!string.IsNullOrWhiteSpace(attr.InstructionsFile))
        {
            var path = Path.IsPathRooted(attr.InstructionsFile)
                ? attr.InstructionsFile!
                : Path.Combine(_contentRoot, attr.InstructionsFile!);

            if (File.Exists(path))
                return File.ReadAllText(path);

            _logger.LogWarning("[MentorAgent:Skills] Instructions file not found for '{Name}': {Path}",
                attr.Name, path);
            return $"# {attr.Name}\n\n{attr.Description}";
        }

        // 3. Auto-discovery fallback: Skills/{Name}/SKILL.md
        var folder = _options.SkillsFolder ?? "Skills";
        var autoPath = Path.IsPathRooted(folder)
            ? Path.Combine(folder, attr.Name, "SKILL.md")
            : Path.Combine(_contentRoot, folder, attr.Name, "SKILL.md");

        if (File.Exists(autoPath))
            return File.ReadAllText(autoPath);

        // 4. No file available — use description as fallback
        return $"# {attr.Name}\n\n{attr.Description}";
    }

    // ── Context provider factory ─────────────────────────────────────────────────

    public static SkillsContextProvider BuildContextProvider(IReadOnlyList<SkillRegistration> skills)
        => new(skills);

    // ── AI tools factory ─────────────────────────────────────────────────────────

    public static IReadOnlyList<AITool> BuildTools(IReadOnlyList<SkillRegistration> skills, ILogger logger)
    {
        if (skills.Count == 0)
            return [];

        var registry = skills.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        var tools = new List<AITool>(2);

        // -- load_skill -----------------------------------------------------------
        tools.Add(AIFunctionFactory.Create(
#pragma warning disable CS1998
            async (
                [System.ComponentModel.Description(
                    "Name of the skill to load (e.g. 'expense-report', 'shipping-policy')")]
                string skillName,
                CancellationToken ct = default) =>
            {
                if (!registry.TryGetValue(skillName, out var skill))
                {
                    var available = string.Join(", ", registry.Keys);
                    return MentorUtils.ToolNotFound("Skill", skillName, available);
                }
                logger.LogDebug("[MentorAgent:Skills] load_skill: {Name}", skillName);
                return skill.InstructionsMarkdown;
            },
#pragma warning restore CS1998
            name: "load_skill",
            description: "Loads full instructions and domain knowledge for a registered skill. " +
                         "Call this before handling requests that require specialised expertise " +
                         "(e.g. policy rules, validation logic, workflows)."));

        // -- read_skill_resource — only if at least one skill can serve one --------
        // A skill from a host-registered AgentSkillsSource counts: its resources cannot be
        // enumerated ahead of time (AgentSkill exposes only GetResourceAsync), so the tool has to
        // exist for the model to be able to ask at all.
        if (skills.Any(s => s.Resources?.Count > 0 || s.Native is not null))
        {
            tools.Add(AIFunctionFactory.Create(
#pragma warning disable CS1998
                async (
                    [System.ComponentModel.Description("Skill name (e.g. 'expense-report')")]
                    string skillName,
                    [System.ComponentModel.Description(
                        "Resource file name within the skill (e.g. 'policy.md', 'examples.txt')")]
                    string resourceName,
                    CancellationToken ct = default) =>
                {
                    if (!registry.TryGetValue(skillName, out var skill))
                        return MentorUtils.ToolNotFound("Skill", skillName, string.Join(", ", registry.Keys));

                    if (skill.Resources is null || !skill.Resources.TryGetValue(resourceName, out var content))
                    {
                        // A skill from a registered AgentSkillsSource resolves on demand — the
                        // framework gives no way to list its resources, so this is the only way to
                        // find out whether the name exists.
                        if (skill.Native is { } native)
                        {
                            try
                            {
                                var resource = await native.GetResourceAsync(resourceName, ct);
                                if (resource is not null)
                                {
                                    logger.LogDebug(
                                        "[MentorAgent:Skills] read_skill_resource (source): {Skill}/{Resource}",
                                        skillName, resourceName);
                                    return await resource.ReadAsync(null!, ct) ?? string.Empty;
                                }
                            }
                            catch (Exception ex)
                            {
                                logger.LogWarning(ex,
                                    "[MentorAgent:Skills] Resource '{Resource}' of skill '{Skill}' could not be read.",
                                    resourceName, skillName);
                            }

                            return MentorUtils.ToolNotFound(
                                "Resource", resourceName, "the skill's source did not provide it");
                        }

                        var available = skill.Resources?.Count > 0
                            ? string.Join(", ", skill.Resources.Keys)
                            : "none";
                        return MentorUtils.ToolNotFound("Resource", resourceName, available);
                    }

                    logger.LogDebug("[MentorAgent:Skills] read_skill_resource: {Skill}/{Resource}",
                        skillName, resourceName);
                    return content;
                },
#pragma warning restore CS1998
                name: "read_skill_resource",
                description: "Reads a resource file attached to a skill " +
                             "(e.g. policy documents, FAQ pages, templates, examples). " +
                             "Call load_skill first to discover what resources are available."));
        }

        return tools;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Extracts the first descriptive paragraph from a SKILL.md file.
    /// Returns the text of the first non-heading, non-empty line after a heading.
    /// Falls back to <paramref name="fallbackName"/> if none is found.
    /// </summary>
    private static string ExtractDescription(string fallbackName, string content)
    {
        var lines = content.Split('\n');
        bool foundHeading = false;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('#'))
            {
                foundHeading = true;
                continue;
            }
            if (foundHeading && !string.IsNullOrEmpty(trimmed))
                return trimmed;
        }
        return fallbackName;
    }
}

/// <summary>
/// <see cref="AIContextProvider"/> that injects the skills catalogue into the
/// coordinator's system prompt, enabling progressive disclosure of domain expertise.
/// </summary>
internal sealed class SkillsContextProvider : AIContextProvider
{
    private readonly IReadOnlyList<SkillRegistration> _skills;

    public SkillsContextProvider(IReadOnlyList<SkillRegistration> skills)
    {
        _skills = skills;
    }

    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        if (_skills.Count == 0)
            return ValueTask.FromResult(new AIContext());

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Available Skills");
        sb.AppendLine("When a request requires specialised expertise in any area below, " +
                      "call load_skill with the skill name to get full instructions before responding:");
        foreach (var skill in _skills)
            sb.AppendLine($"- **{skill.Name}**: {skill.Description}");

        return ValueTask.FromResult(new AIContext { Instructions = sb.ToString() });
    }
}
