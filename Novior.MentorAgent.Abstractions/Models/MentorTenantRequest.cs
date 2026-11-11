using System.Security.Claims;

namespace Novior.MentorAgent.Abstractions.Models;

/// <summary>
/// What an <see cref="Interfaces.IMentorTenantResolver"/> is asked about: one request, from one surface. More properties may
/// be added.
/// </summary>
public sealed record MentorTenantRequest
{
    /// <summary>
    /// The signed-in user, or <see langword="null"/> when nobody is. On the MCP server, the caller
    /// <c>McpCallerPrincipal</c> resolves, else the request's user.
    /// </summary>
    public ClaimsPrincipal? User { get; init; }

    /// <summary>Where the request comes from.</summary>
    public required MentorActionSurface Surface { get; init; }

    /// <summary>
    /// The services of the request's scope — the circuit, the hub connection, the request, the A2A task; on the MCP server,
    /// the request's — for a scoped service of the application, an <c>IHttpContextAccessor</c> say. Never the application's
    /// root services.
    /// </summary>
    public required IServiceProvider Services { get; init; }
}
