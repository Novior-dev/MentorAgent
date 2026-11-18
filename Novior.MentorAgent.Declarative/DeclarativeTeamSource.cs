using System.Globalization;
using Novior.MentorAgent.Core;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Novior.MentorAgent.Declarative;

/// <summary>
/// Story 1570, slice B — the teams written in YAML: every <c>*.team.yaml</c> file of
/// <see cref="MentorDeclarativeOptions.Directory"/>, and the <see cref="MentorDeclarativeOptions.TeamDefinitions"/> given
/// inline, read into <see cref="MentorTeamDefinition"/>s for the coordinator (rule <c>team.from-yaml</c>).
///
/// <para>
/// The file is read with YamlDotNet's node model, never its reflection deserializer: each key is matched by name, its
/// value checked for its shape, and a file that does not hold is left out with the line and column of the fault. An
/// unknown key is a fault too, so a misspelt <c>membres</c> never becomes a team without members in silence. What the
/// definition means — its members, its approver, its tools, its name against the code's — the core checks, as for any
/// <see cref="IMentorTeamSource"/>: this class only reads.
/// </para>
/// </summary>
internal sealed class DeclarativeTeamSource : IMentorTeamSource
{
    private static readonly string[] TeamKeys =
        ["kind", "name", "description", "triggerOn", "maxIterations", "handoffTo", "approver", "members"];
    private static readonly string[] MemberKeys = ["role", "instructions", "tools"];

    private readonly MentorDeclarativeOptions _options;
    private readonly ILogger<DeclarativeTeamSource> _logger;

    public DeclarativeTeamSource(MentorDeclarativeOptions options, ILoggerFactory loggerFactory)
    {
        _options = options;
        _logger = loggerFactory.CreateLogger<DeclarativeTeamSource>();
    }

    public Task<MentorTeamSourceResult> LoadAsync(MentorTeamSourceContext context, CancellationToken ct = default)
    {
        var teams = new List<MentorTeamDefinition>();
        var rejected = new List<MentorAgentRejection>();

        foreach (var (name, yaml) in DeclarativeFiles.Read(
                     _options, _options.TeamSearchPattern, _options.TeamDefinitions, _logger, warnWhenMissing: false))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                teams.Add(Read(yaml));
                _logger.LogInformation("[MentorAgent:Declarative] Team '{Team}' read from {Name}.", teams[^1].Name, name);
            }
            catch (TeamFileException ex)
            {
                _logger.LogWarning("[MentorAgent:Declarative] '{Name}' is not a team definition — skipped: {Fault}", name, ex.Message);
                rejected.Add(new(name, MentorAgentRejectionReasons.InvalidDefinition, ex.Message));
            }
        }

        return Task.FromResult(new MentorTeamSourceResult(teams, rejected));
    }

    /// <summary>One team from one YAML document; <see cref="TeamFileException"/> with the line and column of the fault.</summary>
    internal static MentorTeamDefinition Read(string yaml)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlException ex)
        {
            // The parser's message names the fault; its mark says where. A duplicate key is one of them.
            throw new TeamFileException(ex.Start, ex.Message);
        }

        if (stream.Documents.Count == 0) throw new TeamFileException(null, "the file has no team");
        if (stream.Documents.Count > 1)
            throw new TeamFileException(stream.Documents[1].RootNode.Start, "one team per file: a second document starts here");
        if (stream.Documents[0].RootNode is not YamlMappingNode root)
            throw new TeamFileException(stream.Documents[0].RootNode.Start, "a team is a mapping of keys, such as name: and members:");

        var keys = Keys(root, TeamKeys, "team");
        if (keys.TryGetValue("kind", out var kind) && !string.Equals(Text(kind, "kind"), "Team", StringComparison.OrdinalIgnoreCase))
            throw new TeamFileException(kind.Start, "kind is Team, or left out, in a team file");

        var members = keys.TryGetValue("members", out var m) ? Sequence(m, "members") : null;
        return new MentorTeamDefinition
        {
            // The core checks what is missing or empty, and says it with its reason: here, only the shape.
            Name = keys.TryGetValue("name", out var name) ? Text(name, "name") : "",
            Description = keys.TryGetValue("description", out var description) ? Text(description, "description") : "",
            TriggerOn = keys.TryGetValue("triggerOn", out var trigger) ? Texts(trigger, "triggerOn") : [],
            MaxIterations = keys.TryGetValue("maxIterations", out var max) ? Number(max, "maxIterations") : 10,
            HandoffTo = keys.TryGetValue("handoffTo", out var handoff) ? Texts(handoff, "handoffTo") : [],
            Approver = keys.TryGetValue("approver", out var approver) ? Text(approver, "approver") : null,
            Members = members is null ? [] : [.. members.Children.Select(Member)],
        };
    }

    private static MentorTeamMemberDefinition Member(YamlNode node)
    {
        if (node is not YamlMappingNode member)
            throw new TeamFileException(node.Start, "a member is a mapping of keys, such as role: and instructions:");
        var keys = Keys(member, MemberKeys, "member");
        return new MentorTeamMemberDefinition
        {
            Role = keys.TryGetValue("role", out var role) ? Text(role, "role") : "",
            Instructions = keys.TryGetValue("instructions", out var instructions) ? Text(instructions, "instructions") : "",
            Tools = keys.TryGetValue("tools", out var tools) ? Texts(tools, "tools") : [],
        };
    }

    /// <summary>The keys of <paramref name="node"/>, each one known; the parser has already refused a key written twice.</summary>
    private static Dictionary<string, YamlNode> Keys(YamlMappingNode node, string[] known, string what)
    {
        var keys = new Dictionary<string, YamlNode>(StringComparer.Ordinal);
        foreach (var (key, value) in node.Children)
        {
            var name = key is YamlScalarNode { Value: { } text } ? text : null;
            if (name is null || !known.Contains(name, StringComparer.Ordinal))
                throw new TeamFileException(key.Start,
                    $"unknown key '{name ?? key.ToString()}' in a {what}; the keys are {string.Join(", ", known)}");
            keys[name] = value;
        }
        return keys;
    }

    private static string Text(YamlNode node, string key)
        => node is YamlScalarNode scalar
            ? scalar.Value ?? ""
            : throw new TeamFileException(node.Start, $"{key} is a text, not a list or a mapping");

    private static int Number(YamlNode node, string key)
        => int.TryParse(Text(node, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n
            : throw new TeamFileException(node.Start, $"{key} is a whole number");

    private static YamlSequenceNode Sequence(YamlNode node, string key)
        => node as YamlSequenceNode ?? throw new TeamFileException(node.Start, $"{key} is a list");

    private static IReadOnlyList<string> Texts(YamlNode node, string key)
        => [.. Sequence(node, key).Children.Select(item => Text(item, key + " item"))];
}

/// <summary>A team file that does not hold: the message says where, by line and column, and what.</summary>
internal sealed class TeamFileException(Mark? at, string fault)
    : Exception(at is { } mark ? $"line {mark.Line}, column {mark.Column}: {fault}" : fault);
