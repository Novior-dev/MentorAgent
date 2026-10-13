using System.Net.Http.Json;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Blazor.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;

namespace Novior.MentorAgent.Blazor.Services;

/// <summary>
/// WASM side of the onboarding tour (roadmap #18). The steps are generated from discovery data —
/// registered pages, available tools — which lives on the server, so the client fetches them from
/// <c>GET /mentor/tour</c> rather than reproducing anything locally. That is the same rule the
/// hosted-tools badge follows: the server is the single source of truth, and a client that mirrors
/// server state by hand eventually shows something the server does not agree with.
/// </summary>
internal sealed class WasmMentorTour : IMentorTour
{
    private readonly HttpClient _http;
    private readonly string _url;
    private readonly ILogger<WasmMentorTour> _logger;

    private IReadOnlyList<MentorTourStep>? _cached;

    public WasmMentorTour(
        NavigationManager navigation,
        MentorAgentBlazorOptions options,
        ILogger<WasmMentorTour> logger)
    {
        // One HttpClient for the lifetime of the app. In WebAssembly this is a thin wrapper over
        // fetch(), so the usual socket-exhaustion argument for IHttpClientFactory does not apply,
        // and requiring the host to have registered an HttpClient with the right BaseAddress would
        // be one more thing to get wrong.
        _http = new HttpClient { BaseAddress = new Uri(navigation.BaseUri) };
        _url = ResolveTourUrl(options, navigation);
        _logger = logger;
    }

    /// <summary>
    /// The tour is served by the MentorAgent server, which is the one that knows the pages and
    /// tools — so the URL has to resolve against the *hub's* origin, not the client app's.
    /// </summary>
    /// <remarks>
    /// A relative <c>/mentor/tour</c> against the client's own base address is the wrong request
    /// whenever the WASM app and the server are on different origins, which is the documented
    /// deployment for this package. It does not fail loudly either: a SPA host answers any unknown
    /// path with <c>index.html</c> and HTTP 200, so the client received a page, failed to parse it
    /// as JSON, and the tour silently never appeared.
    /// </remarks>
    private static string ResolveTourUrl(MentorAgentBlazorOptions options, NavigationManager navigation)
    {
        // An explicit absolute TourUrl always wins — the host has said exactly what it wants.
        if (IsHttpUrl(options.TourUrl, out var absolute))
            return absolute.ToString();

        // Same-origin hosting (Blazor Auto, or a server that also serves the client): the app's own
        // base address is already the right one.
        if (!IsHttpUrl(options.HubUrl, out var hub))
            return options.TourUrl;

        return new Uri(new Uri(hub.GetLeftPart(UriPartial.Authority)), options.TourUrl).ToString();
    }

    /// <summary>
    /// True only for an absolute http/https URL. The scheme check is the point: in WebAssembly, as on
    /// Linux, <c>Uri.TryCreate("/mentor/tour", UriKind.Absolute, …)</c> succeeds and yields
    /// <c>file:///mentor/tour</c>, so testing "is it absolute" alone treats the default relative
    /// path as an explicit override and requests it from the local filesystem.
    /// </summary>
    private static bool IsHttpUrl(string? value, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return false;
        uri = parsed;
        return true;
    }

    public async Task<IReadOnlyList<MentorTourStep>> GetStepsAsync(CancellationToken ct = default)
    {
        if (_cached is { } ready) return ready;

        try
        {
            using var response = await _http.GetAsync(_url, ct);
            response.EnsureSuccessStatusCode();

            // A SPA host answers an unknown path with index.html and a 200, so "it returned
            // something" proves nothing. Name the real problem instead of letting the JSON reader
            // report a stray '<' at position 0, which says nothing about what to fix.
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (!string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "[MentorAgent:Tour] {Url} answered with '{MediaType}' instead of JSON — that URL is "
                    + "not the MentorAgent server. Point HubUrl at the server (or set TourUrl to an "
                    + "absolute URL) and make sure the server enables CORS for this origin.",
                    _url, mediaType ?? "no content type");
                return _cached = [];
            }

            var steps = await response.Content.ReadFromJsonAsync<List<MentorTourStep>>(ct);
            return _cached = steps ?? [];
        }
        catch (Exception ex)
        {
            // No tour is a missing nicety, not a broken app: log it and let the widget open normally.
            _logger.LogWarning(ex, "[MentorAgent:Tour] Could not fetch the tour from {Url}.", _url);
            return _cached = [];
        }
    }
}
