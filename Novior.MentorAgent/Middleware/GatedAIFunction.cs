namespace Novior.MentorAgent.Middleware;

/// <summary>
/// Carries MentorAgent's tool gate — <c>RequiredRoles</c>, <c>RequiresConfirmation</c>, action
/// feedback, result overrides, auto-navigation and metrics — into agents that run their own
/// function-calling loop: Level-2 specialists (<c>HandoffBuilder</c>) and Level-3 team members
/// (<c>GroupChatBuilder</c>).
///
/// <para>
/// <b>Why a wrapper and not middleware.</b> Those agents are built as
/// <c>new ChatClientAgent(chatClient, …)</c> and then handed to <c>AgentWorkflowBuilder</c>, which
/// owns the graph — there is no place to insert <c>MentorAgentUIMiddleware</c>, and the function
/// invocation happens inside the workflow, out of the coordinator's reach. Attaching the gate to the
/// <see cref="AIFunction"/> itself makes it travel with the tool instead, so it applies wherever the
/// tool is invoked from.
/// </para>
///
/// <para>
/// Before this, <c>[MentorAction(RequiresConfirmation = true, RequiredRoles = ["Admin"])]</c> on a
/// specialist method was silently ignored: the action ran with no banner and no role check. That was
/// a security gap, not just a UX one.
/// </para>
/// </summary>
internal sealed class GatedAIFunction : DelegatingAIFunction
{
    private readonly MentorAgentUIMiddleware _gate;
    private readonly MentorAgentKind _agentKind;
    private readonly string? _agentName;

    /// <param name="innerFunction">The tool.</param>
    /// <param name="gate">The scope's gate.</param>
    /// <param name="agentKind">Which kind of agent the tool is given to, for the gate's decisions (story 180).</param>
    /// <param name="agentName">
    /// That agent's name, when the tool belongs to one agent. The agents of an <see cref="Core.IMentorAgentSource"/>
    /// share one list, so theirs is the agent running the call (<see cref="AIAgent.CurrentRunContext"/>).
    /// </param>
    public GatedAIFunction(AIFunction innerFunction, MentorAgentUIMiddleware gate,
        MentorAgentKind agentKind = MentorAgentKind.Specialist, string? agentName = null)
        : base(innerFunction)
    {
        _gate      = gate;
        _agentKind = agentKind;
        _agentName = agentName;
    }

    protected override ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments, CancellationToken cancellationToken)
        => _gate.InvokeGatedAsync(
               Name,
               arguments,
               ct => base.InvokeCoreAsync(arguments, ct),
               cancellationToken,
               // Nested agents cannot use the Agent Framework's native approval protocol: a
               // ToolApprovalRequestContent raised inside the workflow never reaches the
               // orchestrator. The blocking flow is the only one that can reach the user from here,
               // and the banner is identical either way.
               alwaysBlockForApproval: true,
               // This function's own method: a level-1 method with the same name and no roles used to decide
               // the roles of a specialist's call (BUG-188).
               method: UnderlyingMethod,
               callId: FunctionInvokingChatClient.CurrentContext?.CallContent?.CallId,
               agentKind: _agentKind,
               agentName: _agentName ?? AIAgent.CurrentRunContext?.Agent?.Name,
               description: Description);

    /// <summary>
    /// Wraps <paramref name="tool"/> in the gate when it is an invocable <see cref="AIFunction"/>.
    /// Anything else (hosted tools, declarations) is returned unchanged.
    /// </summary>
    public static AITool Wrap(AITool tool, MentorAgentUIMiddleware gate,
        MentorAgentKind agentKind = MentorAgentKind.Specialist, string? agentName = null)
        => tool is AIFunction fn and not GatedAIFunction ? new GatedAIFunction(fn, gate, agentKind, agentName) : tool;
}
