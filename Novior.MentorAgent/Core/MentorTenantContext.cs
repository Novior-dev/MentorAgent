using System.Security.Claims;

namespace Novior.MentorAgent.Core;

/// <summary>
/// The resolver <c>AddMentorAgent</c> registers when the application registers none (story 155): one tenant, as before.
/// Never asked: <see cref="MentorTenantContext"/> knows it by its type.
/// </summary>
internal sealed class MentorSingleTenantResolver : IMentorTenantResolver
{
    public ValueTask<string?> ResolveAsync(MentorTenantRequest request, CancellationToken cancellationToken)
        => ValueTask.FromResult<string?>(null);
}

/// <summary>What became of a turn's tenant.</summary>
internal enum MentorTenantOutcome
{
    /// <summary>Resolved, and the same as the scope's: the turn goes on.</summary>
    Resolved,

    /// <summary>The resolver threw, or named no tenant: the turn is refused.</summary>
    Failed,

    /// <summary>Another tenant than the one the scope's first turn had: the turn is refused (story 155, C1).</summary>
    Changed,
}

/// <summary>
/// The tenant of this scope — the circuit, the hub connection, the request, the A2A task — and of the MCP call being served
/// (story 155). The orchestrator resolves it when a turn starts; the MCP handler, for every request, into an async-local
/// value: the MCP server's tool classes are resolved once, from a scope every call shares. A scope that had no turn yet
/// — the memory's "forget me", the dashboard, a restore — resolves it when it is first asked (story 156).
/// </summary>
internal sealed class MentorTenantContext(IMentorTenantResolver resolver, IServiceProvider services) : IMentorTenant
{
    /// <summary>The tenant of the turn or the MCP call this flow serves; a box, so that a call's null is not "no call".</summary>
    private static readonly AsyncLocal<Box?> s_call = new();

    private Box? _scope;

    private sealed record Box(string? Id);

    /// <summary>Whether the application registered no resolver of its own: one tenant, nothing to ask.</summary>
    internal bool SingleTenant => IsSingleTenant(resolver);

    internal static bool IsSingleTenant(IMentorTenantResolver? resolver) => resolver is null or MentorSingleTenantResolver;

    /// <summary>Whether a turn of this scope, or a build before one, resolved its tenant.</summary>
    internal bool IsResolved => _scope is not null;

    public string? Id
        => s_call.Value is { } call ? call.Id
           : _scope is { } scope ? scope.Id
           : SingleTenant ? null
           : throw new InvalidOperationException(
               "No tenant has been resolved in this scope yet: MentorAgent resolves it when a turn starts, and for every " +
               "request to its MCP server.");

    public async ValueTask<string?> GetIdAsync(CancellationToken cancellationToken = default)
    {
        if (s_call.Value is { } call) return call.Id;
        if (_scope is { } scope) return scope.Id;
        if (SingleTenant) return null;

        // The scope's user and surface, as a turn would read them.
        var user = services.GetService<Middleware.MentorAgentUIMiddleware>() is { } ui
            ? await ui.ReadUserAsync(cancellationToken)
            : null;
        var surface = services.GetService<MentorTurnContext>()?.Surface ?? MentorActionSurface.InProcess;
        var (outcome, failure) = await ResolveTurnAsync(user, surface, services, cancellationToken);
        return outcome == MentorTenantOutcome.Resolved
            ? _scope!.Id
            : throw new InvalidOperationException(
                $"The tenant resolver named no tenant ({failure ?? "another tenant than the scope's"}): nothing is served " +
                "for a request without one.");
    }

    /// <summary>
    /// <paramref name="key"/> for <paramref name="tenant"/>'s use only (story 156): <c>tenant:&lt;id&gt;/&lt;key&gt;</c>,
    /// the id escaped so that one with a <c>/</c> cannot pass for another; the key as it is with a single tenant.
    /// </summary>
    internal static string Key(string? tenant, string key)
        => tenant is null ? key : "tenant:" + Uri.EscapeDataString(tenant) + "/" + key;

    /// <summary>
    /// Resolves the tenant of a turn that starts in this scope. The scope keeps the tenant of its first turn: a later turn
    /// with another is <see cref="MentorTenantOutcome.Changed"/>, and the conversation is not handed to it. Stop cancels it.
    /// </summary>
    internal async Task<(MentorTenantOutcome Outcome, string? Failure)> ResolveTurnAsync(
        ClaimsPrincipal? user, MentorActionSurface surface, IServiceProvider services, CancellationToken ct)
    {
        if (SingleTenant)
        {
            _scope ??= new Box(null);
            return (MentorTenantOutcome.Resolved, null);
        }

        var (ok, id, failure) = await AskAsync(resolver, user, surface, services, ct);
        if (!ok) return (MentorTenantOutcome.Failed, failure);
        if (_scope is { } bound && !string.Equals(bound.Id, id, StringComparison.Ordinal))
            return (MentorTenantOutcome.Changed, null);

        _scope = new Box(id);
        return (MentorTenantOutcome.Resolved, null);
    }

    /// <summary>
    /// Asks <paramref name="resolver"/>. Not ok, with the reason, when it throws or names no tenant: <see langword="null"/>,
    /// empty or white space. Only the exception's type is kept, never its message. Stop cancels it.
    /// </summary>
    internal static async Task<(bool Ok, string? Id, string? Failure)> AskAsync(IMentorTenantResolver resolver,
        ClaimsPrincipal? user, MentorActionSurface surface, IServiceProvider services, CancellationToken ct)
    {
        try
        {
            var id = await resolver.ResolveAsync(new MentorTenantRequest
            {
                User     = user?.Identity?.IsAuthenticated == true ? user : null,
                Surface  = surface,
                Services = services,
            }, ct);
            return string.IsNullOrWhiteSpace(id) ? (false, null, "no tenant named") : (true, id, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return (false, null, ex.GetType().Name);
        }
    }

    /// <summary>
    /// The tenant of the turn or the MCP request this flow serves, until the method that entered it returns: the
    /// orchestrator's turn, the MCP request's handler. Synchronous on purpose: set from that method itself, the value stays
    /// in its flow; set inside an awaited method, it would end with that method. Entered only with a resolver.
    /// </summary>
    internal static void Enter(string? id) => s_call.Value = new Box(id);

    /// <summary>
    /// The tenant of the turn or the MCP request this flow serves, for what serves every tenant at once — the dashboard's
    /// figures, the metrics' tag (story 156). <see langword="null"/> with a single tenant, and outside a turn or a request.
    /// </summary>
    internal static string? Ambient => s_call.Value?.Id;
}
