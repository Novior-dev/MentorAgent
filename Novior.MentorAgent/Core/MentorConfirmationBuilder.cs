using Novior.MentorAgent.Options;

using System.Collections.Concurrent;

namespace Novior.MentorAgent.Core;

/// <summary>
/// Single source of truth for "does this tool need human approval, and what do we show the user?"
/// (roadmap #16).
///
/// <para>
/// Shared by <c>MentorAgentUIMiddleware</c> (the <see cref="MentorHitlMode.Blocking"/> flow),
/// <c>MentorOrchestrator</c> (the <see cref="MentorHitlMode.Native"/> Agent Framework flow) and
/// <c>McpToolsBuilder</c>, so a tool is gated by exactly the same rule and the banner reads exactly
/// the same regardless of mode or tool origin.
/// </para>
///
/// <para>
/// Approval is required when <b>any</b> of these holds:
/// <list type="bullet">
///   <item><c>[MentorAction(RequiresConfirmation = true)]</c> on an L1 action, an L2 agent method or an L3 team-member tool</item>
///   <item>the tool comes from an MCP server with <c>MentorMcpServer.RequiresConfirmation = true</c></item>
///   <item><c>MentorOptions.RequiresApproval</c> returns <see langword="true"/> for its name</item>
/// </list>
/// </para>
/// </summary>
internal sealed class MentorConfirmationBuilder
{
    private readonly MentorDiscoveryService _discovery;
    private readonly MentorOptions _options;

    // MCP tool names gated at connect time by McpToolsBuilder (external tools are not in discovery).
    private readonly ConcurrentDictionary<string, byte> _externalApproval = new(StringComparer.OrdinalIgnoreCase);

    private DiscoveryResult? _cache;

    public MentorConfirmationBuilder(MentorDiscoveryService discovery, IOptions<MentorOptions> options)
    {
        _discovery = discovery;
        _options   = options.Value;
    }

    private DiscoveryResult Discovery => _cache ??= _discovery.Discover();

    /// <summary>
    /// Registers a tool that lives outside discovery (today: MCP) as approval-required.
    /// Called while the coordinator is being built, before any tool can be invoked.
    /// </summary>
    public void RegisterApprovalRequired(string toolName)
    {
        if (!string.IsNullOrWhiteSpace(toolName))
            _externalApproval[toolName] = 0;
    }

    /// <summary>
    /// Returns <see langword="true"/> when the tool must be confirmed by the user before it runs.
    /// Always <see langword="false"/> when <see cref="MentorOptions.RequireConfirmation"/> is off, so
    /// callers can use this as the single gate.
    /// </summary>
    public bool RequiresApproval(string toolName)
        => _options.RequireConfirmation && IsDeclaredConfirmationGated(toolName);

    /// <summary>
    /// Whether the application DECLARES the tool as needing a person's confirmation —
    /// <c>RequiresConfirmation</c>, the <c>RequiresApproval</c> predicate, or an approval-required MCP
    /// server — regardless of <see cref="MentorOptions.RequireConfirmation"/>. That switch decides whether
    /// the banner is shown; the declaration is how the application marks an action that changes data,
    /// and tool filtering reads it so such an action never reaches the model alone (BUG-099).
    /// </summary>
    internal bool IsDeclaredConfirmationGated(string toolName)
    {
        if (string.IsNullOrEmpty(toolName)) return false;

        if (_externalApproval.ContainsKey(toolName)) return true;

        var l1 = Discovery.Level1Methods.FirstOrDefault(m => m.ToolName == toolName);
        if (l1 is not null && l1.RequiresConfirmation) return true;

        var l2 = Discovery.Level2Agents
            .SelectMany(a => a.Methods)
            .FirstOrDefault(m => m.ToolName == toolName);
        if (l2 is not null && l2.RequiresConfirmation) return true;

        // L3 team-member tools are gated too — GatedAIFunction reaches them now, and the role check
        // in MentorAgentUIMiddleware has always looked them up, so the two must agree.
        var l3 = Discovery.Level3Teams
            .SelectMany(t => t.Members)
            .SelectMany(m => m.Tools)
            .FirstOrDefault(m => m.ToolName == toolName);
        if (l3 is not null && l3.RequiresConfirmation) return true;

        return _options.RequiresApproval?.Invoke(toolName) == true;
    }

    /// <summary>
    /// Whether the application declares the tool as one that only reads: <c>[MentorAction(ReadOnly = true)]</c>
    /// or <see cref="MentorOptions.IsReadOnlyAction"/>. Never for a tool declared confirmation-gated —
    /// confirmation is the mark of an action that changes data, and it wins over a read-only claim.
    /// MCP tools are not decided here: their server's <c>readOnlyHint</c> is read by <c>McpToolsBuilder</c>.
    /// </summary>
    internal bool IsDeclaredReadOnly(string toolName)
    {
        if (string.IsNullOrEmpty(toolName) || IsDeclaredConfirmationGated(toolName)) return false;

        // Decided by name: when two actions share it, every copy must be declared.
        var copies = Discovery.Level1Methods.Where(m => m.ToolName == toolName).ToList();
        if (copies.Count > 0 && copies.All(m => m.ReadOnly)) return true;

        return _options.IsReadOnlyAction?.Invoke(toolName) == true;
    }

    /// <summary>
    /// Human-readable description of a tool, used in the confirmation banner and the action feedback
    /// bar instead of the raw technical name. Falls back to a title-cased tool name.
    /// </summary>
    public string GetToolDescription(string toolName)
    {
        var l1 = Discovery.Level1Methods.FirstOrDefault(m => m.ToolName == toolName);
        if (l1 is not null && !string.IsNullOrWhiteSpace(l1.Description))
            return l1.Description;

        var l2 = Discovery.Level2Agents
            .SelectMany(a => a.Methods)
            .FirstOrDefault(m => m.ToolName == toolName);
        if (l2 is not null && !string.IsNullOrWhiteSpace(l2.Description))
            return l2.Description;

        var l3 = Discovery.Level3Teams
            .SelectMany(t => t.Members)
            .SelectMany(m => m.Tools)
            .FirstOrDefault(m => m.ToolName == toolName);
        if (l3 is not null && !string.IsNullOrWhiteSpace(l3.Description))
            return l3.Description;

        return MentorUtils.ToFriendlyName(toolName);
    }

    /// <summary>
    /// Builds the confirmation message shown to the user: the action description followed by the
    /// concrete arguments the AI is about to pass, so the banner states <em>exactly</em> what will
    /// change (e.g. which product, what price) — not just a generic "Update a product's price".
    /// Parameter names are humanised (<c>newPrice</c> → "New Price"). Kept as a single string so it
    /// flows unchanged to every transport (in-process Blazor and the headless SignalR
    /// <c>ConfirmationRequired(actionId, toolName, message)</c> event alike).
    /// </summary>
    public string BuildMessage(string toolName, IEnumerable<KeyValuePair<string, object?>>? arguments)
    {
        var description = GetToolDescription(toolName);
        if (arguments is null) return description;

        var parts = new List<string>();
        foreach (var kv in arguments)
        {
            var text = kv.Value?.ToString();
            if (string.IsNullOrWhiteSpace(text)) continue;

            var label = MentorUtils.ToFriendlyName(MentorUtils.ToSnakeCase(kv.Key));
            parts.Add($"{label}: {text.Trim()}");
        }

        return parts.Count > 0
            ? $"{description} — {string.Join(" · ", parts)}"
            : description;
    }
}
