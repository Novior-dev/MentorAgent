using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace Novior.MentorAgent.Scanner;

/// <summary>
/// Story 1636, slice B — puts the scan's user in the application's own pipeline, first, in <c>Development</c> only. In
/// any other environment it adds nothing: <c>/_mentor/scan/start</c> is not mapped at all (<c>page.scan-user</c>).
/// </summary>
internal sealed class MentorUiScanStartupFilter(IHostEnvironment environment, MentorUiScanGate gate) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        if (!environment.IsDevelopment()) return next;
        return app =>
        {
            var middleware = new MentorUiScanMiddleware(gate);
            app.Use((context, following) => middleware.InvokeAsync(context, following));
            next(app);
        };
    }
}

/// <summary>
/// Story 1636, slice B — the door of the scan's user. A request from the loopback to <c>/_mentor/scan/start</c> with
/// the one-time token in the <c>X-MentorAgent-Scan-Token</c> header burns the token and gets the session cookie,
/// <c>HttpOnly</c>; anything else there is a 404. A request from the loopback with the open session's cookie is the
/// scan's user, set before the application's authentication, which keeps it when it finds no user of its own.
/// </summary>
internal sealed class MentorUiScanMiddleware(MentorUiScanGate gate)
{
    internal async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var local = IsLoopback(context.Connection.RemoteIpAddress);

        if (context.Request.Path.Equals(MentorUiScanGate.StartPath, StringComparison.OrdinalIgnoreCase))
        {
            var session = local && HttpMethods.IsGet(context.Request.Method)
                ? gate.Redeem(context.Request.Headers[MentorUiScanGate.TokenHeader].ToString())
                : null;
            if (session is null)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            context.Response.Cookies.Append(MentorUiScanGate.CookieName, session, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                Secure = context.Request.IsHttps,
                Path = "/",
                IsEssential = true,
            });
            context.Response.Headers.CacheControl = "no-store";
            context.Response.ContentType = "text/plain";
            await context.Response.WriteAsync("MentorAgent scan user signed in.").ConfigureAwait(false);
            return;
        }

        if (local && gate.UserFor(context.Request.Cookies[MentorUiScanGate.CookieName]) is { } user)
            context.User = user;

        await next(context).ConfigureAwait(false);
    }

    private static bool IsLoopback(IPAddress? address)
        => address is not null && IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
}
