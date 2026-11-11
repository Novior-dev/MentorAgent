namespace Novior.MentorAgent.Abstractions.Interfaces;

/// <summary>
/// The tenant of the current request, as the <see cref="IMentorTenantResolver"/> named it. Scoped: inject it into an action,
/// a policy, or a service the governor resolves from <c>MentorActionRequest.Services</c>.
/// </summary>
public interface IMentorTenant
{
    /// <summary>
    /// The tenant's identifier; <see langword="null"/> when the application registers no
    /// <see cref="IMentorTenantResolver"/> and serves a single tenant.
    /// </summary>
    /// <remarks>
    /// In a turn, the tenant resolved when the turn started; during a call to the MCP server, the call's own. With a
    /// resolver registered, reading it in a scope where nothing was resolved yet — before the first turn of a circuit,
    /// say, or in the build of <c>WarmUpAtStartup</c>, which serves nobody — throws <see cref="InvalidOperationException"/>:
    /// it never reads as the single tenant. <see cref="GetIdAsync"/> resolves it there.
    /// </remarks>
    string? Id { get; }

    /// <summary>
    /// The tenant's identifier, as <see cref="Id"/>; in a scope that had no turn yet — a page that only reads the
    /// memory or the dashboard — it asks the <see cref="IMentorTenantResolver"/> first, with the scope's user, under the
    /// same rules as a turn.
    /// </summary>
    /// <exception cref="InvalidOperationException">The resolver names no tenant for this scope, or another than its own.</exception>
    ValueTask<string?> GetIdAsync(CancellationToken cancellationToken = default);
}
