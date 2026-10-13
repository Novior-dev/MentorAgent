using A2A.AspNetCore;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Utility;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;

// Story 105: this class moved from the MentorAgent package to Novior.MentorAgent.Server with the MCP and A2A
// server packages, which need ASP.NET Core. The namespace is unchanged on purpose: a host that adds the
// Novior.MentorAgent.Server package keeps its Program.cs usings.
namespace Novior.MentorAgent.Extensions;

public static class EndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps the MentorAgent MCP server endpoint, exposing every
    /// <c>[MentorAction]</c> method as an MCP tool.
    ///
    /// Prerequisites in Program.cs:
    /// <code>
    /// builder.Services.AddMentorAgent(options =>
    /// {
    ///     options.McpServerEnabled = true;
    ///     // ...
    /// });
    /// builder.Services.AddMentorAgentMcp();   // AddMentorAgentServer() includes it
    /// // ...
    /// app.MapMentorAgentMcp();          // default path: /mcp
    /// app.MapMentorAgentMcp("/my-mcp"); // custom path
    /// </code>
    ///
    /// MCP clients (e.g. Claude Desktop) can then connect to
    /// <c>https://yourapp/mcp</c> and discover all available tools.
    ///
    /// <para>
    /// The tool instances are resolved once, from a scope that lives as long as the application, so a
    /// <c>[MentorAction]</c> class is one shared instance for every MCP call, whatever lifetime it is
    /// registered with — transient included. Keep such classes stateless: inject
    /// <c>IDbContextFactory</c> or <c>IServiceScopeFactory</c> and create what a call needs inside it,
    /// never hold a <c>DbContext</c> in a field.
    /// </para>
    ///
    /// <para>
    /// <b>No CORS policy is attached</b> (BUG-108). MCP clients that are not browsers — desktop and IDE
    /// clients, the MCP Inspector's proxy — need none, and the endpoint used to carry one that allowed
    /// any origin, which also made every request fail with a 500 on a pipeline without
    /// <c>app.UseCors()</c>. A host with a browser-based client opts in with <c>app.UseCors()</c> and
    /// <c>app.MapMentorAgentMcp(configure: e =&gt; e.RequireCors("MentorAgentMcp"))</c>, replacing the
    /// built-in <c>MentorAgentMcp</c> policy with one that names its origins.
    /// </para>
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="path">
    /// The URL path for the MCP endpoint. Omitted: <see cref="MentorOptions.McpServerPath"/> (default
    /// <c>/mcp</c>) — the option used to be read by nothing, so setting it moved no endpoint.
    /// </param>
    /// <param name="configure">
    /// Applied to the mapped endpoint, so the host can protect it:
    /// <c>app.MapMentorAgentMcp(configure: e =&gt; e.RequireAuthorization());</c>. Without it the
    /// endpoint stays open to anyone who can reach the port — which is what the withheld-tool rule
    /// (BUG-021) assumes, and why every gated action is refused there. Optional: omitting it changes
    /// nothing for existing hosts.
    /// </param>
    public static IEndpointRouteBuilder MapMentorAgentMcp(
        this IEndpointRouteBuilder endpoints,
        string? path = null,
        Action<IEndpointConventionBuilder>? configure = null)
    {
        // Validate that the MCP server was registered in DI.
        // AddMcpServer().WithHttpTransport() must be called by AddMentorAgentMcp()
        // before MapMcp() is called here. If McpServerEnabled = false, or the server was never
        // registered, this call throws a clear error rather than the SDK's or a silent failure.
        var sp = endpoints.ServiceProvider;
        var opts = sp.GetService<IOptions<MentorOptions>>();
        if (opts is not null && !opts.Value.McpServerEnabled)
        {
            throw new InvalidOperationException(
                "[MentorAgent] Cannot map MCP endpoint: McpServerEnabled is false. " +
                "Set options.McpServerEnabled = true in AddMentorAgent() before calling MapMentorAgentMcp().");
        }
        if (opts is not null && sp.GetService<MentorMcpServerRegistered>() is null)
            throw new InvalidOperationException(NotRegistered("MCP", "AddMentorAgentMcp", "MapMentorAgentMcp"));

        // No RequireCors here (BUG-108). CORS metadata on an endpoint makes ASP.NET Core refuse the request
        // with a 500 unless the CORS middleware ran — and whether the host added it cannot be read at map
        // time. Opting in is the host's one line: configure: e => e.RequireCors("MentorAgentMcp").
        var mcp = endpoints.MapMcp(path ?? opts?.Value.McpServerPath ?? "/mcp");
        configure?.Invoke(mcp);
        return endpoints;
    }

    /// <summary>
    /// Maps the MentorAgent A2A agent endpoints:
    /// <list type="bullet">
    ///   <item><c>GET /.well-known/agent-card.json</c> — Agent Card</item>
    ///   <item><c>POST {path}</c> — A2A message handler</item>
    /// </list>
    ///
    /// Prerequisites in Program.cs:
    /// <code>
    /// builder.Services.AddMentorAgent(options =>
    /// {
    ///     options.A2AServerEnabled = true;
    ///     options.AppName          = "MyApp";
    ///     options.AppDescription   = "My Blazor AI assistant";
    ///     // optional: options.AgentCard = new AgentCardInfo { Version = "2.0.0" };
    /// });
    /// builder.Services.AddMentorAgentA2A();   // AddMentorAgentServer() includes it
    /// // ...
    /// app.MapMentorAgentA2A();          // default path: /a2a
    /// app.MapMentorAgentA2A("/agent");  // custom path
    /// </code>
    ///
    /// Remote orchestrators can then discover this agent via
    /// <c>https://yourapp/.well-known/agent-card.json</c> and send A2A tasks to
    /// <c>https://yourapp/a2a</c>.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="path">
    /// The URL path for the A2A endpoint. Omitted: <see cref="MentorOptions.A2AServerPath"/> (default
    /// <c>/a2a</c>) — the option used to be read by nothing, so setting it moved no endpoint.
    /// </param>
    /// <param name="configure">
    /// Applied to both mapped endpoints — the task handler and the well-known agent card — so the
    /// host can protect them: <c>app.MapMentorAgentA2A(configure: e =&gt; e.RequireAuthorization());</c>.
    /// Optional; omitting it leaves the endpoints exactly as they were.
    /// </param>
    public static IEndpointRouteBuilder MapMentorAgentA2A(
        this IEndpointRouteBuilder endpoints,
        string? path = null,
        Action<IEndpointConventionBuilder>? configure = null)
    {
        var sp = endpoints.ServiceProvider;
        var opts = sp.GetService<IOptions<MentorOptions>>();
        path ??= opts?.Value.A2AServerPath ?? "/a2a";
        if (opts is not null && !opts.Value.A2AServerEnabled)
        {
            throw new InvalidOperationException(
                "[MentorAgent] Cannot map A2A endpoint: A2AServerEnabled is false. " +
                "Set options.A2AServerEnabled = true in AddMentorAgent() before calling MapMentorAgentA2A().");
        }
        if (opts is not null && sp.GetService<MentorA2AServerRegistered>() is null)
            throw new InvalidOperationException(NotRegistered("A2A", "AddMentorAgentA2A", "MapMentorAgentA2A"));

        var a2a = endpoints.MapA2A(path);
        configure?.Invoke(a2a);

        // AddA2AAgent<T>(agentCard) does NOT register AgentCard as a directly resolvable
        // DI service in preview2 of the SDK — GetService<AgentCard>() always returns null.
        // Re-build the card from MentorOptions and register the well-known card endpoint explicitly.
        // The SDK serves it at /.well-known/agent-card.json — the path both READMEs publish.
        if (opts is not null)
        {
            var o = opts.Value;
            var card = new A2A.AgentCard
            {
                Name        = o.AppName,
                Description = string.IsNullOrEmpty(o.AppDescription) ? string.Empty : o.AppDescription,
                Version     = o.AgentCard?.Version ?? "1.0.0",
                // SupportedInterfaces tells A2A clients which protocol bindings are available
                // and the URL where they can POST tasks (A2A spec §8.3).
                // A2AServerUrl must be the full URL, e.g. "http://localhost:5001/a2a".
                // Falls back to the path only (works for same-host clients).
                SupportedInterfaces =
                [
                    // JSON-RPC, because that is what MapA2A above maps. The card used to say
                    // HTTP+JSON, so a client that believed it POSTed to {url}/message:send and got
                    // 404 — every client, MentorAgent's own included (BUG-078). Nobody had ever
                    // followed the card to a live peer.
                    new A2A.AgentInterface
                    {
                        ProtocolBinding = A2A.ProtocolBindingNames.JsonRpc,
                        Url             = o.A2AServerUrl ?? path,
                    }
                ],

                // The rest of what AgentCardInfo offers. Previously only Version was copied, so a
                // host that followed the type's own worked example — which sets IconUrl, Provider and
                // DocumentationUrl — had three of the four silently dropped. Nobody would notice: the
                // audience is a remote agent, and it simply receives less metadata than the README
                // promised it would.
                IconUrl          = o.AgentCard?.IconUrl,
                DocumentationUrl = o.AgentCard?.DocumentationUrl,
                Provider = string.IsNullOrWhiteSpace(o.AgentCard?.Provider)
                    ? null
                    : new A2A.AgentProvider { Organization = o.AgentCard.Provider },

                // What a remote orchestrator actually chooses an agent by. Until now the card
                // carried a name and a description and nothing else: a peer could see that we
                // exist and not one thing we can do. The skills are the application's own
                // [MentorAction]s — the ungated ones only, by the same rule that withholds gated
                // actions from the MCP surface (BUG-021): the card is the same for every caller,
                // signed in or not, and nobody answers a confirmation, so advertising a gated action
                // would be advertising a refusal.
                Skills = BuildAgentSkills(sp, o),

                // Text in, text out; images too when the host accepts them. An empty list is not
                // "unknown" to a client, it is "nothing", so these are stated rather than omitted.
                DefaultInputModes  = o.EnableImageInput ? ["text/plain", "image/png"] : ["text/plain"],
                DefaultOutputModes = ["text/plain"],

                // MentorAgent streams every turn; a peer that can consume a stream should know.
                Capabilities = new A2A.AgentCapabilities { Streaming = true },
            };
            var wellKnown = endpoints.MapWellKnownAgentCard(card);
            configure?.Invoke(wellKnown);
        }

        return endpoints;
    }

    /// <summary>
    /// Story 105: <c>AddMentorAgent</c> no longer registers the two servers, so a host that only moved to
    /// the new package would otherwise meet the SDK's own error, which names methods it never calls.
    /// </summary>
    private static string NotRegistered(string server, string addMethod, string mapMethod) =>
        $"[MentorAgent] Cannot map the {server} endpoint: the {server} server is not registered. " +
        $"Call builder.Services.{addMethod}() after AddMentorAgent() (AddMentorAgentServer() includes it), " +
        $"then app.{mapMethod}(). Since 1.0.0-rc.15 the MCP and A2A servers are registered by the " +
        "Novior.MentorAgent.Server package, not by AddMentorAgent().";

    /// <summary>
    /// The application's ungated actions, as A2A skills. Gated ones (roles, confirmation, or the
    /// <c>RequiresApproval</c> predicate) are left out for the reason BUG-021 established for MCP.
    /// An A2A task runs as the request's user: signed in when the host authenticates the endpoint,
    /// anonymous otherwise — and then, with the default <c>AnonymousIdentity</c> (PerSession), each task
    /// gets its own user key, so <c>RateLimitPerUser</c> does not hold across tasks. Either way the card is one for every caller and no human answers a
    /// confirmation, so a gated action would be a refusal, and a card that promises a refusal is worse
    /// than one that promises nothing.
    /// </summary>
    private static List<A2A.AgentSkill> BuildAgentSkills(IServiceProvider sp, MentorOptions o)
    {
        var discovery = sp.GetService<Core.MentorDiscoveryService>();
        if (discovery is null) return [];

        var skills = new List<A2A.AgentSkill>();
        foreach (var m in discovery.Discover().Level1Methods)
        {
            if (m.RequiredRoles.Length > 0 || m.RequiresConfirmation) continue;
            if (o.RequiresApproval?.Invoke(m.ToolName) == true) continue;

            skills.Add(new A2A.AgentSkill
            {
                Id          = m.ToolName,
                // ToolName, not Method.Name: ToFriendlyName splits on underscores, so the PascalCase
                // method name came out as one flattened word ("Getallordersforanalysis") — BUG-070.
                Name        = MentorUtils.ToFriendlyName(m.ToolName),
                Description = m.Description ?? string.Empty,
                Tags        = string.IsNullOrWhiteSpace(m.Category) ? [] : [m.Category],
            });
        }

        return skills;
    }
}
