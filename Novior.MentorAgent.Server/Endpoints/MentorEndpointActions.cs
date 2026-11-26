using System.Globalization;
using System.IO.Pipelines;
using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Novior.MentorAgent.Attributes;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Utility;

namespace Novior.MentorAgent.Server.Endpoints;

/// <summary>What <c>WithMentorAction</c> leaves on an endpoint (story 1287).</summary>
internal sealed record MentorEndpointActionMetadata(string? Name, MentorActionBuilder Action);

/// <summary>Where a parameter of an endpoint's handler comes from, when the action runs the endpoint.</summary>
internal enum MentorEndpointParameterSource { Route, Query, Body, NotFromTheModel }

internal sealed record MentorEndpointParameter(ParameterInfo Parameter, string Name, MentorEndpointParameterSource Source);

/// <summary>The endpoint an action of origin <c>endpoint</c> runs: <see cref="MethodDiscoveryInfo.Endpoint"/>.</summary>
internal sealed record MentorEndpointBinding(RouteEndpoint Endpoint, string Verb, IReadOnlyList<MentorEndpointParameter> Parameters)
    : IMentorEndpoint
{
    /// <summary>The verb and the route, as the trace names the endpoint: <c>POST /orders/{id}/cancel</c>.</summary>
    public string Display => Verb + " /" + (Endpoint.RoutePattern.RawText ?? "").TrimStart('/');
}

