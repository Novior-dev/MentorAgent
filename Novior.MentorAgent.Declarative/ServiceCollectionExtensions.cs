using Novior.MentorAgent.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Novior.MentorAgent.Declarative;

/// <summary>
/// Registers YAML-defined Level-2 specialists (roadmap #23).
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Loads agent definitions from YAML and adds them to MentorAgent's handoff graph, alongside
    /// the ones declared with <c>[MentorAgent]</c>.
    ///
    /// <code>
    /// builder.Services.AddMentorAgentDeclarative(o => o.Directory = "Agents");
    /// builder.Services.AddMentorAgent(o => { /* … */ });   // order does not matter
    /// </code>
    ///
    /// A definition looks like this — note that <c>tools</c> <b>names</b> tools, it does not define
    /// them; each name must match a tool your application already exposes:
    ///
    /// <code>
    /// kind: Prompt
    /// name: ShippingAgent
    /// description: Answers questions about deliveries and shipping costs
    /// instructions: |
    ///   You handle shipping questions only. Use the available tools to look up real orders;
    ///   never invent a tracking number. If the question is not about shipping, say so.
    /// model:
    ///   options:
    ///     temperature: 0.2
    /// tools:
    ///   - kind: function          # required on every entry; `function` is the only kind kept
    ///     name: get_order_status
    ///   - kind: function
    ///     name: get_all_orders
    /// </code>
    ///
    /// <para>
    /// <b>Treat definition files as code.</b> They choose the model, write the system instructions
    /// and name the callable tools, so load them only from a location that ships with the
    /// application. What they cannot do is grant themselves anything new: a named tool is resolved
    /// from the ones already registered and arrives already wrapped in MentorAgent's gate, keeping
    /// <c>RequiredRoles</c> and human approval in force. The provider-hosted kinds the Agent Framework
    /// also reads (<c>webSearch</c>, <c>codeInterpreter</c>, <c>fileSearch</c>, <c>mcp</c>) are removed
    /// with a warning: those capabilities are the host's to configure.
    /// </para>
    ///
    /// <para>
    /// A team is written the same way, in a <c>*.team.yaml</c> file of the same folder
    /// (<see cref="MentorDeclarativeOptions.TeamSearchPattern"/>) or in
    /// <see cref="MentorDeclarativeOptions.TeamDefinitions"/>: <c>name</c>, <c>description</c>, <c>members</c> with their
    /// <c>role</c>, <c>instructions</c> and <c>tools</c> named as the session's actions, and optionally <c>approver</c>,
    /// <c>triggerOn</c>, <c>maxIterations</c> and <c>handoffTo</c>. A file that does not hold is left out with its line
    /// and column.
    /// </para>
    /// </summary>
    public static IServiceCollection AddMentorAgentDeclarative(
        this IServiceCollection services,
        Action<MentorDeclarativeOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var options = new MentorDeclarativeOptions();
        configure(options);
        services.AddSingleton(options);

        // Enumerable, not TryAdd: several sources may coexist (a folder of YAML plus, say, a
        // database-backed one), and MentorAgent asks every registered source.
        //
        // The two-generic overload is required. ServiceDescriptor.Singleton<IMentorAgentSource>(factory)
        // records the implementation type as the *interface*, and TryAddEnumerable then rejects it as
        // indistinguishable from any other registration — it deduplicates on implementation type.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IMentorAgentSource, DeclarativeAgentSource>(sp => new DeclarativeAgentSource(
                sp.GetRequiredService<MentorDeclarativeOptions>(),
                sp.GetRequiredService<ILoggerFactory>(),
                sp.GetService<IConfiguration>())));

        // Story 1570, slice B: the teams of the same folder (*.team.yaml) and TeamDefinitions, through the core's
        // IMentorTeamSource: the core checks them as it checks any source's.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IMentorTeamSource, DeclarativeTeamSource>(sp => new DeclarativeTeamSource(
                sp.GetRequiredService<MentorDeclarativeOptions>(),
                sp.GetRequiredService<ILoggerFactory>())));

        return services;
    }
}
