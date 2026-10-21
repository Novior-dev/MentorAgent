using ModelContextProtocol.Server;

namespace Novior.MentorAgent.Core;

/// <summary>
/// Provides <see cref="McpServerTool"/> instances for the MCP server by wrapping
/// every <c>[MentorAction]</c> method discovered by <see cref="MentorDiscoveryService"/>
/// as an <see cref="AIFunctionMcpServerTool"/>.
///
/// Registered in DI as a Singleton and consumed by the custom
/// <c>WithListToolsHandler</c> / <c>WithCallToolHandler</c> registered in
/// <see cref="Novior.MentorAgent.Extensions.ServiceCollectionExtensions"/>.
///
/// Tools are built once and cached — assemblies are scanned and functions created once per
/// process. Which of them a caller sees is decided per request, never cached (BUG-103).
///
/// Tool classes registered as Scoped are supported: a dedicated <see cref="IServiceScope"/>
/// is created at construction time so that scoped services can be resolved even though
/// this collection is itself a Singleton. The scope lives for the lifetime of the
/// application, and each tool class is resolved from it ONCE — whatever its lifetime, transient
/// included — so every MCP call shares one instance. Tool classes must therefore be stateless: one
/// that needs a DbContext creates it per call (IDbContextFactory, IServiceScopeFactory).
/// </summary>
internal sealed class MentorAgentMcpToolCollection : IEnumerable<McpServerTool>, IDisposable
{
    private readonly MentorDiscoveryService _discovery;
    private readonly IServiceScope _scope;
    private readonly ILogger<MentorAgentMcpToolCollection> _logger;
    private readonly MentorOptions _options;

    /// <summary>
    /// What may ever be published, built once on first access: the ungated actions, plus — only when
    /// <see cref="MentorOptions.McpCallerPrincipal"/> is set — the role-gated ones that need no
    /// confirmation. Who a role-gated action is published TO is decided per request, never cached.
    /// </summary>
    private readonly Lazy<Catalogue> _catalogue;

    /// <summary>1 while the resolver is failing, so its outage is logged once at Warning, not per request.</summary>
    private int _resolverFailing;

    /// <summary>1 once the withheld actions are named, so the startup report and the catalogue write it once.</summary>
    private int _withheldLogged;

    /// <summary>A tool and the roles that may see it (empty: everybody).</summary>
    private sealed record Candidate(McpServerTool Tool, string[] RequiredRoles);

    private sealed record Catalogue(
        IReadOnlyList<Candidate> All,
        IReadOnlyList<McpServerTool> Ungated,
        IReadOnlyDictionary<string, Candidate[]> ByName,
        bool HasRoleGated);