/// <summary>
/// Story 1287 — reads the application's endpoints declared as actions once the host has started, when every endpoint is
/// mapped, and adds them to the discovery. A declaration the action cannot honour stops the application, naming the
/// endpoint, as a registration in code does (<c>fluent.method-must-exist</c>).
/// </summary>
internal sealed class MentorEndpointActionDiscovery(
    IServiceProvider services,
    MentorDiscoveryService discovery,
    IOptions<MentorOptions> options,
    IHostApplicationLifetime lifetime,
    ILogger<MentorEndpointActionDiscovery> logger) : IHostedService
{
    private static readonly string[] Reads = [HttpMethods.Get, HttpMethods.Head];

    // Once every hosted service has started: a host built with ConfigureWebHost maps its endpoints when the web host
    // starts, which may come after this service.
    public Task StartAsync(CancellationToken cancellationToken)
    {
        lifetime.ApplicationStarted.Register(() =>
        {
            try
            {
                Discover();
            }
            catch (InvalidOperationException ex)
            {
                logger.LogCritical("{Message} The application stops.", ex.Message);
                lifetime.StopApplication();
            }
        });
        return Task.CompletedTask;
    }

    internal void Discover()
    {
        if (services.GetService<EndpointDataSource>() is not { } source) return;

        var isService = services.GetService<IServiceProviderIsService>();
        var actions = new List<MethodDiscoveryInfo>();
        foreach (var endpoint in source.Endpoints.OfType<RouteEndpoint>())
            if (Read(endpoint, isService) is { } action)
                actions.Add(action);

        var names = actions.GroupBy(a => a.ToolName, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (names is not null)
            throw new InvalidOperationException(
                $"[MentorAgent] Two endpoint actions are named '{names.Key}' ({string.Join(", ", names.Select(n => ((MentorEndpointBinding)n.Endpoint!).Display))}). " +
                "Give each its own name with WithMentorAction(name, …).");

        discovery.AddEndpointActions(actions);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private MethodDiscoveryInfo? Read(RouteEndpoint endpoint, IServiceProviderIsService? isService)
    {
        var declared = endpoint.Metadata.GetMetadata<MentorEndpointActionMetadata>();
        var attribute = endpoint.Metadata.GetMetadata<MentorActionAttribute>();
        if (declared is null && attribute is null) return null;

        var at = endpoint.DisplayName ?? endpoint.RoutePattern.RawText ?? "an endpoint";
        var method = endpoint.Metadata.GetMetadata<ControllerActionDescriptor>()?.MethodInfo
                     ?? endpoint.Metadata.GetMetadata<MethodInfo>()
                     ?? throw Stop(at, "its handler cannot be read");

        // endpoint.declared: the model calls the action, the action calls the endpoint with one verb.
        var verbs = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [];
        if (verbs.Count != 1)
            throw Stop(at, $"it answers {(verbs.Count == 0 ? "every verb" : string.Join(", ", verbs))}; an action needs an endpoint with one verb");
        var verb = verbs[0].ToUpperInvariant();

        var name = ToolName(declared?.Name, endpoint, method, at);
        var description = declared?.Action.Description ?? attribute?.Description;
        if (string.IsNullOrWhiteSpace(description))
            throw Stop(at, "it has no description: give it one with Describe(…), or in [MentorAction]");

        var parameters = Parameters(method, endpoint.RoutePattern, isService, at);

        // endpoint.verb-default: GET and HEAD read; the other verbs write, and a write asks for a confirmation when
        // RequireConfirmationForWrites is on, unless the action says it only reads. What is declared wins.
        var o = options.Value;
        var declaredConfirmation = declared is not null ? declared.Action.Confirmation
            : attribute!.RequiresConfirmationDeclared ? attribute.RequiresConfirmation : null;
        var readOnly = (declared?.Action.IsReadOnly ?? attribute!.ReadOnly) || Reads.Contains(verb)
                       || o.IsReadOnlyAction?.Invoke(name) == true;
        var confirmation = declaredConfirmation ?? (o.RequireConfirmationForWrites && !readOnly);

        // endpoint.roles-from-authorization: the roles declared replace the ones of the endpoint's authorization.
        IReadOnlyList<string> declaredRoles = declared?.Action.Roles ?? attribute!.RequiredRoles;

        return new MethodDiscoveryInfo(
            DeclaringType: method.DeclaringType ?? typeof(object),
            Method: method,
            ToolName: name,
            Description: description,
            Category: declared?.Action.CategoryValue ?? attribute?.Category,
            RequiresConfirmation: confirmation,
            ProactiveHint: declared?.Action.ProactiveHint ?? attribute?.ProactiveHint,
            RequiredRoles: declaredRoles.Count > 0 ? [.. declaredRoles] : RolesFromAuthorization(endpoint, at),
            NavigateTo: declared?.Action.Page ?? attribute?.NavigateTo,
            ReadOnly: readOnly && !confirmation,
            IsMentorAction: true,
            ConfirmationDeclared: declaredConfirmation is not null,
            EnabledByDefault: declared?.Action.Enabled ?? attribute!.Enabled,
            Origin: MentorActionOrigins.Endpoint,
            Endpoint: new MentorEndpointBinding(endpoint, verb, parameters));
    }

    /// <summary>
    /// Story 1307, <c>endpoint.roles-from-authorization</c> — the roles of the endpoint's authorization, combined as
    /// <c>UseAuthorization</c> combines it and as the call checks it: its <c>[Authorize]</c>, its policies, the fallback
    /// policy; none with <c>[AllowAnonymous]</c>. The roles of one role requirement are the action's, any one of them
    /// (<c>roles.any-of</c>). Several requirements ask for roles together, which an action's roles cannot say: then none,
    /// with a line in the log, and the endpoint still refuses the call. A policy named and not registered stops the
    /// application, as ASP.NET Core would fail the first request.
    /// </summary>
    private string[] RolesFromAuthorization(RouteEndpoint endpoint, string at)
    {
        if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null) return [];
        if (services.GetService<IAuthorizationPolicyProvider>() is not { } provider) return [];

        var authorizeData = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();
        foreach (var policyName in authorizeData.Select(d => d.Policy).OfType<string>().Where(p => p.Length > 0).Distinct())
            if (provider.GetPolicyAsync(policyName).GetAwaiter().GetResult() is null)
                throw Stop(at, $"its authorization names the policy '{policyName}', which is not registered");

        var policy = AuthorizationPolicy.CombineAsync(provider, authorizeData, endpoint.Metadata.GetOrderedMetadata<AuthorizationPolicy>())
            .GetAwaiter().GetResult();
        var roleSets = (policy?.Requirements ?? []).OfType<RolesAuthorizationRequirement>()
            .Select(r => r.AllowedRoles.Distinct(StringComparer.Ordinal).ToArray())
            .DistinctBy(roles => string.Join('\n', roles.Order(StringComparer.Ordinal)))
            .ToList();
        if (roleSets.Count <= 1) return roleSets.FirstOrDefault() ?? [];

        logger.LogInformation(
            "[MentorAgent] The endpoint {Endpoint} asks for roles together ({Roles}), which an action's roles cannot say: " +
            "the action takes no roles from its authorization, and the endpoint still refuses the call (403) to a user without them.",
            at, string.Join(" and ", roleSets.Select(roles => string.Join(" or ", roles))));
        return [];
    }

    /// <summary>
    /// endpoint.declared: the name declared, then the endpoint's name (<c>WithName</c>), then the handler's method, in
    /// snake_case; a lambda has no name of its own.
    /// </summary>
    private static string ToolName(string? declared, RouteEndpoint endpoint, MethodInfo method, string at)
    {
        if (declared is not null)
            return MentorPageActionTrace.IsActionName(declared) ? declared
                : throw Stop(at, $"the name '{declared}' is not a tool name (letters, digits, _ and -, up to 64)");

        var named = endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName;
        if (!string.IsNullOrWhiteSpace(named)) return MentorUtils.ToSnakeCase(named);

        if (method.Name.Contains('<') || method.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute)))
            throw Stop(at, "its handler is a lambda: name the action with WithMentorAction(name, …) or the endpoint with WithName(…)");

        return MentorUtils.ToSnakeCase(method.Name);
    }

    /// <summary>
    /// endpoint.declared: the model gives route values, query values and one JSON body. Services, the
    /// <see cref="HttpContext"/> and the cancellation token are not the model's; headers, forms and files cannot come
    /// from it, and stop the application.
    /// </summary>
    private static List<MentorEndpointParameter> Parameters(MethodInfo method, RoutePattern pattern,
        IServiceProviderIsService? isService, string at)
    {
        var parameters = new List<MentorEndpointParameter>();
        foreach (var p in method.GetParameters())
        {
            var name = p.Name ?? "";
            var attributes = p.GetCustomAttributes(inherit: true);
            MentorEndpointParameterSource source;

            if (attributes.OfType<IFromRouteMetadata>().FirstOrDefault() is { } route)
            {
                source = MentorEndpointParameterSource.Route;
                name = route.Name ?? name;
            }
            else if (attributes.OfType<IFromQueryMetadata>().FirstOrDefault() is { } query)
            {
                source = MentorEndpointParameterSource.Query;
                name = query.Name ?? name;
            }
            else if (attributes.OfType<IFromBodyMetadata>().Any())
                source = MentorEndpointParameterSource.Body;
            else if (attributes.OfType<IFromServiceMetadata>().Any() || attributes.OfType<FromKeyedServicesAttribute>().Any())
                source = MentorEndpointParameterSource.NotFromTheModel;
            else if (attributes.OfType<IFromHeaderMetadata>().Any() || attributes.OfType<IFromFormMetadata>().Any()
                     || attributes.OfType<AsParametersAttribute>().Any()
                     || typeof(IFormFile).IsAssignableFrom(p.ParameterType) || typeof(IFormFileCollection).IsAssignableFrom(p.ParameterType)
                     || typeof(IFormCollection).IsAssignableFrom(p.ParameterType))
                throw Stop(at, $"its parameter '{p.Name}' comes from a header, a form, a file or [AsParameters], which a model cannot give");
            else if (IsFromTheRequest(p.ParameterType) || isService?.IsService(p.ParameterType) == true)
                source = MentorEndpointParameterSource.NotFromTheModel;
            else if (pattern.GetParameter(name) is not null)
                source = MentorEndpointParameterSource.Route;
            else if (IsSimple(p.ParameterType))
                source = MentorEndpointParameterSource.Query;
            else
                source = MentorEndpointParameterSource.Body;

            parameters.Add(new(p, name, source));
        }

        if (parameters.Count(p => p.Source == MentorEndpointParameterSource.Body) > 1)
            throw Stop(at, "it reads more than one body");

        return parameters;
    }

    private static bool IsFromTheRequest(Type type)
        => type == typeof(HttpContext) || type == typeof(HttpRequest) || type == typeof(HttpResponse)
           || type == typeof(CancellationToken) || type == typeof(ClaimsPrincipal) || type == typeof(Stream)
           || type == typeof(PipeReader);

    private static bool IsSimple(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || t == typeof(Guid)
               || t == typeof(DateTime) || t == typeof(DateTimeOffset) || t == typeof(DateOnly) || t == typeof(TimeOnly)
               || t == typeof(TimeSpan) || t == typeof(Uri);
    }

    private static InvalidOperationException Stop(string endpoint, string why)
        => new($"[MentorAgent] The endpoint {endpoint} is declared as an action, but {why}.");
}

