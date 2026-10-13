

using Novior.MentorAgent.Options;
using ModelContextProtocol.Client;

namespace Novior.MentorAgent.Core;

/// <summary>
/// Connects to external MCP (Model Context Protocol) servers configured in
/// <see cref="MentorOptions.McpServers"/> and exposes their tools as Level-1
/// tools for the coordinator — exactly like <c>[MentorAction]</c> methods.
///
/// Scoped — one instance per user session. Maintains open connections for the
/// lifetime of the session; all <see cref="McpClient"/> instances are disposed
/// when this service is disposed.
/// </summary>
internal sealed class McpToolsBuilder : IAsyncDisposable
{
    private readonly MentorOptions _options;
    private readonly IMentorStateService _stateService;
    private readonly MentorConfirmationBuilder _confirmations;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<McpToolsBuilder> _logger;
    // Sessions this scope owns — the per-session ones only. A shared session belongs to the pool
    // and outlives every scope that uses it.
    private readonly List<Mcp.IMcpSession> _ownedSessions = [];
    private readonly Mcp.McpClientPool? _pool;
    private readonly Mcp.IMcpSessionFactory _factory;

    private readonly HashSet<string> _readOnly = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _notReadOnly = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The tools of the last build whose server marks them <c>readOnlyHint: true</c>, from servers that
    /// need no confirmation. Tool filtering offers only those on a turn where no action matched (BUG-106).
    /// </summary>
    internal IReadOnlySet<string> ReadOnlyToolNames => _readOnly;

    /// <summary>
    /// The tools of the last build that a server does NOT mark read-only, or that need confirmation. A name in
    /// both sets — two servers sharing a tool name — is not offered on a turn that matched nothing.
    /// </summary>
    internal IReadOnlySet<string> NotReadOnlyToolNames => _notReadOnly;

    public McpToolsBuilder(
        IOptions<MentorOptions> options,
        IMentorStateService stateService,
        MentorConfirmationBuilder confirmations,
        ILoggerFactory loggerFactory,
        ILogger<McpToolsBuilder> logger,
        Mcp.McpClientPool? pool = null,
        Mcp.IMcpSessionFactory? factory = null)
    {
        _options = options.Value;
        _stateService = stateService;
        _confirmations = confirmations;
        _loggerFactory = loggerFactory;
        _logger = logger;
        _pool = pool;
        _factory = factory ?? new Mcp.McpSessionFactory(loggerFactory);
    }

    // ── Build ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Connects to all configured MCP servers and returns their tools as
    /// <see cref="AITool"/> instances ready to be added to the coordinator.
    ///
    /// Fires <see cref="IMentorStateService.NotifyMcpServerStatusChanged"/> for
    /// each server (connected = <c>true</c> on success, <c>false</c> on failure).
    /// </summary>
    public async Task<List<AITool>> BuildAsync(CancellationToken ct = default)
    {
        var tools = new List<AITool>();
        _readOnly.Clear();
        _notReadOnly.Clear();

        if (_options.McpServers is not { Length: > 0 })
            return tools;

        // An entry that was renamed or re-pointed leaves a session behind, and an orphaned
        // stdio session is an orphaned child process.
        if (_pool is not null) await _pool.PruneAsync(_options.McpServers);

        foreach (var server in _options.McpServers)
        {
            try
            {
                // Shared servers come from the process-wide pool (one connection, one child
                // process); the rest are opened here and die with this scope, exactly as before.
                var shared = Mcp.McpServerIdentity.IsShared(server);
                Mcp.IMcpSession session;
                if (shared && _pool is not null)
                {
                    session = await _pool.GetOrConnectAsync(server, ct);
                }
                else
                {
                    session = await _factory.ConnectAsync(server, ct);
                    _ownedSessions.Add(session);
                }

                var mcpTools = session.Tools;
                int addedCount = 0;

                foreach (var tool in mcpTools)
                {
                    // Apply AllowedTools filter
                    if (server.AllowedTools is { Length: > 0 } &&
                        !server.AllowedTools.Contains(tool.Name, StringComparer.OrdinalIgnoreCase))
                        continue;

                    // Roadmap #16 — MCP tools live outside the discovery cache, so a server marked
                    // RequiresConfirmation registers its tool names with the shared confirmation
                    // builder. From there they are gated exactly like a [MentorAction] confirmation,
                    // in both HITL modes.
                    if (server.RequiresConfirmation)
                        _confirmations.RegisterApprovalRequired(tool.Name);

                    tools.Add(tool);
                    addedCount++;

                    if (!server.RequiresConfirmation && session.ReadOnlyToolNames.Contains(tool.Name))
                        _readOnly.Add(tool.Name);
                    else
                        _notReadOnly.Add(tool.Name);
                }

                _stateService.NotifyMcpServerStatusChanged(server.Name, connected: true);
                _logger.LogInformation(
                    "[MentorAgent:MCP] Server '{Name}' connected — {Count} tool(s) registered{Gated}.",
                    server.Name, addedCount,
                    server.RequiresConfirmation ? " (human approval required)" : string.Empty);
            }
            catch (Exception ex)
            {
                _stateService.NotifyMcpServerStatusChanged(server.Name, connected: false);
                _logger.LogWarning(ex,
                    "[MentorAgent:MCP] Failed to connect to MCP server '{Name}'.", server.Name);
            }
        }

        return tools;
    }

    // ── Transport creation ─────────────────────────────────────────────────────

    // ── Dispose ────────────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        // Only what this scope owns. Disposing a pooled session here would close it for every
        // other session in the process — the bug pooling is supposed to avoid, inverted.
        foreach (var session in _ownedSessions)
        {
            try { await session.DisposeAsync(); }
            catch { /* best-effort */ }
        }
        _ownedSessions.Clear();
    }
}
