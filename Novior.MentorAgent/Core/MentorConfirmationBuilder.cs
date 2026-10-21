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
    private readonly ILogger _logger;

    // MCP tool names gated at connect time by McpToolsBuilder (external tools are not in discovery).
    private readonly ConcurrentDictionary<string, byte> _externalApproval = new(StringComparer.OrdinalIgnoreCase);

    private DiscoveryResult? _cache;

    public MentorConfirmationBuilder(
        MentorDiscoveryService discovery,
        IOptions<MentorOptions> options,
        ILogger<MentorConfirmationBuilder>? logger = null)
    {
        _discovery = discovery;
        _options   = options.Value;
        _logger    = (ILogger?)logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
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
    /// <param name="toolName">The tool's name.</param>
    /// <param name="method">The method the function was built from, when the caller has the function.</param>
    public bool RequiresApproval(string toolName, MethodInfo? method = null)
        => _options.RequireConfirmation && IsDeclaredConfirmationGated(toolName, method);

    /// <summary>
    /// Whether the application DECLARES the tool as needing a person's confirmation —
    /// <c>RequiresConfirmation</c>, the <c>RequiresApproval</c> predicate, or an approval-required MCP
    /// server — regardless of <see cref="MentorOptions.RequireConfirmation"/>. That switch decides whether
    /// the banner is shown; the declaration is how the application marks an action that changes data,
    /// and tool filtering reads it so such an action never reaches the model alone (BUG-099).
    /// <para>
    /// With the function's <paramref name="method"/>, that method's declaration decides, not another method's
    /// with the same name (BUG-188). By name alone, as before, the first copy at each level counts.
    /// </para>
    /// </summary>
    internal bool IsDeclaredConfirmationGated(string toolName, MethodInfo? method = null)
    {
        if (string.IsNullOrEmpty(toolName)) return false;

        if (_externalApproval.ContainsKey(toolName)) return true;

        if (Discovery.BuiltFrom(toolName, method) is { } own)
            return own.RequiresConfirmation || _options.RequiresApproval?.Invoke(toolName) == true;

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
    /// bar instead of the raw technical name. Falls back to a title-cased tool name. With the function's
    /// <paramref name="method"/>, that method's description (BUG-188).
    /// </summary>
    public string GetToolDescription(string toolName, MethodInfo? method = null)
    {
        if (Discovery.BuiltFrom(toolName, method) is { } own)
            return string.IsNullOrWhiteSpace(own.Description) ? MentorUtils.ToFriendlyName(toolName) : own.Description;

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
    ///
    /// <para>
    /// BUG-186: the arguments are the model's, and with <see cref="MentorOptions.RequireActionForChangeRequests"/>
    /// the banner is the last look a person gets at them. Each value is cleaned of control and bidirectional
    /// formatting characters and cut at <see cref="ArgumentValueLimit"/>; they follow the order of the method's
    /// parameters, not of the model's JSON. <see cref="MentorOptions.ConfirmationMessage"/> lets the host write
    /// the text instead; when it throws or writes nothing, this text is shown.
    /// </para>
    /// <para>
    /// With the function's <paramref name="method"/>, the description and the parameter order are that method's,
    /// not another method's with the same name (BUG-188).
    /// </para>
    /// </summary>
    public string BuildMessage(
        string toolName, IEnumerable<KeyValuePair<string, object?>>? arguments, IServiceProvider? services = null,
        MethodInfo? method = null)
    {
        var description = GetToolDescription(toolName, method);
        var shown = ShownArguments(toolName, arguments, method);

        var message = shown.Count > 0
            ? $"{description} — {string.Join(" · ", shown.Select(a => $"{a.Label}: {a.Value}"))}"
            : description;

        if (_options.ConfirmationMessage is not { } write) return message;

        try
        {
            var written = write(new MentorConfirmationContext
            {
                ToolName       = toolName,
                Description    = description,
                Arguments      = shown,
                Language       = _options.Language,
                DefaultMessage = message,
                Services       = services ?? EmptyServices.Instance,
            });
            // Null is the host's way of saying "the default for this one".
            if (written is null) return message;
            if (!string.IsNullOrWhiteSpace(written)) return written;

            _logger.LogWarning(
                "[MentorAgent] ConfirmationMessage wrote no text for '{Tool}'; the default text is shown.", toolName);
        }
        catch (Exception ex)
        {
            // The type only: a host's exception message can carry the arguments it was formatting (BUG-148).
            _logger.LogWarning(
                "[MentorAgent] ConfirmationMessage threw {Exception} for '{Tool}'; the default text is shown.",
                ex.GetType().Name, toolName);
        }

        return message;
    }

    /// <summary>The most of one argument's value the banner shows; a longer one is cut and ends with "…".</summary>
    internal const int ArgumentValueLimit = 200;

    /// <summary>
    /// The arguments as the banner shows them: in the order of the method's parameters, then any the method
    /// does not declare in the model's order (an MCP tool's are all in the model's order); cleaned, cut, and
    /// without the empty ones.
    /// </summary>
    private List<MentorConfirmationArgument> ShownArguments(
        string toolName, IEnumerable<KeyValuePair<string, object?>>? arguments, MethodInfo? method)
    {
        if (arguments is null) return [];

        var given = arguments.ToList();
        var ordered = new List<KeyValuePair<string, object?>>(given.Count);
        foreach (var parameter in DeclaredParameters(toolName, method))
        {
            var i = given.FindIndex(kv => string.Equals(kv.Key, parameter, StringComparison.Ordinal));
            if (i < 0) i = given.FindIndex(kv => string.Equals(kv.Key, parameter, StringComparison.OrdinalIgnoreCase));
            if (i < 0) continue;
            ordered.Add(given[i]);
            given.RemoveAt(i);
        }
        ordered.AddRange(given);

        var shown = new List<MentorConfirmationArgument>(ordered.Count);
        foreach (var kv in ordered)
        {
            var value = ForBanner(kv.Value?.ToString());
            if (value.Length == 0) continue;
            shown.Add(new MentorConfirmationArgument(
                kv.Key, MentorUtils.ToFriendlyName(MentorUtils.ToSnakeCase(kv.Key)), value));
        }
        return shown;
    }

    /// <summary>The parameter names of the method behind <paramref name="toolName"/>, in order; none for an MCP tool.</summary>
    private IEnumerable<string> DeclaredParameters(string toolName, MethodInfo? method)
        => Discovery.Called(toolName, method)?.Method.GetParameters().Select(p => p.Name).OfType<string>() ?? [];

    /// <summary>
    /// One value as the banner shows it: control characters, line and paragraph separators, the bidirectional
    /// embeddings, overrides, isolates and marks, and zero-width spaces become a space; runs of white space
    /// become one; the result is trimmed and cut at <see cref="ArgumentValueLimit"/>, never inside a
    /// surrogate pair.
    /// </summary>
    internal static string ForBanner(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";

        var sb = new System.Text.StringBuilder(Math.Min(value.Length, ArgumentValueLimit + 1));
        var space = false;
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c) || IsInvisibleFormat(c))
            {
                space = sb.Length > 0;
                continue;
            }
            if (space) { sb.Append(' '); space = false; }
            sb.Append(c);
            if (sb.Length > ArgumentValueLimit) break;
        }

        if (sb.Length <= ArgumentValueLimit) return sb.ToString();

        var cut = ArgumentValueLimit;
        if (char.IsHighSurrogate(sb[cut - 1])) cut--;
        return sb.ToString(0, cut).TrimEnd() + "…";
    }

    private static bool IsInvisibleFormat(char c)
        => c is '\u200B' or '\u200E' or '\u200F' or '\u2028' or '\u2029' or '\uFEFF'
           || c is >= '\u202A' and <= '\u202E'
           || c is >= '\u2066' and <= '\u2069';

    /// <summary>What <see cref="MentorConfirmationContext.Services"/> is when the caller has no scope to give.</summary>
    private sealed class EmptyServices : IServiceProvider
    {
        public static readonly EmptyServices Instance = new();
        public object? GetService(Type serviceType) => null;
    }
}
