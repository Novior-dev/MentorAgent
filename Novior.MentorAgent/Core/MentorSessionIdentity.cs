namespace Novior.MentorAgent.Core;

/// <summary>
/// A stable id for the current session — one Blazor Server circuit, one SignalR connection, one
/// application run — generated when the scope is created and gone when it ends.
///
/// <para>
/// It exists so an unauthenticated visitor has an identity of their own
/// (<see cref="Options.MentorAnonymousIdentity.PerSession"/>): memory and the rate-limit counter are
/// keyed by it instead of by the literal <c>"anonymous"</c> that every visitor used to share.
/// </para>
///
/// <para>
/// Never used for a signed-in user: their claims are the identity, and keying by session would give
/// the same person a different memory in every tab.
/// </para>
/// </summary>
internal sealed class MentorSessionIdentity
{
    /// <summary>Opaque, per scope, and never shown to the model or the user.</summary>
    internal string Id { get; } = Guid.NewGuid().ToString("N");
}
