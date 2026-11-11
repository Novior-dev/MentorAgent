using Novior.MentorAgent.Abstractions.Models;

namespace Novior.MentorAgent.Abstractions.Interfaces;

/// <summary>
/// Names the tenant a request belongs to, so that one instance of the application can serve more than one company. Without
/// one, MentorAgent serves a single tenant, as it always has: <see cref="IMentorTenant.Id"/> is <see langword="null"/> and
/// nothing changes.
///
/// <para>
/// Register it with <c>AddMentorTenantResolver&lt;T&gt;()</c>. There is one per application: a second registration
/// throws. It is a singleton: what it needs from the request's scope it resolves from
/// <see cref="MentorTenantRequest.Services"/>.
/// </para>
///
/// <para>
/// It is asked once for every unit of work, before anything else is done for it: when a turn starts — in a Blazor circuit,
/// on the hub, on <c>/mentor/chat</c> and in a task from another agent — and for every <c>tools/list</c> and
/// <c>tools/call</c> of the MCP server, whose calls share no scope. Its answer is what <see cref="IMentorTenant.Id"/>
/// reads in that scope, or during that call.
/// </para>
///
/// <para>
/// <b>A request is never served without a tenant.</b> A resolver that throws, or answers <see langword="null"/>, an empty
/// string or white space, refuses the request: the turn is refused before the model is called, a <c>tools/call</c> is
/// answered with an error and a <c>tools/list</c> lists nothing. Going on as the single tenant would put the request in the
/// data every tenant shares. A turn whose tenant differs from the one the scope's first turn had is refused as well: a
/// conversation stays with the tenant it began with. The exception's message is never logged, only its type.
/// </para>
/// </summary>
public interface IMentorTenantResolver
{
    /// <summary>The tenant <paramref name="request"/> belongs to: an identifier MentorAgent uses as it is.</summary>
    ValueTask<string?> ResolveAsync(MentorTenantRequest request, CancellationToken cancellationToken);
}
