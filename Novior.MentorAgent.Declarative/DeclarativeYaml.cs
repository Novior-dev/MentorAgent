using System.Runtime.ExceptionServices;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Novior.MentorAgent.Declarative;

/// <summary>
/// BUG-243 — an agent's definition in a form the Agent Framework's reader takes. That reader, behind
/// <c>ChatClientPromptAgentFactory.CreateFromYamlAsync</c>, refuses some valid YAML:
/// <list type="bullet">
///   <item>a block sequence at its key's indentation (<c>tools:</c>, then <c>- kind: function</c>), the form YamlDotNet
///   writes by default;</item>
///   <item>an explicit end of the document, <c>...</c>;</item>
///   <item>a folded scalar, <c>&gt;</c>.</item>
/// </list>
/// Its error names the end of the text, not the construct. It also reads the <c>kind</c> of a <c>tools</c> entry only as
/// the entry's first key, and with a list at its key's indentation it drops an entry without <c>kind</c> and says nothing:
/// the agent loads without the tool (BUG-244).
/// <para>
/// A definition is given to the factory as it is written first. Only when the factory refuses it, or makes fewer tools
/// than the definition's <c>tools</c> lists, and YamlDotNet reads it, is it given again rewritten: sequences indented, no
/// end marker, folded scalars written literal with the same value, <c>kind</c> the first key of every mapping. A
/// definition the factory takes whole is never rewritten. One that fails both ways fails with the error of the text as
/// written, whose lines are the file's; one whose tools the factory dropped both ways fails with an error that says so.
/// </para>
/// </summary>
internal static class DeclarativeYaml
{
    /// <summary>
    /// Creates the agent of <paramref name="yaml"/> with <paramref name="create"/>, rewritten if it must be.
    /// <paramref name="toolsMade"/> counts the tools the factory gave an agent; none, the agent is not checked.
    /// </summary>
    internal static async Task<T> CreateAsync<T>(string yaml, Func<string, CancellationToken, Task<T>> create,
        CancellationToken ct, Func<T, int>? toolsMade = null)
    {
        var listed = toolsMade is null ? null : ToolsListed(yaml);
        var made = 0;
        bool Whole(T agent)
        {
            if (agent is null || listed is null) return true;
            made = toolsMade!(agent);
            return made >= listed.Count;
        }

        ExceptionDispatchInfo? first = null;
        try
        {
            var agent = await create(yaml, ct).ConfigureAwait(false);
            if (Whole(agent)) return agent;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            first = ExceptionDispatchInfo.Capture(ex);
        }

        if (Rewrite(yaml) is { } rewritten)
        {
            try
            {
                var agent = await create(rewritten, ct).ConfigureAwait(false);
                if (Whole(agent)) return agent;
            }
            catch (Exception) when (!ct.IsCancellationRequested && first is null)
            {
                // The text as written loaded without some of its tools: that is what is said, below.
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                first!.Throw();
            }
        }

        first?.Throw();
        // Only an agent with fewer tools than the definition lists comes this far, and only a definition whose tools were
        // read can have one (Whole): the list is there. The entries without kind are the ones the factory drops: named,
        // so that they are found at once.
        var entries = listed ?? [];
        var named = string.Join("; ", entries.Where(e => !e.HasKind)
            .Select(e => $"entry {e.Number} of `tools`{(e.Name is null ? "" : $" ({e.Name})")} has no `kind`"));
        throw new InvalidOperationException(
            $"The Agent Framework's factory made {made} of the {entries.Count} tools the definition lists, and said nothing " +
            $"of the others{(named.Length == 0 ? "" : ": " + named)}. Each entry of `tools` needs `kind: function` and the " +
            "`name` of one of the application's actions.");
    }

    /// <summary>One entry of a definition's <c>tools</c>: its number from 1, its <c>name</c>, and whether it has a <c>kind</c>.</summary>
    internal sealed record ToolEntry(int Number, string? Name, bool HasKind);

    /// <summary>The entries of the definition's <c>tools</c>; null when YamlDotNet cannot read it as one document.</summary>
    internal static IReadOnlyList<ToolEntry>? ToolsListed(string yaml)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlException)
        {
            return null;
        }
        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root) return null;
        if (!root.Children.TryGetValue(new YamlScalarNode("tools"), out var tools) || tools is not YamlSequenceNode list)
            return [];
        return [.. list.Children.Select((entry, i) => entry is YamlMappingNode map
            ? new ToolEntry(i + 1, (map.Children.TryGetValue(new YamlScalarNode("name"), out var name) ? name as YamlScalarNode : null)?.Value,
                map.Children.ContainsKey(new YamlScalarNode("kind")))
            : new ToolEntry(i + 1, (entry as YamlScalarNode)?.Value, false))];
    }

    /// <summary>
    /// The definition rewritten in the form the factory's reader takes; null when YamlDotNet cannot read it, when it
    /// holds other than one document, or when the rewriting changes nothing.
    /// </summary>
    internal static string? Rewrite(string yaml)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlException)
        {
            return null;
        }
        if (stream.Documents.Count != 1) return null;

        foreach (var scalar in stream.Documents[0].AllNodes.OfType<YamlScalarNode>())
            if (scalar.Style == ScalarStyle.Folded)
                scalar.Style = ScalarStyle.Literal;   // the same value: the folding was done when it was read

        // A mapping has no order: kind first, where the factory's reader looks for it (BUG-244).
        foreach (var map in stream.Documents[0].AllNodes.OfType<YamlMappingNode>().ToList())
        {
            var kind = new YamlScalarNode("kind");
            if (!map.Children.TryGetValue(kind, out var value) || map.Children.First().Key.Equals(kind)) continue;
            var rest = map.Children.Where(c => !c.Key.Equals(kind)).ToList();
            map.Children.Clear();
            map.Children.Add(kind, value);
            foreach (var (k, v) in rest) map.Children.Add(k, v);
        }

        var writer = new StringWriter();
        stream.Save(new Emitter(writer, new EmitterSettings().WithIndentedSequences()), assignAnchors: false);
        var text = writer.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);   // the emitter ends its lines its own way

        // One document, so the end marker YamlStream writes is the last line.
        if (text.EndsWith("...\n", StringComparison.Ordinal)) text = text[..^4];
        return text == yaml ? null : text;
    }
}
