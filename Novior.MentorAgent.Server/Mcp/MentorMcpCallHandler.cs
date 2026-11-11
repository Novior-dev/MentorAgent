using System.Diagnostics;
using System.Security.Claims;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Core.Mcp;
using Novior.MentorAgent.Observability;
using Novior.MentorAgent.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

// Story 105: moved from MentorAgent with the MCP server it serves — it reads the request's remote
// address through IHttpContextAccessor, which is ASP.NET Core.
namespace Novior.MentorAgent.Server.Mcp;

/// <summary>
/// The MCP server's <c>tools/call</c>, with the gates every other surface already applies (BUG-107):
/// <see cref="MentorOptions.RateLimitPerUser"/>, the tool metrics, and — through
/// <see cref="McpToolResultFunction"/> — <see cref="MentorOptions.OnToolResult"/>. The call used to invoke
/// the tool and return its result with nothing around it: a host that redacts with the hook served the
/// unredacted result to MCP callers, 30 calls went through a limit of 20, and the dashboard never moved.
///
/// <para>
/// Nothing is thrown out of here: the SDK logs any exception from the call handler as a crashed tool and
/// tells the caller only "An error occurred" (BUG-101). A refused or unknown call is a result with
/// <c>isError</c>.
/// </para>
///
/// <para>
/// Every decision about a tool the server knows reaches the action observers (story 180), and the audit line
/// says why a call was refused: <c>denied-role</c>, <c>withheld</c> or <c>unknown</c>, which used to be
/// <c>unknown</c> for all three. The caller is still told the same for all of them.
/// </para>
///
/// <para>
/// The application's <see cref="IMentorActionPolicy"/> instances decide here too (stories 311, 153): on
/// <c>tools/list</c>, which tools each caller is offered; on <c>tools/call</c>, after the tool is found and before
/// it runs, whether the call may run. Nobody can confirm a call here, so a policy's Confirm refuses it.
/// </para>
///
/// <para>
/// And whether the action is on (story 1549): the code's word (<c>[MentorAction(Enabled = false)]</c>) or the
/// application's <see cref="IMentorActionGovernor"/>, asked on <c>tools/list</c> before the policies and on
/// <c>tools/call</c> as soon as the tool is found. A tool that is off is refused with the words of an unknown one. With
/// a governor, the roles it gives an action decide which callers see it and may call it, in place of the code's
/// (story 1546): an action withheld only for its roles is published once the governor requires none.
/// </para>
/// </summary>
internal sealed class MentorMcpCallHandler(
    MentorAgentMcpToolCollection tools,
    MentorRateLimiter rateLimiter,
    MentorMetrics metrics,
    IOptions<MentorOptions> options,
    ILogger<MentorMcpCallHandler> logger,
    IServiceProvider services,
    MentorTelemetry? telemetry = null,
    MentorActionDecisions? decisions = null,
    MentorActionPolicies? policies = null,
    Abstractions.Core.MentorLocalizer? localizer = null,
    MentorActionGovernance? governance = null)
{
    private readonly MentorOptions _options = options.Value;

    /// <summary>What the caller reads for a call a policy wanted confirmed: nobody can confirm it here.</summary>
    internal const string NobodyToConfirm =
        "Not run: this action needs a person to confirm it, and a call to this MCP server has nobody to ask. " +
        "Say that it has to be done from the application itself.";

    /// <summary>
    /// The <c>tools/list</c> of this request: the tools the caller may see that are on — by the code, or by the
    /// <see cref="IMentorActionGovernor"/> — less those an <see cref="IMentorActionPolicy"/> does not offer this caller.
    /// </summary>
    internal async ValueTask<ListToolsResult> ListAsync(
        RequestContext<ListToolsRequestParams> context, CancellationToken cancellationToken)
    {
        // Resolved once for the request, as for a call: the same principal decides the roles and is the policies' user.
        var rolePrincipal = tools.ResolveCaller();

        // Story 155 — the tenant of this request, before anything is decided for it: a resolver that names none lists
        // nothing, never the single tenant's tools.
        if (await TenantAsync(rolePrincipal ?? context.User, context.Services, cancellationToken) is { } tenant)
        {
            if (!tenant.Ok)
            {
                logger.LogWarning(
                    "[MentorAgent:MCP] The tenant resolver named no tenant ({Failure}) — tools/list lists nothing.", tenant.Failure);
                return new ListToolsResult { Tools = [] };
            }
            MentorTenantContext.Enter(tenant.Id);
        }

        // Story 157 — the request's tenant on its log lines, as on a turn's.
        using var tenantLogScope = MentorTenantLogScope.Begin(logger, MentorTenantContext.Ambient);

        var governor = governance is { Any: true } registered ? registered : null;
        if (governor is null && policies is not { Any: true })
            return new ListToolsResult { Tools = [.. tools.GetTools(rolePrincipal).Select(t => t.ProtocolTool)] };

        await using var scope = RequestScope.For(context.Services, services);
        var offered = new List<Tool>();

        // Stories 1549, 1546, 1547, 1548 — with a governor, every action it decides about here: listed when it is on, the
        // caller holds one of the roles it gives, or the code's, and nobody has to confirm it — the governor took the
        // code's confirmation away, or neither asks for one — with the description the governor gives, or the code's. The
        // policies are asked with those roles and that description. A call is asked about with the code's description:
        // the server keeps no session, and the governor is not asked at a call what the model read.
        if (governor is not null)
        {
            foreach (var governable in tools.GovernableTools())
            {
                var name = governable.Tool.ProtocolTool.Name;
                var request = PolicyRequest(MentorActionStage.Offer, name, governable.Tool.ProtocolTool.Description,
                    governable.CodeRoles, rolePrincipal ?? context.User,
                    context.Server?.SessionId, callId: null, arguments: null, scope.Services,
                    _options.RequireConfirmation && governable.CodeConfirms);
                if (!(await governor.IsEnabledAsync(request, governable.EnabledByDefault, cancellationToken)).Enabled) continue;
                var governed = await governor.RequiredRolesAsync(request, cancellationToken);
                if (governed.Failed) continue;
                var roles = governed.Roles ?? governable.CodeRoles;
                if (!tools.IsPublishedTo(roles, rolePrincipal)) continue;
                var confirmation = await governor.ConfirmationAsync(request with { RequiredRoles = roles }, cancellationToken);
                if (confirmation.Failed || Asks(confirmation.Choice, governable.CodeConfirms)) continue;

                // Story 1548 — the description the caller's model reads: the governor's, in a copy of the tool.
                var described = await governor.DescriptionAsync(request with { RequiredRoles = roles }, cancellationToken);
                if (described.Failed) continue;
                if (policies is { Any: true }
                    && !await policies.IsOfferedAsync(request with
                    {
                        RequiredRoles = roles,
                        RequiresConfirmation = false,
                        Description = described.Description ?? request.Description,
                    }, cancellationToken))
                    continue;
                offered.Add(described.Description is { } text
                    ? Described(governable.Tool.ProtocolTool, text)
                    : governable.Tool.ProtocolTool);
            }
            return new ListToolsResult { Tools = offered };
        }

        foreach (var tool in tools.GetTools(rolePrincipal))
        {
            var name = tool.ProtocolTool.Name;
            var request = PolicyRequest(MentorActionStage.Offer, name, tool.ProtocolTool.Description,
                tools.Find(name, rolePrincipal).RequiredRoles, rolePrincipal ?? context.User,
                context.Server?.SessionId, callId: null, arguments: null, scope.Services);
            if (await policies!.IsOfferedAsync(request, cancellationToken)) offered.Add(tool.ProtocolTool);
        }
        return new ListToolsResult { Tools = offered };
    }

    internal async ValueTask<CallToolResult> HandleAsync(
        RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var name = context.Params?.Name;

        // Resolved once for the request: the same principal decides the role gate and names the caller.
        // Roles are decided only from McpCallerPrincipal — reading them from the request's user when the
        // host set no resolver would publish role-gated actions to hosts that never opted in.
        var rolePrincipal = tools.ResolveCaller();
        var caller = CallerId(rolePrincipal ?? context.User)
                     ?? RemoteAddress(context.Services);

        // Story 155 — the tenant of this call, before it is counted: one the resolver cannot name is refused, never served
        // as the single tenant. The action reads it through IMentorTenant, since its class is shared by every call.
        string? tenantId = null;
        if (await TenantAsync(rolePrincipal ?? context.User, context.Services, cancellationToken) is { } tenant)
        {
            if (!tenant.Ok)
            {
                Audit(name, caller, "tenant-unresolved", started);
                logger.LogWarning(
                    "[MentorAgent:MCP] The tenant resolver named no tenant ({Failure}) — the call is refused.", tenant.Failure);
                return Error(Text("tenant.unresolved",
                    "This request could not be processed: the organization it belongs to could not be identified."));
            }
            MentorTenantContext.Enter(tenant.Id);
            tenantId = tenant.Id;
        }

        // Story 157 — the call's tenant on its log lines, the one Information line of the call included.
        using var tenantLogScope = MentorTenantLogScope.Begin(logger, tenantId);

        // Rate limit first, so every call counts — an unknown name too, which bounds name probing.
        if (_options.RateLimitPerUser > 0)
        {
            // No id and no address: one shared allowance — the tenant's own, as every key here (story 156).
            var key = "mcp:" + MentorTenantContext.Key(tenantId, caller ?? "anonymous");
            var (allowed, _) = rateLimiter.CheckAndIncrement(key);
            if (!allowed)
            {
                Audit(name, caller, "rate-limited", started);
                // A name the server does not know is the caller's text: only a known one is a decision.
                if (decisions is { HasObservers: true } && tools.Find(name, rolePrincipal) is { Result: not MentorAgentMcpToolCollection.LookupResult.Unknown } known)
                    Decide(context, known, name!, rolePrincipal, MentorActionOutcome.Denied, MentorActionReasons.RateLimited);
                return Error(
                    $"Rate limit reached: at most {_options.RateLimitPerUser} tool calls every " +
                    $"{_options.RateLimitWindowSecs} s for this caller. Retry in " +
                    $"{rateLimiter.GetSecondsUntilReset(key)} s.");
            }
        }

        // A name the server does not list for this caller: a gated action it withholds, one only another
        // role may use, or one that never existed. The same words for all of them, so probing names reveals
        // nothing about gated actions (BUG-101).
        // Stories 1549, 1546, 1547 — with a governor, an action it decides about is found whatever the code's roles and
        // confirmation: its answers decide whether it is on, whom it serves and whether it asks to be confirmed.
        var governable = governance is { Any: true } ? tools.FindGovernable(name) : null;
        var lookup = governable is { } found
            ? new MentorAgentMcpToolCollection.Lookup(found.Tool, MentorAgentMcpToolCollection.LookupResult.Found,
                found.CodeRoles, found.Tool.ProtocolTool.Description, found.EnabledByDefault, CodeConfirms: found.CodeConfirms)
            : tools.Find(name, rolePrincipal);
        if (lookup.Tool is not { } tool)
        {
            Audit(name, caller, lookup.Result switch
            {
                MentorAgentMcpToolCollection.LookupResult.RoleMissing or
                MentorAgentMcpToolCollection.LookupResult.LoginRequired => "denied-role",
                MentorAgentMcpToolCollection.LookupResult.Withheld      => "withheld",
                _                                                       => "unknown",
            }, started);

            if (lookup.Result != MentorAgentMcpToolCollection.LookupResult.Unknown)
                Decide(context, lookup, name!, rolePrincipal, MentorActionOutcome.Denied, lookup.Result switch
                {
                    MentorAgentMcpToolCollection.LookupResult.RoleMissing   => MentorActionReasons.RoleMissing,
                    MentorAgentMcpToolCollection.LookupResult.LoginRequired => MentorActionReasons.LoginRequired,
                    _                                                       => MentorActionReasons.Withheld,
                });
            return Error($"Unknown tool: '{name}'. This server does not offer it; tools/list names the tools it does.");
        }

        // Story 1549 — a tool that is off is refused before anything counts it, as a withheld one is.
        if (await RefusedAsOffAsync(context, lookup, tool.ProtocolTool.Name, rolePrincipal, caller, started,
                cancellationToken) is { } off)
            return off;

        if (governable is not null)
        {
            var (refusedForRoles, governed) = await GovernedRolesAsync(context, lookup, tool.ProtocolTool.Name, rolePrincipal,
                caller, started, cancellationToken);
            if (refusedForRoles is not null) return refusedForRoles;

            // Story 1547 — then its confirmation: nobody can confirm a call here, so one that asks is withheld.
            var (refusedForConfirmation, confirmed) = await GovernedConfirmationAsync(context, governed, tool.ProtocolTool.Name,
                rolePrincipal, caller, started, cancellationToken);
            if (refusedForConfirmation is not null) return refusedForConfirmation;
            lookup = confirmed;
        }

        // Metered only once the tool is known: an unknown name is the caller's choice, and counting it
        // would let anyone grow the metrics without bound.
        metrics.RecordToolCall(tool.ProtocolTool.Name);
        // "mentoragent.tool" is a metric tag dashboards group by, not a namespace: keep it on a rename.
        telemetry?.ToolCalls.Add(1, MentorTelemetry.Tags("mentoragent.tool", tool.ProtocolTool.Name));
        using var activity = telemetry?.StartActivity($"mentor.mcp.tool.{tool.ProtocolTool.Name}");
        var toolName = tool.ProtocolTool.Name;

        // Stories 311, 153 — the application's policies, with the arguments, before the call runs. Allowed comes
        // only once they let it through.
        if (policies is { Any: true } && await RefusedByPoliciesAsync(context, lookup, toolName, rolePrincipal, caller,
                started, activity, cancellationToken) is { } refused)
            return refused;

        Decide(context, lookup, toolName, rolePrincipal, MentorActionOutcome.Allowed, MentorActionReasons.Allowed, activity);

        var outcome = "failed";
        try
        {
            var result = await tool.InvokeAsync(context, cancellationToken);
            outcome = result.IsError == true ? "error" : "ok";
            // The one error result this server gives is McpToolResultFunction's, when OnToolResult threw: the action
            // ran and its result was withheld, as on the chat path. An action that throws arrives as an exception.
            Decide(context, lookup, toolName, rolePrincipal, MentorActionOutcome.Executed,
                result.IsError == true ? MentorActionReasons.ResultWithheld : MentorActionReasons.Completed, activity);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The client cancelled the call (notifications/cancelled), or the session closed under it.
            outcome = "cancelled";
            Decide(context, lookup, toolName, rolePrincipal, MentorActionOutcome.Stopped, MentorActionReasons.Stop, activity);
            throw;
        }
        catch (Exception ex)
        {
            Decide(context, lookup, toolName, rolePrincipal, MentorActionOutcome.Failed, MentorActionReasons.Exception,
                activity, ex.GetType().FullName);
            throw;
        }
        finally
        {
            Audit(toolName, caller, outcome, started);
        }
    }

    /// <summary>
    /// Asks the policies about the call. When they refuse it, the call gets its last decision and its audit line here,
    /// and the result is what the caller reads; null when they let it through. A policy's Confirm refuses it as a
    /// declared confirmation would: <c>nobody-to-confirm</c>, with the policy's reason in the decision's detail.
    /// </summary>
    private async ValueTask<CallToolResult?> RefusedByPoliciesAsync(RequestContext<CallToolRequestParams> context,
        MentorAgentMcpToolCollection.Lookup lookup, string toolName, ClaimsPrincipal? rolePrincipal, string? caller,
        long started, Activity? activity, CancellationToken cancellationToken)
    {
        PolicyCheck check;
        await using (var scope = RequestScope.For(context.Services, services))
        {
            try
            {
                check = await policies!.CheckCallAsync(PolicyRequest(MentorActionStage.Call, toolName, lookup.Description,
                    lookup.RequiredRoles, rolePrincipal ?? context.User, context.Server?.SessionId,
                    context.JsonRpcRequest?.Id.ToString(), context.Params?.Arguments, scope.Services),
                    allowedOnly: false, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Audit(toolName, caller, "cancelled", started);
                Decide(context, lookup, toolName, rolePrincipal, MentorActionOutcome.Stopped, MentorActionReasons.Stop, activity);
                throw;
            }
        }

        (MentorActionOutcome Outcome, string Reason, string Audit, string Text)? refusal = check.Result switch
        {
            // The same words as for a role this caller lacks: probing names reveals nothing (BUG-101).
            PolicyResult.NotAllowed => (MentorActionOutcome.Denied, MentorActionReasons.NotAuthorized, "denied-policy",
                $"Unknown tool: '{toolName}'. This server does not offer it; tools/list names the tools it does."),
            PolicyResult.Stop       => (MentorActionOutcome.Blocked, MentorActionReasons.Policy, "blocked",
                string.Format(Text("action.stopped.rule", "Stopped by a rule: {0}"), check.Reason)),
            PolicyResult.Confirm    => (MentorActionOutcome.Denied, MentorActionReasons.NobodyToConfirm, "nobody-to-confirm",
                NobodyToConfirm),
            PolicyResult.Failed     => (MentorActionOutcome.PolicyFailed, check.ReasonCode ?? MentorActionReasons.PolicyException,
                "policy-failed", Text("action.stopped.rule.failed", "Stopped: the check before this action could not be completed.")),
            _                       => null,
        };
        if (refusal is not { } r) return null;

        Audit(toolName, caller, r.Audit, started);
        Decide(context, lookup, toolName, rolePrincipal, r.Outcome, r.Reason, activity, check.ExceptionType,
            detail: check.Result is PolicyResult.Stop or PolicyResult.Confirm ? check.Reason : null);
        return Error(r.Text);
    }

    /// <summary>
    /// Whether the tool of a call is off (story 1549): the code ships it off and no governor turned it on, or the
    /// governor turned it off or failed. When it is, the call gets its last decision and its audit line here, and the
    /// result is what the caller reads — for a tool that is off, the words of an unknown one (BUG-101); null when it is on.
    /// </summary>
    private async ValueTask<CallToolResult?> RefusedAsOffAsync(RequestContext<CallToolRequestParams> context,
        MentorAgentMcpToolCollection.Lookup lookup, string toolName, ClaimsPrincipal? rolePrincipal, string? caller,
        long started, CancellationToken cancellationToken)
    {
        GovernorAnswer answer;
        if (governance is not { Any: true } governor)
        {
            if (lookup.EnabledByDefault) return null;
            answer = new(false);
        }
        else
        {
            await using var scope = RequestScope.For(context.Services, services);
            try
            {
                answer = await governor.IsEnabledAsync(PolicyRequest(MentorActionStage.Call, toolName, lookup.Description,
                    lookup.RequiredRoles, rolePrincipal ?? context.User, context.Server?.SessionId,
                    context.JsonRpcRequest?.Id.ToString(), context.Params?.Arguments, scope.Services,
                    _options.RequireConfirmation && lookup.CodeConfirms),
                    lookup.EnabledByDefault, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Audit(toolName, caller, "cancelled", started);
                Decide(context, lookup, toolName, rolePrincipal, MentorActionOutcome.Stopped, MentorActionReasons.Stop);
                throw;
            }
        }

        if (answer.Enabled) return null;

        if (answer.Failed)   // the governance logged it
        {
            Audit(toolName, caller, "governor-failed", started);
            Decide(context, lookup, toolName, rolePrincipal, MentorActionOutcome.PolicyFailed, MentorActionReasons.GovernorFailed,
                exceptionType: answer.ExceptionType);
            return Error(Text("action.stopped.rule.failed", "Stopped: the check before this action could not be completed."));
        }

        Audit(toolName, caller, "disabled", started);
        Decide(context, lookup, toolName, rolePrincipal, MentorActionOutcome.Denied, MentorActionReasons.Disabled);
        return Error($"Unknown tool: '{toolName}'. This server does not offer it; tools/list names the tools it does.");
    }

    /// <summary>
    /// The roles the governor gives the tool of a call (story 1546), in place of the code's, and whether this caller
    /// holds one, by the rule of the code's roles: the lookup with them, or the refusal, with its last decision and audit
    /// line — withheld without <c>McpCallerPrincipal</c>, else denied for the role — and the words of an unknown tool
    /// (BUG-101). A governor that fails stops the call.
    /// </summary>
    private async ValueTask<(CallToolResult? Refused, MentorAgentMcpToolCollection.Lookup Lookup)> GovernedRolesAsync(
        RequestContext<CallToolRequestParams> context, MentorAgentMcpToolCollection.Lookup lookup, string toolName,
        ClaimsPrincipal? rolePrincipal, string? caller, long started, CancellationToken cancellationToken)
    {
        GovernorRoles governed;
        await using (var scope = RequestScope.For(context.Services, services))
        {
            try
            {
                governed = await governance!.RequiredRolesAsync(PolicyRequest(MentorActionStage.Call, toolName,
                    lookup.Description, lookup.RequiredRoles, rolePrincipal ?? context.User, context.Server?.SessionId,
                    context.JsonRpcRequest?.Id.ToString(), context.Params?.Arguments, scope.Services,
                    _options.RequireConfirmation && lookup.CodeConfirms), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Audit(toolName, caller, "cancelled", started);
                Decide(context, lookup, toolName, rolePrincipal, MentorActionOutcome.Stopped, MentorActionReasons.Stop);
                throw;
            }
        }

        if (governed.Failed)   // the governance logged it
        {
            Audit(toolName, caller, "governor-failed", started);
            Decide(context, lookup, toolName, rolePrincipal, MentorActionOutcome.PolicyFailed, MentorActionReasons.GovernorFailed,
                exceptionType: governed.ExceptionType);
            return (Error(Text("action.stopped.rule.failed", "Stopped: the check before this action could not be completed.")), lookup);
        }

        lookup = lookup with { RequiredRoles = governed.Roles ?? lookup.RequiredRoles, RolesFromOutside = governed.Roles is not null };
        if (tools.IsPublishedTo(lookup.RequiredRoles, rolePrincipal)) return (null, lookup);

        var reason = _options.McpCallerPrincipal is null ? MentorActionReasons.Withheld
            : rolePrincipal?.Identity?.IsAuthenticated == true ? MentorActionReasons.RoleMissing
            : MentorActionReasons.LoginRequired;
        Audit(toolName, caller, reason == MentorActionReasons.Withheld ? "withheld" : "denied-role", started);
        Decide(context, lookup, toolName, rolePrincipal, MentorActionOutcome.Denied, reason);
        return (Error($"Unknown tool: '{toolName}'. This server does not offer it; tools/list names the tools it does."), lookup);
    }

    /// <summary>
    /// Whether the governor's answer about the confirmation of a call (story 1547) lets it run here, where nobody can
    /// confirm it: the lookup, with whether the answer changed that from the code's, or the refusal — withheld, with its
    /// last decision and audit line, and the words of an unknown tool (BUG-101). A governor that fails stops the call.
    /// </summary>
    private async ValueTask<(CallToolResult? Refused, MentorAgentMcpToolCollection.Lookup Lookup)> GovernedConfirmationAsync(
        RequestContext<CallToolRequestParams> context, MentorAgentMcpToolCollection.Lookup lookup, string toolName,
        ClaimsPrincipal? rolePrincipal, string? caller, long started, CancellationToken cancellationToken)
    {
        GovernorConfirmation answer;
        await using (var scope = RequestScope.For(context.Services, services))
        {
            try
            {
                answer = await governance!.ConfirmationAsync(PolicyRequest(MentorActionStage.Call, toolName,
                    lookup.Description, lookup.RequiredRoles, rolePrincipal ?? context.User, context.Server?.SessionId,
                    context.JsonRpcRequest?.Id.ToString(), context.Params?.Arguments, scope.Services,
                    _options.RequireConfirmation && lookup.CodeConfirms), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Audit(toolName, caller, "cancelled", started);
                Decide(context, lookup, toolName, rolePrincipal, MentorActionOutcome.Stopped, MentorActionReasons.Stop);
                throw;
            }
        }

        if (answer.Failed)   // the governance logged it
        {
            Audit(toolName, caller, "governor-failed", started);
            Decide(context, lookup, toolName, rolePrincipal, MentorActionOutcome.PolicyFailed, MentorActionReasons.GovernorFailed,
                exceptionType: answer.ExceptionType);
            return (Error(Text("action.stopped.rule.failed", "Stopped: the check before this action could not be completed.")), lookup);
        }

        var asks = Asks(answer.Choice, lookup.CodeConfirms);
        lookup = lookup with { ConfirmationFromOutside = asks != lookup.CodeConfirms };
        if (!asks) return (null, lookup);

        Audit(toolName, caller, "withheld", started);
        Decide(context, lookup, toolName, rolePrincipal, MentorActionOutcome.Denied, MentorActionReasons.Withheld);
        return (Error($"Unknown tool: '{toolName}'. This server does not offer it; tools/list names the tools it does."), lookup);
    }

    /// <summary>
    /// Whether a call asks for a confirmation, by the governor's answer (story 1547): what it requires, or the code's —
    /// declared, by default or by the <c>RequiresApproval</c> predicate, whatever <c>RequireConfirmation</c> says, for
    /// nobody can confirm one here.
    /// </summary>
    private static bool Asks(MentorConfirmationChoice choice, bool codeConfirms)
        => choice == MentorConfirmationChoice.Required || (choice == MentorConfirmationChoice.Code && codeConfirms);

    /// <summary>
    /// <paramref name="tool"/> as <c>tools/list</c> shows it with the governor's description (story 1548): a copy, so the
    /// tool the server built keeps the code's, with every other field — the name, the schemas, the annotations — as it
    /// is, those a later version of the protocol adds included.
    /// </summary>
    private static Tool Described(Tool tool, string description)
    {
        var copy = System.Text.Json.JsonSerializer.Deserialize<Tool>(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(tool, ModelContextProtocol.McpJsonUtilities.DefaultOptions),
            ModelContextProtocol.McpJsonUtilities.DefaultOptions)!;
        copy.Description = description;
        return copy;
    }

    /// <summary>
    /// Story 155 — the tenant of this request, asked of the application's resolver about the caller the roles are decided
    /// for, in the request's scope; null with a single tenant, where there is nothing to ask. The handler enters it itself:
    /// an async-local value set in here would end with this method.
    /// </summary>
    private async Task<(bool Ok, string? Id, string? Failure)?> TenantAsync(
        ClaimsPrincipal? caller, IServiceProvider? requestServices, CancellationToken cancellationToken)
    {
        var resolver = services.GetService<IMentorTenantResolver>();
        if (MentorTenantContext.IsSingleTenant(resolver)) return null;

        await using var scope = RequestScope.For(requestServices, services);
        return await MentorTenantContext.AskAsync(resolver!, caller, MentorActionSurface.Mcp, scope.Services, cancellationToken);
    }

    /// <summary>The localized text, in the language of the application, or the English one without a localizer.</summary>
    private string Text(string key, string english) => localizer?.Get(key) ?? english;

    /// <summary>What a policy is asked about a tool of this server.</summary>
    private static MentorActionRequest PolicyRequest(MentorActionStage stage, string toolName, string? description,
        string[] roles, ClaimsPrincipal? user, string? sessionId, string? callId,
        IDictionary<string, System.Text.Json.JsonElement>? arguments, IServiceProvider scopeServices,
        bool requiresConfirmation = false)
        => new()
        {
            Stage         = stage,
            ToolName      = toolName,
            Description   = string.IsNullOrWhiteSpace(description) ? null : description,
            Arguments     = stage == MentorActionStage.Offer ? null
                : new System.Collections.ObjectModel.ReadOnlyDictionary<string, object?>(
                    arguments?.ToDictionary(a => a.Key, a => (object?)a.Value) ?? []),
            User          = user?.Identity?.IsAuthenticated == true ? user : null,
            Surface       = MentorActionSurface.Mcp,
            SessionId     = sessionId is { Length: > 0 } ? "mcp#" + Fingerprint(sessionId) : null,
            CallId        = callId,
            RequiredRoles = roles,
            RequiresConfirmation = requiresConfirmation,
            Services      = scopeServices,
        };

    /// <summary>
    /// The services of this request's scope, for the policies — never the application's root services (story 311):
    /// the SDK's, which it creates for every request; the HTTP request's when it gives none; else a scope of the
    /// call's own, closed after it.
    /// </summary>
    private sealed class RequestScope : IAsyncDisposable
    {
        private readonly AsyncServiceScope? _owned;

        private RequestScope(IServiceProvider services, AsyncServiceScope? owned)
        {
            Services = services;
            _owned   = owned;
        }

        internal IServiceProvider Services { get; }

        internal static RequestScope For(IServiceProvider? requestServices, IServiceProvider root)
        {
            if (requestServices is not null && !ReferenceEquals(requestServices, root))
                return new(requestServices, null);

            if (root.GetService<IHttpContextAccessor>()?.HttpContext?.RequestServices is { } http && !ReferenceEquals(http, root))
                return new(http, null);

            var owned = root.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
            return new(owned.ServiceProvider, owned);
        }

        public ValueTask DisposeAsync() => _owned?.DisposeAsync() ?? ValueTask.CompletedTask;
    }

    /// <summary>
    /// One decision for the action observers, and on the call's span. The session is a fingerprint of the MCP
    /// session id, never the id: a client sends that id to resume its session, and an audit trail would hand it
    /// to whoever reads it.
    /// </summary>
    private void Decide(RequestContext<CallToolRequestParams> context, MentorAgentMcpToolCollection.Lookup lookup,
        string toolName, ClaimsPrincipal? rolePrincipal, MentorActionOutcome outcome, string reason,
        Activity? activity = null, string? exceptionType = null, string? detail = null)
    {
        var failed = outcome is MentorActionOutcome.Failed or MentorActionOutcome.PolicyFailed;
        if (activity is not null)
        {
            activity.SetTag("mentoragent.tool", toolName);
            activity.SetTag("mentoragent.tool.outcome", outcome switch
            {
                MentorActionOutcome.Executed     => "executed",
                MentorActionOutcome.Failed       => "failed",
                MentorActionOutcome.Stopped      => "stopped",
                MentorActionOutcome.Denied       => "denied",
                MentorActionOutcome.Blocked      => "blocked",
                MentorActionOutcome.PolicyFailed => "policy-failed",
                _                                => "allowed",
            });
            activity.SetTag("mentoragent.tool.reason", reason);
            activity.SetTag("mentoragent.tool.confirmation", reason == MentorActionReasons.NobodyToConfirm ? "unavailable" : "none");
            activity.SetTag("mentoragent.surface", "mcp");
            if (failed)
                activity.SetStatus(ActivityStatusCode.Error, exceptionType ?? reason);
        }

        if (decisions is not { HasObservers: true }) return;

        var user = rolePrincipal ?? context.User;
        decisions.Publish(new MentorActionDecision
        {
            Outcome       = outcome,
            ReasonCode    = reason,
            ToolName      = toolName,
            DisplayName   = string.IsNullOrWhiteSpace(lookup.Description) ? toolName : lookup.Description,
            Surface       = MentorActionSurface.Mcp,
            SessionId     = context.Server?.SessionId is { Length: > 0 } session ? "mcp#" + Fingerprint(session) : null,
            CallId        = context.JsonRpcRequest?.Id.ToString(),
            User          = user?.Identity?.IsAuthenticated == true ? user : null,
            RequiredRoles = lookup.RequiredRoles,
            RolesFromOutside = lookup.RolesFromOutside,
            ConfirmationFromOutside = lookup.ConfirmationFromOutside,
            Timestamp     = decisions.Time.GetUtcNow(),
            ExceptionType = failed ? exceptionType : null,
            Detail        = detail,
        });
    }

    /// <summary>
    /// The caller's stable id for the rate limit — <c>NameIdentifier</c>, else <c>sub</c> or <c>oid</c> (a
    /// JwtBearer principal with <c>MapInboundClaims = false</c> has no <c>NameIdentifier</c>), else the
    /// name — or null when there is none, never a constant shared by every signed-in caller.
    /// </summary>
    private static string? CallerId(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true) return null;

        var id = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
                 ?? principal.FindFirst("sub")?.Value
                 ?? principal.FindFirst("oid")?.Value
                 ?? principal.Identity?.Name;
        return string.IsNullOrEmpty(id) ? null : "user:" + id;
    }

    /// <summary>
    /// A caller with no id is keyed by its remote address when the host exposes it
    /// (<c>IHttpContextAccessor</c>); behind a proxy that is the proxy unless the host applies forwarded
    /// headers. Without one, every such caller shares one allowance: never the MCP session, which costs
    /// nothing to open and would hand out a fresh allowance on every <c>initialize</c>.
    /// </summary>
    private static string? RemoteAddress(IServiceProvider? services)
        => services?.GetService(typeof(IHttpContextAccessor))
               is IHttpContextAccessor { HttpContext.Connection.RemoteIpAddress: { } address }
            ? "ip:" + address
            : null;

    /// <summary>
    /// One Information line per call. The caller is a fingerprint, never the id itself (often an e-mail
    /// address); arguments and results are never logged.
    /// </summary>
    private void Audit(string? tool, string? caller, string outcome, long started)
    {
        if (!logger.IsEnabled(LogLevel.Information)) return;

        // The name is the caller's text: no control character reaches the log (a newline would forge a line).
        var shown = tool is null ? "(none)" : new string([.. tool.Take(64).Select(c => char.IsControl(c) ? '?' : c)])
                                               + (tool.Length > 64 ? "…" : "");
        logger.LogInformation(
            "[MentorAgent:MCP] tools/call '{Tool}' by {Caller}: {Outcome} in {Ms} ms.",
            shown, caller is null ? "anonymous" : "caller#" + Fingerprint(caller), outcome,
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    private static string Fingerprint(string value)
        => Convert.ToHexString(
               System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))[..10];

    private static CallToolResult Error(string text)
        => new() { IsError = true, Content = [new TextContentBlock { Text = text }] };
}