    public MentorAgentMcpToolCollection(
        MentorDiscoveryService discovery,
        IServiceScopeFactory scopeFactory,
        ILogger<MentorAgentMcpToolCollection> logger,
        IOptions<MentorOptions> options)
    {
        _discovery = discovery;
        _scope     = scopeFactory.CreateScope();
        _logger    = logger;
        _options   = options.Value;
        _catalogue = new Lazy<Catalogue>(BuildCatalogue, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    /// The tools this caller may see.
    ///
    /// <para>
    /// With no <see cref="MentorOptions.McpCallerPrincipal"/> the answer cannot vary: the one list of
    /// ungated tools, built once, is returned every time. With one configured the answer <b>depends on
    /// who is asking</b>, so it is decided on every request — the resolver is asked once, and the same
    /// principal answers for every role-gated action.
    /// </para>
    ///
    /// <para>
    /// BUG-103: the answer used to be cached per caller under a key made of the principal's
    /// <c>ClaimTypes.Role</c> claims, while the decision itself used <c>IsInRole</c> — which reads each
    /// identity's own <c>RoleClaimType</c>. With roles in a <c>roles</c> claim (Entra ID, or JwtBearer
    /// with <c>MapInboundClaims = false</c>) every signed-in caller got the same key, and the first one's
    /// list — the list <c>tools/call</c> looks names up in — was served to all the others. There is no
    /// key any more: only the decision.
    /// </para>
    /// </summary>
    public IReadOnlyList<McpServerTool> GetTools()
        => _options.McpCallerPrincipal is null
            ? _catalogue.Value.Ungated
            : GetTools(ResolveCaller());

    /// <summary>The tools <paramref name="caller"/> may see, by the one rule <see cref="IsPublishedTo"/>.</summary>
    internal IReadOnlyList<McpServerTool> GetTools(System.Security.Claims.ClaimsPrincipal? caller)
    {
        var catalogue = _catalogue.Value;
        if (!catalogue.HasRoleGated) return catalogue.Ungated;

        return [.. catalogue.All.Where(c => IsPublishedTo(c, caller)).Select(c => c.Tool)];
    }

    /// <summary>
    /// The tool a <c>tools/call</c> names, or <see langword="null"/> when this caller may not use it —
    /// a withheld action, one only another role may see, or a name that was never defined. The same
    /// decision as the list, with the caller resolved once for the request.
    /// </summary>
    internal McpServerTool? FindTool(string? name)
        => FindTool(name, _options.McpCallerPrincipal is null ? null : ResolveCaller());

    /// <inheritdoc cref="FindTool(string?)"/>
    internal McpServerTool? FindTool(string? name, System.Security.Claims.ClaimsPrincipal? caller)
        => name is not null && _catalogue.Value.ByName.TryGetValue(name, out var candidates)
            ? candidates.FirstOrDefault(c => IsPublishedTo(c, caller))?.Tool
            : null;

    /// <summary>
    /// Whether <paramref name="caller"/> may see <paramref name="candidate"/>: an ungated action always;
    /// a role-gated one when the caller's primary identity is authenticated and
    /// <see cref="System.Security.Claims.ClaimsPrincipal.IsInRole"/> holds for one of its roles — the
    /// test <c>[Authorize(Roles)]</c> and the widget's role gate apply: across every identity, each with
    /// its own <c>RoleClaimType</c>, case-sensitive.
    /// </summary>
    private static bool IsPublishedTo(Candidate candidate, System.Security.Claims.ClaimsPrincipal? caller)
        => candidate.RequiredRoles.Length == 0
           || (caller?.Identity?.IsAuthenticated == true && candidate.RequiredRoles.Any(caller.IsInRole));

    /// <summary>
    /// The principal <see cref="MentorOptions.McpCallerPrincipal"/> resolves to for this request, or
    /// <see langword="null"/> when there is no resolver or it threw. A resolver reaches into the host's
    /// own auth stack and that stack can be down; "the token service is unavailable" must mean withhold,
    /// never publish, and must not take the MCP endpoint with it. Logged at Warning when it starts
    /// failing and at Debug while it keeps failing, so an outage is not logged once per request.
    /// </summary>
    internal System.Security.Claims.ClaimsPrincipal? ResolveCaller()
    {
        if (_options.McpCallerPrincipal is not { } resolve) return null;

        try
        {
            var principal = resolve();
            Interlocked.Exchange(ref _resolverFailing, 0);
            return principal;
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _resolverFailing, 1) == 0)
            {
                // The type only: the resolver is the host's auth code, and its message can name the caller.
                _logger.LogWarning(
                    "[MentorAgent:MCP] McpCallerPrincipal threw ({Type}) — treating the caller as unidentified " +
                    "and withholding every role-gated action until it answers again.", ex.GetType().Name);
                _logger.LogDebug(ex, "[MentorAgent:MCP] McpCallerPrincipal threw.");
            }
            else
                _logger.LogDebug("[MentorAgent:MCP] McpCallerPrincipal is still failing ({Type}).", ex.GetType().Name);
            return null;
        }
    }

    /// <summary>
    /// Names the withheld actions without building the catalogue. <c>MapMentorAgentMcp</c> calls it, so the
    /// warning comes at startup, as documented, and not with the first MCP request (BUG-178). Which tools a
    /// caller sees is still decided when that request builds the catalogue, and per request after it.
    /// </summary>
    internal void ReportWithheld()
        => LogWithheld([.. _discovery.Discover().Level1Methods.Select(m => WithheldReason(m, out _)).OfType<string>()]);

    private Catalogue BuildCatalogue()
    {
        var methods = _discovery.Discover().Level1Methods;
        _logger.LogInformation("[MentorAgent:MCP] Building MCP tool collection — {N} Level1 method(s) discovered.",
            methods.Count);

        var candidates = new List<Candidate>(methods.Count);
        var withheld = new List<string>();
        var perCaller = new List<string>();

        foreach (var method in methods)
        {
            if (WithheldReason(method, out var roleGatedForCallers) is { } reason)
            {
                withheld.Add(reason);
                continue;
            }

            try
            {
                var instance = _scope.ServiceProvider.GetRequiredService(method.DeclaringType);
                var aiFunc = AIFunctionFactory.Create(
                    method:      method.Method,
                    target:      instance,
                    name:        method.ToolName,
                    description: method.Description);

                // OnToolResult travels with the function, so the hook sees the action's own result, as
                // it does in a chat turn (BUG-107).
                // Declared read-only ([MentorAction(ReadOnly = true)], options.IsReadOnlyAction): published with
                // readOnlyHint, the MCP word for the same declaration. Nothing gated reaches this point.
                var readOnly = method.ReadOnly || _options.IsReadOnlyAction?.Invoke(method.ToolName) == true;
                candidates.Add(new Candidate(
                    McpServerTool.Create(new Mcp.McpToolResultFunction(aiFunc, _options, _logger),
                        readOnly ? new McpServerToolCreateOptions { ReadOnly = true } : null),
                    roleGatedForCallers ? [.. method.RequiredRoles] : []));

                if (roleGatedForCallers)
                    perCaller.Add($"{method.ToolName} (roles: {string.Join(", ", method.RequiredRoles)})");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[MentorAgent:MCP] Could not build MCP server tool '{Tool}' ({Type}) — skipped. " +
                    "Ensure the class is registered in the DI container.",
                    method.ToolName, method.DeclaringType.Name);
            }
        }