/// <summary>Story 1287 — the tool of an endpoint's action, for the coordinator (<see cref="IMentorEndpointActionRunner"/>).</summary>
internal sealed class MentorEndpointActionRunner(ILogger<MentorEndpointActionFunction> logger) : IMentorEndpointActionRunner
{
    public AIFunction Create(MethodDiscoveryInfo action, IServiceProvider services)
        => new MentorEndpointActionFunction(Described(action), (MentorEndpointBinding)action.Endpoint!, services, logger);

    /// <inheritdoc/>
    public System.Text.Json.JsonElement Schema(MethodDiscoveryInfo action) => Described(action).JsonSchema;

    /// <summary>
    /// The function that describes the action to the model: its name, its description and the schema of its arguments.
    /// It never runs the handler.
    /// </summary>
    private static AIFunction Described(MethodDiscoveryInfo action)
    {
        var binding = (MentorEndpointBinding)action.Endpoint!;
        var fromTheModel = binding.Parameters
            .Where(p => p.Source != MentorEndpointParameterSource.NotFromTheModel)
            .Select(p => p.Parameter)
            .ToHashSet();

        // The schema the model reads, built as for every other action; the parameters the model does not give are left
        // out of it. The function itself never runs the handler: the endpoint does.
        var factory = new AIFunctionFactoryOptions
        {
            Name = action.ToolName,
            Description = action.ToolDescription,
            ConfigureParameterBinding = p => fromTheModel.Contains(p)
                ? default
                : new AIFunctionFactoryOptions.ParameterBindingOptions { ExcludeFromSchema = true, BindParameter = (_, _) => null },
        };
        return action.Method.IsStatic
            ? AIFunctionFactory.Create(action.Method, target: null, factory)
            : AIFunctionFactory.Create(action.Method, _ => throw new InvalidOperationException("The endpoint runs the handler."), factory);
    }
}

