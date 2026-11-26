using System.Reflection;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;

namespace Novior.MentorAgent.Server.Endpoints;

/// <summary>
/// Story 1641 — <c>GET /mentor/agent.js</c>, the script a front end that is not Blazor (React, Angular) loads with
/// <c>&lt;script type="module"&gt;</c> to run the recipes the hub asks for (<c>recipe.front-end</c>): the recipes' runner
/// of Novior.MentorAgent.Abstractions, the same file the Blazor widget imports, and the part that ties it to the front
/// end's own hub connection. One module, embedded in this assembly: the front end needs no package of its own.
/// <para>
/// A module from another origin runs only with CORS, so the script takes the CORS of the hub (<c>recipe.front-end-cors</c>):
/// the host's <c>app.UseCors(…)</c> applies to it as to the hub, and a policy the hub has from <c>configureTransports</c>
/// (<c>RequireCors</c>) is copied onto it, and nothing else of those conventions. It is the library's code and no data
/// of the application, so it is served to anyone, with no authorization: a module script is loaded without the
/// front end's bearer token.
/// </para>
/// </summary>
internal static class MentorClientScript
{
    /// <summary>Where the script is served, under the application's base path.</summary>
    internal const string Path = "/mentor/agent.js";

    private const string RunnerResource = "Novior.MentorAgent.Server.MentorRecipeRunner.js";
    private const string ClientResource = "Novior.MentorAgent.Server.MentorAgentClient.js";

    /// <summary>
    /// The version the README's address carries (<c>?v=</c>, as the widget's assets since BUG-209): the package's,
    /// without the commit. An address with it is cached for a year; any other is checked with the server each time.
    /// </summary>
    internal static string Version { get; } =
        (typeof(MentorClientScript).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(MentorClientScript).Assembly.GetName().Version?.ToString(3) ?? "0").Split('+')[0];

    private static readonly Lazy<(byte[] Body, string ETag)> Built = new(Build);

    /// <summary>The script as served: the runner, then the part for the front end.</summary>
    internal static string Text => System.Text.Encoding.UTF8.GetString(Built.Value.Body);

    private static (byte[], string) Build()
    {
        var text = Read(RunnerResource) + "\r\n" + Read(ClientResource);
        var body = System.Text.Encoding.UTF8.GetBytes(text);
        return (body, "\"" + Convert.ToHexString(SHA256.HashData(body))[..32] + "\"");
    }

    private static string Read(string name)
    {
        using var stream = typeof(MentorClientScript).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"The resource {name} is missing from Novior.MentorAgent.Server.");
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>Maps the script; <paramref name="transports"/> are the hub's conventions, of which it takes the CORS.</summary>
    internal static IEndpointConventionBuilder Map(IEndpointRouteBuilder endpoints, Action<IEndpointConventionBuilder>? transports)
    {
        var script = endpoints.MapGet(Path, Serve).AllowAnonymous().ExcludeFromDescription();
        if (transports is not null)
        {
            var recorded = new Recorder();
            transports(recorded);
            script.Add(builder =>
            {
                foreach (var cors in recorded.Cors())
                    builder.Metadata.Add(cors);
            });
        }
        return script;
    }

    private static IResult Serve(HttpContext http)
    {
        var (body, etag) = Built.Value;
        var headers = http.Response.Headers;
        headers.ETag = etag;
        headers.XContentTypeOptions = "nosniff";
        headers.CacheControl = http.Request.Query["v"] == Version ? "public, max-age=31536000, immutable" : "no-cache";
        if (http.Request.Headers.IfNoneMatch == etag)
            return Results.StatusCode(StatusCodes.Status304NotModified);
        return Results.Bytes(body, "text/javascript; charset=utf-8");
    }

    /// <summary>
    /// The hub's conventions, run on an endpoint of their own: of what they add, the script takes only the CORS
    /// metadata, so an authorization the hub requires does not keep the script from a front end.
    /// </summary>
    private sealed class Recorder : IEndpointConventionBuilder
    {
        private readonly List<Action<EndpointBuilder>> _conventions = [];
        private readonly List<Action<EndpointBuilder>> _finally = [];

        public void Add(Action<EndpointBuilder> convention) => _conventions.Add(convention);

        public void Finally(Action<EndpointBuilder> finallyConvention) => _finally.Add(finallyConvention);

        internal IEnumerable<object> Cors()
        {
            var probe = new RouteEndpointBuilder(_ => Task.CompletedTask, RoutePatternFactory.Parse(Path), 0);
            foreach (var convention in _conventions) convention(probe);
            foreach (var convention in _finally) convention(probe);
            return probe.Metadata.Where(m => m is ICorsMetadata).ToList();
        }
    }
}
