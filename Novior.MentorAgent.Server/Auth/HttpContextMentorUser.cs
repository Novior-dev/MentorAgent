using System.Security.Claims;
using Novior.MentorAgent.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Novior.MentorAgent.Server.Auth;

/// <summary>
/// The HTTP request's principal, for the core: the A2A handler records it as the caller of the scope it
/// opens, and MentorAgent falls back on it when the host's provider throws outside a Blazor circuit
/// (BUG-131).
/// </summary>
internal sealed class HttpContextMentorUser(IHttpContextAccessor accessor) : IMentorHttpUser
{
    public ClaimsPrincipal? User => accessor.HttpContext?.User;

    /// <summary>Registers it once, from every method that maps a transport running a turn.</summary>
    internal static void Register(IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.TryAddSingleton<IMentorHttpUser, HttpContextMentorUser>();
    }
}
