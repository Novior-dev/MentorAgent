using System.Net.Http.Headers;

namespace Novior.MentorAgent.Blazor.Services;

/// <summary>
/// The client's own HTTP calls to the MentorAgent server — answering a confirmation, Stop — sent
/// to where the hub is, with the hub's credential.
///
/// <para>
/// They used to go through the host's <see cref="HttpClient"/> with a relative URL and nothing else.
/// Two things followed, both silent (found by the rc.11 README audit): in the cross-origin layout this
/// package documents, the host's client points at its own static origin unless the host thinks to
/// change it; and even pointed at the server, it carries no bearer token, while
/// <c>/mentor/approve</c> and <c>/mentor/cancel</c> check that the caller owns the connection — so a
/// signed-in user's confirmations and Stop were answered 401, the banner had already closed, and the
/// turn waited for its timeout. The tour client already resolved its URL against the hub's origin;
/// these now do the same, and send what <see cref="MentorAgentBlazorOptions.AccessTokenProvider"/>
/// gives the hub.
/// </para>
/// </summary>
internal static class MentorServerRequests
{
    public static async Task<HttpResponseMessage> PostAsync(
        HttpClient http, MentorAgentBlazorOptions? options, string pathAndQuery, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Resolve(options, pathAndQuery));

        if (options?.AccessTokenProvider is { } provider && await provider() is { Length: > 0 } token)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return await http.SendAsync(request, ct);
    }

    /// <summary>
    /// The hub's origin when <see cref="MentorAgentBlazorOptions.HubUrl"/> is an absolute http(s) URL;
    /// otherwise relative, resolved by the host's <see cref="HttpClient.BaseAddress"/> as before
    /// (same-origin hosting). The scheme check matters: in WebAssembly, as on Linux, "/mentor-hub"
    /// parses as an absolute <c>file:///</c> URI.
    /// </summary>
    internal static Uri Resolve(MentorAgentBlazorOptions? options, string pathAndQuery)
        => options is not null
           && Uri.TryCreate(options.HubUrl, UriKind.Absolute, out var hub)
           && (hub.Scheme == Uri.UriSchemeHttp || hub.Scheme == Uri.UriSchemeHttps)
            ? new Uri(new Uri(hub.GetLeftPart(UriPartial.Authority)), pathAndQuery)
            : new Uri(pathAndQuery, UriKind.Relative);
}
