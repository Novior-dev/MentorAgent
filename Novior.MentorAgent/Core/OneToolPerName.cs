namespace Novior.MentorAgent.Core;

/// <summary>
/// BUG-195 — one tool per name in the list an agent is built with. From Agent Framework 1.23 a request that
/// offers two different functions with the same name fails ("Duplicate tool name"), and so does every turn of
/// that agent. The first copy is kept: it is the one a call by that name already reached, since the
/// function-invoking client runs the first tool with the name. Each agent's list is handled on its own, so a
/// name a specialist shares with the coordinator stays with both.
/// </summary>
internal static class OneToolPerName
{
    /// <summary>
    /// <paramref name="tools"/> without the later functions that repeat a name, and a warning that names every
    /// copy and the one kept. Only functions are counted, as the framework counts them: a declaration or a
    /// provider-hosted tool is left where it is.
    /// </summary>
    internal static List<AITool> Keep(List<AITool> tools, string agent, ILogger logger)
    {
        var kept = new List<AITool>(tools.Count);
        var copies = new Dictionary<string, List<AIFunction>>(StringComparer.Ordinal);

        foreach (var tool in tools)
        {
            if (tool is not AIFunction function || string.IsNullOrEmpty(function.Name))
            {
                kept.Add(tool);
                continue;
            }

            if (copies.TryGetValue(function.Name, out var same))
            {
                // The same instance twice is one tool.
                if (!same.Any(f => ReferenceEquals(f, function)))
                    same.Add(function);
                continue;
            }

            copies[function.Name] = [function];
            kept.Add(tool);
        }

        var repeated = copies.Values.Where(c => c.Count > 1).ToList();
        if (repeated.Count > 0)
            logger.LogWarning(
                "[MentorAgent] {Agent} is offered {Count} tool name(s) by more than one tool: {Names}. The Agent " +
                "Framework takes one tool per name, so only the first is offered — the one a call by that name " +
                "already reached. Rename the methods or expose only one of them.",
                agent, repeated.Count,
                string.Join("; ", repeated.Select(c =>
                    $"{c[0].Name} (kept {Describe(c[0])}; left out {string.Join(", ", c.Skip(1).Select(Describe))})")));

        return kept;
    }

    private static string Describe(AIFunction function)
        => function.UnderlyingMethod is { } method
            ? $"{method.DeclaringType?.Name}.{method.Name}"
            : function.GetType().Name;
}
