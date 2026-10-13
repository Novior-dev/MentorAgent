using A2A.AspNetCore;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Server.Mcp;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

// Same namespace as AddMentorAgent and the two map methods, so a Blazor Server host that adds the
// Novior.MentorAgent.Server package needs no new using.
namespace Novior.MentorAgent.Extensions;

/// <summary>
/// Registers the MCP server and the A2A agent that <see cref="EndpointRouteBuilderExtensions.MapMentorAgentMcp"/>
/// and <see cref="EndpointRouteBuilderExtensions.MapMentorAgentA2A"/> map.
///
/// <para>
/// Until 1.0.0-rc.15 <c>AddMentorAgent</c> registered both itself. They moved to this package with the
/// endpoints (story 105) because their transports need ASP.NET Core, which <c>MentorAgent</c> no longer
/// references — that is what lets a MAUI app reference it on Android, iOS and Mac Catalyst.
/// <c>AddMentorAgentServer()</c> calls both, so a headless host needs nothing more; a Blazor Server host
/// that serves MCP or A2A without the hub calls the one it needs after <c>AddMentorAgent</c>.
/// </para>
/// </summary>
public static class ProtocolServiceCollectionExtensions
{
    /// <summary>
    /// Registers the MCP server when <see cref="MentorOptions.McpServerEnabled"/> is true, so that
    /// <c>app.MapMentorAgentMcp()</c> can map it. Call it after <c>AddMentorAgent()</c>:
    /// <code>
    /// builder.Services.AddMentorAgent(options => { options.McpServerEnabled = true; /* ... */ });
    /// builder.Services.AddMentorAgentMcp();
    /// // ...
    /// app.MapMentorAgentMcp();
    /// </code>
    /// <para>
    /// The flag is read as <c>AddMentorAgent</c> read it, at registration time. Calling this twice, or
    /// together with <c>AddMentorAgentServer()</c>, which calls it, registers the server once. Called
    /// before <c>AddMentorAgent()</c> it registers nothing, and the host stops at start-up saying so; the
    /// same when <c>McpServerEnabled</c> is turned on after <c>AddMentorAgent()</c> read it, in a
    /// <c>Configure</c> or <c>PostConfigure</c> of <see cref="MentorOptions"/>.
    /// </para>
    /// </summary>
    public static IServiceCollection AddMentorAgentMcp(this IServiceCollection services)
    {
        AddStartUpCheck<MentorMcpServerRequested>(services);
        if (services.Any(d => d.ServiceType == typeof(MentorMcpServerRegistered)))
            return services;
        if (Snapshot(services) is not { McpServerEnabled: true } o)
            return services;

        services.AddSingleton<MentorMcpServerRegistered>();

        // WHY custom handlers instead of .WithTools<MentorAgentMcpToolCollection>():
        //   The SDK's generic WithTools<T>() scans T for [McpServerTool]-attributed methods.
        //   MentorAgentMcpToolCollection has none — it implements IEnumerable<McpServerTool>
        //   and builds tools dynamically at runtime. Using WithListToolsHandler +
        //   WithCallToolHandler lets us delegate directly to the collection at request time.
        //
        // CORS: a named policy for hosts with a browser-based MCP client. The endpoint does not use it
        // unless the host opts in — MapMentorAgentMcp(configure: e => e.RequireCors("MentorAgentMcp"))
        // with app.UseCors() (BUG-108). It allows any origin; a host that opts in defines a policy of the
        // same name, before or after AddMentorAgent, that lists its origins and exposes Mcp-Session-Id.
        // Only when the host has not defined one: a policy of the same name registered BEFORE
        // AddMentorAgent must not be silently replaced by any-origin (one registered after overwrites
        // this one, as it always did). The name is not a namespace: hosts write it in RequireCors, so it
        // stays when the namespace changes (ContractNameTests).
        services.AddCors(cors =>
        {
            if (cors.GetPolicy("MentorAgentMcp") is null)
                cors.AddPolicy("MentorAgentMcp", policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
        });

        services.AddSingleton<MentorAgentMcpToolCollection>();
        services.AddSingleton<MentorMcpCallHandler>();
        services.AddMcpServer(opts =>
            {
                opts.ServerInfo = new Implementation
                {
                    Name    = o.AppName,
                    Version = "1.0",
                };
            })
            .WithHttpTransport()
            .WithListToolsHandler((ctx, ct) =>
            {
                var collection = ctx.Services!.GetRequiredService<MentorAgentMcpToolCollection>();
                var tools = collection.GetTools().Select(t => t.ProtocolTool).ToList();
                return new ValueTask<ListToolsResult>(new ListToolsResult { Tools = tools });
            })
            // tools/call carries the gates every other surface applies — the per-user rate limit, the
            // tool metrics and OnToolResult — and the role decision of THIS request (BUG-103, BUG-107).
            .WithCallToolHandler((ctx, ct) =>
                ctx.Services!.GetRequiredService<MentorMcpCallHandler>().HandleAsync(ctx, ct));

        return services;
    }

    /// <summary>
    /// Registers the A2A agent when <see cref="MentorOptions.A2AServerEnabled"/> is true, so that
    /// <c>app.MapMentorAgentA2A()</c> can map it. Call it after <c>AddMentorAgent()</c>:
    /// <code>
    /// builder.Services.AddMentorAgent(options => { options.A2AServerEnabled = true; /* ... */ });
    /// builder.Services.AddMentorAgentA2A();
    /// // ...
    /// app.MapMentorAgentA2A();
    /// </code>
    /// <para>
    /// The flag is read as <c>AddMentorAgent</c> read it, at registration time. Calling this twice, or
    /// together with <c>AddMentorAgentServer()</c>, which calls it, registers the agent once. Called
    /// before <c>AddMentorAgent()</c> it registers nothing, and the host stops at start-up saying so; the
    /// same when <c>A2AServerEnabled</c> is turned on after <c>AddMentorAgent()</c> read it, in a
    /// <c>Configure</c> or <c>PostConfigure</c> of <see cref="MentorOptions"/>.
    /// </para>
    /// </summary>
    public static IServiceCollection AddMentorAgentA2A(this IServiceCollection services)
    {
        AddStartUpCheck<MentorA2AServerRequested>(services);
        if (services.Any(d => d.ServiceType == typeof(MentorA2AServerRegistered)))
            return services;
        if (Snapshot(services) is not { A2AServerEnabled: true } o)
            return services;

        services.AddSingleton<MentorA2AServerRegistered>();

        // Registers the IAgentHandler + A2AServer infrastructure. This card is not the one served:
        // MapMentorAgentA2A builds the full card (skills, interfaces, provider) when it maps.
        var agentCard = new A2A.AgentCard
        {
            Name        = o.AppName,
            Description = string.IsNullOrEmpty(o.AppDescription)
                              ? string.Empty : o.AppDescription,
            Version     = o.AgentCard?.Version ?? "1.0.0",
        };

        services.AddA2AAgent<MentorAgentA2AHandler>(agentCard);

        // The caller of each task, from the request (BUG-131): a Blazor Server host serves A2A without
        // AddMentorAgentServer(), and there the host's provider throws outside a circuit.
        Novior.MentorAgent.Server.Auth.HttpContextMentorUser.Register(services);
        return services;
    }

    /// <summary>The options as <c>AddMentorAgent</c> read them, or null when it has not run yet.</summary>
    private static MentorOptions? Snapshot(IServiceCollection services) =>
        (services.LastOrDefault(d => d.ServiceType == typeof(MentorRegistrationSnapshot))?
            .ImplementationInstance as MentorRegistrationSnapshot)?.Options;

    /// <summary>
    /// Records that the host asked for a server, whatever the order and the flag, and adds the check that
    /// stops the host when the server it asked for was not registered.
    /// </summary>
    private static void AddStartUpCheck<TRequested>(IServiceCollection services) where TRequested : class
    {
        services.TryAddSingleton<TRequested>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<MentorOptions>, MentorProtocolRegistrationCheck>());
    }
}

