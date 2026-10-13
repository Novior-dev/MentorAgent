
using Novior.MentorAgent.Options;

namespace Novior.MentorAgent.Core;

/// <summary>
/// Builds the Handoff workflow (Level 2) from [MentorAgent] classes
/// discovered by the discovery service, and optionally from remote A2A agents
/// configured via <see cref="MentorOptions.RemoteAgents"/>.
///
/// Pattern from the document (section 7.4):
/// <code>
/// var workflow = AgentWorkflowBuilder
///     .StartHandoffWith(coordinator)
///     .WithHandoffs(coordinator, [agent1, agent2])
///     .WithHandoff(agent1, coordinator)
///     .Build();
/// </code>
///
/// Each [MentorAgent]:
/// 1. Is instantiated as a ChatClientAgent with its own methods as tools
/// 2. Is connected to the Main Coordinator via WithHandoff
/// 3. Connections between specialists follow the HandoffTo[] of the attribute
///
/// Remote A2A agents are added to the same Handoff graph via the
/// <see cref="A2AChatClient"/> adapter — the coordinator can hand off to them
/// and they return to the coordinator when done.
/// </summary>
internal sealed class HandoffBuilder
{
    private readonly MentorPromptBuilder _promptBuilder;
    private readonly IServiceProvider _sp;
    private readonly IMentorStateService _stateService;
    private readonly Middleware.MentorAgentUIMiddleware _gate;
    private readonly MentorOptions _options;
    private readonly ILogger<HandoffBuilder> _logger;
    private readonly CoordinatorAnnouncement _announcement;

    /// <summary>Quiet for every coordinator after the first — the graph is the application's (BUG-068).</summary>
    private ILogger BuildLog => QuietBuildLogger.Wrap(_logger, _announcement.Quiet);

