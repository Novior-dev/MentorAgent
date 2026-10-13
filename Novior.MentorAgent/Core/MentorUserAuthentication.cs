using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace Novior.MentorAgent.Core;

/// <summary>
/// Who called through a MentorAgent transport — the hub, <c>/mentor/chat</c>, A2A — for this scope. Set by
/// the transport; <see langword="null"/> in a Blazor circuit and anywhere no transport runs.
/// </summary>
internal sealed class MentorTransportUser
{
    internal ClaimsPrincipal? User { get; set; }
}

/// <summary>
/// The current HTTP request's principal, supplied by Novior.MentorAgent.Server — the core has no HTTP of its own.
/// </summary>
internal interface IMentorHttpUser
{
    ClaimsPrincipal? User { get; }
}

/// <summary>
/// Where MentorAgent reads the current user: roles (<c>RequiredRoles</c>), memory, <c>RateLimitPerUser</c>,
/// the app context and RAG requests (BUG-131).
///
/// <para>
/// Up to rc.14 they asked the host's <see cref="AuthenticationStateProvider"/> directly. In a Blazor Web App
/// that is Blazor's, which works only inside a circuit and throws everywhere else; Novior.MentorAgent.Server's
/// bridge was registered with <c>TryAdd</c> and lost to it. So on the hub, on <c>/mentor/chat</c> and over
/// A2A every role-gated action was refused with an error logged, and memory and the rate limit followed a
/// connection instead of a person.
/// </para>
///
/// <para>
/// The host's registration is left alone — replacing it would depend on the order of the registrations and
/// would put MentorAgent between Blazor and its circuits. The order here is:
/// <list type="number">
///   <item>the caller a MentorAgent transport recorded for this scope (<see cref="MentorTransportUser"/>):
///     on our own transports the host's provider is not asked at all, so one that answers "anonymous"
///     outside a circuit cannot hide the caller either;</item>
///   <item>the host's provider — a Blazor circuit, where no transport runs;</item>
///   <item>the HTTP request's principal, when that provider throws <see cref="InvalidOperationException"/>
///     (Blazor outside a circuit) or the host has none.</item>
/// </list>
/// </para>
/// </summary>
internal sealed class MentorUserAuthentication : AuthenticationStateProvider
{
    private static readonly AuthenticationState Anonymous = new(new ClaimsPrincipal(new ClaimsIdentity()));

    private readonly MentorTransportUser _transport;
    private readonly AuthenticationStateProvider? _host;
    private readonly IMentorHttpUser? _http;

    private MentorUserAuthentication(MentorTransportUser transport, AuthenticationStateProvider? host, IMentorHttpUser? http)
    {
        _transport = transport;
        _host      = host;
        _http      = http;
    }

    /// <summary>
    /// The provider MentorAgent reads in this scope, or <see langword="null"/> when the host has no
    /// authentication at all: no <see cref="AuthenticationStateProvider"/> and no Novior.MentorAgent.Server.
    /// </summary>
    internal static AuthenticationStateProvider? For(IServiceProvider services)
    {
        var host = services.GetService<AuthenticationStateProvider>();
        var http = services.GetService<IMentorHttpUser>();
        return host is null && http is null
            ? null
            : new MentorUserAuthentication(services.GetRequiredService<MentorTransportUser>(), host, http);
    }

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        if (_transport.User is { } caller)
            return new AuthenticationState(caller);

        if (_host is not null)
        {
            try
            {
                return await _host.GetAuthenticationStateAsync();
            }
            catch (InvalidOperationException) when (_http?.User is { } request)
            {
                // Blazor's provider outside a circuit: "Do not call GetAuthenticationStateAsync outside of
                // the DI scope for a Razor component". The request still says who is calling. With no
                // request either, the exception goes on, and the readers handle it as they did in rc.14.
                return new AuthenticationState(request);
            }
        }

        return _http?.User is { } user ? new AuthenticationState(user) : Anonymous;
    }
}