/// <summary>Marks that <see cref="ProtocolServiceCollectionExtensions.AddMentorAgentMcp"/> registered the MCP server.</summary>
internal sealed class MentorMcpServerRegistered;

/// <summary>Marks that <see cref="ProtocolServiceCollectionExtensions.AddMentorAgentA2A"/> registered the A2A agent.</summary>
internal sealed class MentorA2AServerRegistered;

/// <summary>Marks that the host called <see cref="ProtocolServiceCollectionExtensions.AddMentorAgentMcp"/>, at any point.</summary>
internal sealed class MentorMcpServerRequested;

/// <summary>Marks that the host called <see cref="ProtocolServiceCollectionExtensions.AddMentorAgentA2A"/>, at any point.</summary>
internal sealed class MentorA2AServerRequested;

/// <summary>
/// Stops the host when it asked for the MCP server or the A2A agent, its flag is on, and nothing was
/// registered: <c>AddMentorAgentMcp()</c> / <c>AddMentorAgentA2A()</c> ran before <c>AddMentorAgent()</c>, or
/// the flag was turned on after <c>AddMentorAgent()</c> read it. Until 1.0.0 both registered nothing and
/// said nothing; the error came only from the map call, and in the second case it told the host to call a
/// method it had already called. A validator of <see cref="MentorOptions"/>, so it runs where the options
/// are checked: when the host starts (<c>ValidateOnStart</c>), or at the first read without a host.
/// </summary>
internal sealed class MentorProtocolRegistrationCheck(IServiceProvider services) : IValidateOptions<MentorOptions>
{
    public ValidateOptionsResult Validate(string? name, MentorOptions options)
    {
        if (name is not null && name != Microsoft.Extensions.Options.Options.DefaultName)
            return ValidateOptionsResult.Skip;

        var read = services.GetService<MentorRegistrationSnapshot>()?.Options;
        var failures = new List<string>();

        if (options.McpServerEnabled
            && services.GetService<MentorMcpServerRequested>() is not null
            && services.GetService<MentorMcpServerRegistered>() is null)
            failures.Add(Unregistered("MCP server", "McpServerEnabled", "AddMentorAgentMcp", read?.McpServerEnabled == true));

        if (options.A2AServerEnabled
            && services.GetService<MentorA2AServerRequested>() is not null
            && services.GetService<MentorA2AServerRegistered>() is null)
            failures.Add(Unregistered("A2A agent", "A2AServerEnabled", "AddMentorAgentA2A", read?.A2AServerEnabled == true));

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static string Unregistered(string server, string flag, string addMethod, bool onWhenRead) => onWhenRead
        ? $"[MentorAgent] {flag} is true, but {addMethod}() ran before AddMentorAgent(): it found no options and " +
          $"registered no {server}. Call builder.Services.{addMethod}() after AddMentorAgent() " +
          "(AddMentorAgentServer() includes it)."
        : $"[MentorAgent] {flag} is true, but it was false when AddMentorAgent() read it, so {addMethod}() " +
          $"registered no {server}. The {server} is registered with the services, so set {flag} in the " +
          "AddMentorAgent() callback, not in a Configure or PostConfigure of MentorOptions, nor in configuration " +
          "bound after AddMentorAgent().";
}