/// <summary>
/// Story 1287 — runs an endpoint as an action: a request built for the call, in a scope of its own, as the user of the
/// turn; the endpoint's authorization first (<c>endpoint.authorization-first</c>), then its request delegate, so its
/// filters, its binding and its validation (<c>endpoint.runs-the-pipeline</c>). The application's middleware before
/// routing does not run. A refusal of the authorization, or an answer of 400 or more, is an
/// <see cref="HttpRequestException"/> with the status: the gate records the action as failed, and the model reads why.
/// </summary>
internal sealed class MentorEndpointActionFunction(
    AIFunction schema,
    MentorEndpointBinding binding,
    IServiceProvider services,
    ILogger<MentorEndpointActionFunction> logger) : DelegatingAIFunction(schema)
{
    /// <summary>The most characters of the endpoint's answer the model reads.</summary>
    internal const int MaxAnswerChars = 16_000;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        await using var scope = services.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
        var user = MentorUserAuthentication.For(services) is { } provider
            ? (await provider.GetAuthenticationStateAsync()).User
            : new ClaimsPrincipal(new ClaimsIdentity());

        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider, User = user, RequestAborted = cancellationToken };
        var request = http.Request;
        request.Method = binding.Verb;
        request.Scheme = "https";
        request.Host = new HostString("localhost");

        var route = new RouteValueDictionary();
        var query = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in binding.Parameters)
        {
            if (!arguments.TryGetValue(p.Parameter.Name ?? p.Name, out var value) || value is null) continue;
            switch (p.Source)
            {
                case MentorEndpointParameterSource.Route: route[p.Name] = Text(value); break;
                case MentorEndpointParameterSource.Query: query[p.Name] = Text(value); break;
                case MentorEndpointParameterSource.Body:
                    var body = value is JsonElement json ? Encoding.UTF8.GetBytes(json.GetRawText()) : JsonSerializer.SerializeToUtf8Bytes(value);
                    request.Body = new MemoryStream(body);
                    request.ContentType = "application/json";
                    request.ContentLength = body.Length;
                    // A minimal API reads a body only when the server says the request can have one.
                    http.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>(HasBody.Instance);
                    break;
            }
        }

        request.Path = Path(binding.Endpoint.RoutePattern, route);
        request.QueryString = QueryString.Create(query);
        request.RouteValues = route;
        http.SetEndpoint(binding.Endpoint);

        var answer = new MemoryStream();
        http.Response.Body = answer;

        if (await RefusedAsync(http, binding.Endpoint) is { } refused)
        {
            logger.LogInformation("[MentorAgent] Endpoint action {Endpoint}: refused by its authorization ({Status}).",
                binding.Display, (int)refused);
            throw new HttpRequestException(
                $"{binding.Display} refused the call: {(refused == HttpStatusCode.Unauthorized ? "the user is not signed in" : "the user is not allowed")} ({(int)refused}).",
                null, refused);
        }

        await binding.Endpoint.RequestDelegate!(http);

        var status = http.Response.StatusCode;
        var text = Read(answer);
        if (status >= 400)
            throw new HttpRequestException($"{binding.Display} answered {status}{(text.Length > 0 ? ": " + text : ".")}", null, (HttpStatusCode)status);

        if (text.Length == 0) return $"{binding.Display} answered {status}.";
        if (http.Response.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true && text.Length < MaxAnswerChars)
        {
            try { using var doc = JsonDocument.Parse(text); return doc.RootElement.Clone(); }
            catch (JsonException) { /* the text as it is */ }
        }

        return text;
    }

    /// <summary>
    /// The endpoint's authorization, as <c>UseAuthorization</c> decides it — its <c>[Authorize]</c>, its policies, the
    /// fallback policy, <c>[AllowAnonymous]</c> — for the user of the turn. An endpoint that asks for authorization in
    /// a host without it is refused: nothing runs past a rule nobody can check.
    /// </summary>
    private static async Task<HttpStatusCode?> RefusedAsync(HttpContext http, Endpoint endpoint)
    {
        if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null) return null;

        var authorizeData = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();
        var policies = endpoint.Metadata.GetOrderedMetadata<AuthorizationPolicy>();
        var provider = http.RequestServices.GetService<IAuthorizationPolicyProvider>();
        var authorization = http.RequestServices.GetService<IAuthorizationService>();
        if (provider is null || authorization is null)
            return authorizeData.Count > 0 || policies.Count > 0 ? HttpStatusCode.Forbidden : null;

        var policy = await AuthorizationPolicy.CombineAsync(provider, authorizeData, policies);
        if (policy is null) return null;

        var result = await authorization.AuthorizeAsync(http.User, http, policy);
        return result.Succeeded ? null
            : http.User.Identity?.IsAuthenticated == true ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized;
    }

    /// <summary>The path of the call: the route's literals and the values the model gave, each escaped.</summary>
    private static PathString Path(RoutePattern pattern, RouteValueDictionary values)
    {
        var segments = new List<string>();
        foreach (var segment in pattern.PathSegments)
        {
            var text = new StringBuilder();
            foreach (var part in segment.Parts)
            {
                switch (part)
                {
                    case RoutePatternLiteralPart literal: text.Append(literal.Content); break;
                    case RoutePatternSeparatorPart separator: text.Append(separator.Content); break;
                    case RoutePatternParameterPart parameter:
                        var value = values.TryGetValue(parameter.Name, out var v) && v is not null ? Convert.ToString(v, CultureInfo.InvariantCulture)
                            : parameter.Default is { } d ? Convert.ToString(d, CultureInfo.InvariantCulture)
                            : null;
                        if (value is null)
                        {
                            if (parameter.IsOptional || parameter.IsCatchAll) break;
                            throw new ArgumentException($"The route value '{parameter.Name}' is missing.");
                        }
                        values[parameter.Name] = value;
                        text.Append(parameter.IsCatchAll
                            ? string.Join('/', value.Split('/').Select(Uri.EscapeDataString))
                            : Uri.EscapeDataString(value));
                        break;
                }
            }

            if (text.Length > 0) segments.Add(text.ToString());
        }

        return new PathString("/" + string.Join('/', segments));
    }

    private static string Text(object value) => value switch
    {
        JsonElement { ValueKind: JsonValueKind.String } s => s.GetString() ?? "",
        JsonElement json => json.GetRawText(),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    private sealed class HasBody : Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature
    {
        internal static readonly HasBody Instance = new();
        public bool CanHaveBody => true;
    }

    private static string Read(MemoryStream answer)
    {
        var text = Encoding.UTF8.GetString(answer.GetBuffer(), 0, (int)Math.Min(answer.Length, MaxAnswerChars * 4L));
        return text.Length > MaxAnswerChars ? text[..MaxAnswerChars] + "…" : text;
    }
}