    public HandoffBuilder(
        MentorPromptBuilder promptBuilder,
        IServiceProvider sp,
        IMentorStateService stateService,
        Middleware.MentorAgentUIMiddleware gate,
        IOptions<MentorOptions> options,
        ILogger<HandoffBuilder> logger,
        CoordinatorAnnouncement announcement)
    {
        _announcement = announcement;
        _promptBuilder = promptBuilder;
        _sp = sp;
        _stateService = stateService;
        _gate = gate;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Builds the Handoff graph and returns it as an <see cref="AIAgent"/> exposed
    /// via <c>.AsAgent()</c>, ready to be converted into an AITool for the Coordinator.
    ///
    /// Returns null if there are no Level 2 agents (local or remote).
    /// </summary>
    public AIAgent? BuildHandoffAgent(
        IChatClient chatClient,
        ChatClientAgent mainCoordinator,
        List<AgentDiscoveryInfo> agents,
        List<ChatClientAgent>? remoteAgents = null,
        IReadOnlyList<AIAgent>? externalAgents = null)
    {
        remoteAgents ??= [];
        externalAgents ??= [];

        if (agents.Count == 0 && remoteAgents.Count == 0 && externalAgents.Count == 0) return null;

        // Builds a ChatClientAgent for each [MentorAgent]
        var builtAgents = new Dictionary<string, ChatClientAgent>(StringComparer.OrdinalIgnoreCase);

        foreach (var descriptor in agents)
        {
            var tools = BuildToolsForAgent(descriptor);
            var instructions = _promptBuilder.BuildAgentInstructions(descriptor);

            var agent = new ChatClientAgent(
                chatClient,
                new ChatClientAgentOptions
                {
                    Name = descriptor.Attribute.Name,
                    ChatOptions = new ChatOptions
                    {
                        Instructions = instructions,
                        Tools = tools,
                        ToolMode = ChatToolMode.Auto,
                    }
                });

            builtAgents[descriptor.Attribute.Name] = agent;

            _logger.LogDebug("[MentorAgent] Handoff: agent '{Name}' built with {N} tools",
                descriptor.Attribute.Name, tools.Count);
        }

        // Builds the Handoff graph
        // Source: section 7.4 of the Word document
#pragma warning disable MAAIW001 // Experimental API intentionally used for handoff workflow
        var builder = AgentWorkflowBuilder.CreateHandoffBuilderWith(mainCoordinator);
#pragma warning restore MAAIW001

        // Main Coordinator can hand off to all local specialists
        if (builtAgents.Count > 0)
            builder.WithHandoffs(mainCoordinator, builtAgents.Values.ToList());

        // #23 — specialists supplied by an IMentorAgentSource (declarative YAML today). Wired
        // exactly like a remote agent: reachable from the coordinator, and returning to it when
        // done. They are deliberately NOT given handoffs to each other — a YAML file cannot name
        // another agent to jump to, because that would let a definition file rewrite the routing
        // graph of an application it was only meant to add a specialist to.
        foreach (var external in externalAgents)
        {
            builder.WithHandoff(mainCoordinator, external);
            builder.WithHandoff(external, mainCoordinator);
            BuildLog.LogInformation("[MentorAgent] Handoff: declarative agent '{Name}' added to workflow.",
                external.Name ?? "(unnamed)");
        }

        // Main Coordinator can hand off to all remote A2A agents;
        // each remote agent returns to the coordinator when done.
        foreach (var remote in remoteAgents)
        {
            builder.WithHandoff(mainCoordinator, remote);
            builder.WithHandoff(remote, mainCoordinator);
            _logger.LogDebug("[MentorAgent] Handoff: remote A2A agent '{Name}' added to workflow.",
                remote.Name);
        }

        // Connections between local specialists (defined by HandoffTo[])
        foreach (var descriptor in agents)
        {
            var sourceAgent = builtAgents[descriptor.Attribute.Name];

            foreach (var targetName in descriptor.Attribute.HandoffTo)
            {
                if (builtAgents.TryGetValue(targetName, out var targetAgent))
                {
                    builder.WithHandoff(sourceAgent, targetAgent);
                    _logger.LogDebug("[MentorAgent] Handoff: {Source} -> {Target}",
                        descriptor.Attribute.Name, targetName);
                }
                else
                {
                    _logger.LogWarning(
                        "[MentorAgent] HandoffTo '{Target}' from '{Source}' not found. " +
                        "Verify that the name matches the Name in [MentorAgent].",
                        targetName, descriptor.Attribute.Name);
                }
            }

            // Return to Main Coordinator
            builder.WithHandoff(builtAgents[descriptor.Attribute.Name], mainCoordinator);
        }

        var workflow = builder.Build();

        return workflow.AsAIAgent(
            id: "handoff-workflow",
            name: "HandoffWorkflow",
            description: "Multi-agent workflow with handoff between specialists",
            includeExceptionDetails: _options.IncludeWorkflowExceptionDetails);
    }

    // -- Tool Building for Agent ------------------------------------------------

    private List<AITool> BuildToolsForAgent(AgentDiscoveryInfo descriptor)
    {
        var tools = new List<AITool>();

        foreach (var method in descriptor.Methods)
        {
            try
            {
                var instance = _sp.GetRequiredService(descriptor.Type);

                AITool tool = AIFunctionFactory.Create(
                    method: method.Method,
                    target: instance,
                    name: method.ToolName,
                    description: method.ToolDescription);

                // This agent runs its own function-calling loop inside the handoff workflow, where
                // the coordinator's middleware never reaches. Wrapping the function carries the gate
                // (RequiredRoles, RequiresConfirmation, feedback, navigation, metrics) with it —
                // without this, [MentorAction(RequiresConfirmation = true, RequiredRoles = [...])]
                // on a specialist method was silently ignored.
                tools.Add(Middleware.GatedAIFunction.Wrap(tool, _gate));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[MentorAgent] Tool '{Tool}' for agent '{Agent}' could not be registered. " +
                    "Ensure {Type} is registered in DI.",
                    method.ToolName, descriptor.Attribute.Name, descriptor.Type.Name);
            }
        }

        return tools;
    }
}