        // Already written at startup when the endpoint was mapped with MapMentorAgentMcp.
        LogWithheld(withheld);

        if (perCaller.Count > 0)
            _logger.LogInformation(
                "[MentorAgent:MCP] {Count} role-gated action(s) published, per request, to the callers " +
                "McpCallerPrincipal identifies as holding the role: {Tools}.",
                perCaller.Count, string.Join("; ", perCaller));

        // Every candidate under its name, in discovery order: two actions sharing a tool name (the
        // orchestrator warns about it) must not stop the endpoint from starting, and a call is served by
        // the first one THIS caller may see — the one its own tools/list shows.
        var byName = candidates
            .GroupBy(c => c.Tool.ProtocolTool.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);

        _logger.LogInformation("[MentorAgent:MCP] MCP tool collection ready — {Built}/{Total} tool(s) registered.",
            candidates.Count, methods.Count);

        return new Catalogue(
            candidates,
            [.. candidates.Where(c => c.RequiredRoles.Length == 0).Select(c => c.Tool)],
            byName,
            HasRoleGated: perCaller.Count > 0);
    }

    /// <summary>
    /// Why <paramref name="method"/> is withheld from every MCP caller, or <see langword="null"/> when it is
    /// published: to everyone, or — <paramref name="roleGatedForCallers"/> — per request to the callers
    /// that hold its role. One rule for the startup report and the catalogue.
    /// </summary>
    private string? WithheldReason(MethodDiscoveryInfo method, out bool roleGatedForCallers)
    {
        // Fail closed on anything the author protected.
        //
        // This is the only surface where a tool is invoked by a caller who is not the
        // application's own user: an MCP client is a separate process reaching in over HTTP,
        // with no principal to check RequiredRoles against and nobody watching to answer a
        // RequiresConfirmation dialog. Everywhere else the gate travels with the tool
        // (GatedAIFunction, for exactly this reason) — here there is nothing for it to gate
        // against, so the honest answer is not to offer the tool at all. A protected action
        // that a remote client can call unattended is the gap the wrapper was written to close.
        // The predicate is the *other* gate, and it is checked here for exactly the same reason.
        // `MentorOptions.RequiresApproval` gates by tool name, and its whole purpose is covering
        // what the author did not annotate — tools reached through a Level-2 agent, tools from an
        // MCP client, tools in a class the host does not own. Reading only the attribute meant a
        // host who used the documented seam still published those actions to unattended remote
        // callers: the headless sample gates `update_stock` and `update_order_status` this way and
        // both were live on /mcp (BUG-035). How a host gated an action is not the remote caller's
        // business; that it is gated is.
        var predicateGated = _options.RequiresApproval?.Invoke(method.ToolName) == true;

        // ROADMAP #32 — an identity, not an override.
        //
        // A confirmation gate can never be satisfied here: RequiresConfirmation and the
        // RequiresApproval predicate both mean "ask a human first", and there is no human on
        // the far end of an MCP call. A ROLE gate can be satisfied, but only by a host that can
        // actually say who is calling — and then per request (IsPublishedTo). With no resolver —
        // the default — nothing changes.
        roleGatedForCallers = method.RequiredRoles.Length > 0
            && !method.RequiresConfirmation
            && !predicateGated
            && _options.McpCallerPrincipal is not null;

        if (roleGatedForCallers ||
            (method.RequiredRoles.Length == 0 && !method.RequiresConfirmation && !predicateGated))
            return null;

        return method.RequiresConfirmation && method.RequiredRoles.Length > 0
            ? $"{method.ToolName} (roles + approval)"
            : method.RequiredRoles.Length > 0
                ? $"{method.ToolName} (roles: {string.Join(", ", method.RequiredRoles)})"
                : predicateGated && !method.RequiresConfirmation
                    ? $"{method.ToolName} (RequiresApproval predicate)"
                    : $"{method.ToolName} (approval)";
    }

    /// <summary>The warning that names the withheld actions, written once: at startup, or by the first catalogue.</summary>
    private void LogWithheld(IReadOnlyCollection<string> withheld)
    {
        if (withheld.Count == 0 || Interlocked.Exchange(ref _withheldLogged, 1) == 1) return;

        _logger.LogWarning(
            "[MentorAgent:MCP] {Count} action(s) withheld from the MCP server because they are " +
            "gated and an MCP client cannot satisfy the gate — there is no signed-in principal " +
            "to check roles against and no user to approve anything: {Tools}. Remove the " +
            "[MentorAction] gate if the action really is safe for an unattended remote caller.",
            withheld.Count, string.Join("; ", withheld));
    }

    public IEnumerator<McpServerTool> GetEnumerator() => GetTools().GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    public void Dispose() => _scope.Dispose();
}
