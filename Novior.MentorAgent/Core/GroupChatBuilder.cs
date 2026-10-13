
using Novior.MentorAgent.Options;

namespace Novior.MentorAgent.Core;

/// <summary>
/// Builds the Group Chat workflow (Level 3) from [MentorTeam] classes.
///
/// Each [TeamMember] can have read-only tools for analyzing real data.
/// After the team emits <see cref="ApprovalSentinel"/>, it can hand off
/// to L2 agents via HandoffTo.
/// </summary>
internal sealed class GroupChatBuilder
{
    /// <summary>
    /// Structured token the AI must include in the final decision message to
    /// signal approval. Using a bracketed tag makes it far less likely to appear
    /// accidentally in normal discussion text compared to a bare word like "APPROVED".
    /// </summary>
    private const string ApprovalSentinel = "[TEAM:APPROVED]";

    private readonly MentorPromptBuilder _promptBuilder;
    private readonly IServiceProvider _sp;
    private readonly IMentorStateService _stateService;
    private readonly Middleware.MentorAgentUIMiddleware _gate;
    private readonly MentorOptions _options;
    private readonly ILogger<GroupChatBuilder> _logger;

    public GroupChatBuilder(
        MentorPromptBuilder promptBuilder,
        IServiceProvider sp,
        IMentorStateService stateService,
        Middleware.MentorAgentUIMiddleware gate,
        IOptions<MentorOptions> options,
        ILogger<GroupChatBuilder> logger)
    {
        _promptBuilder = promptBuilder;
        _sp = sp;
        _stateService = stateService;
        _gate = gate;
        _options = options.Value;
        _logger = logger;
    }

    public AITool BuildTeamTool(TeamDiscoveryInfo team, IChatClient chatClient)
    {
        var participants = team.Members
            .Select(member => BuildMemberAgent(member, chatClient))
            .ToArray();

        _logger.LogDebug("[MentorAgent] GroupChat '{Name}': {N} participants",
            team.Attribute.Name, participants.Length);

        // BUG-146 — a new manager for every run. The tool lives as long as the session, and the
        // manager counts iterations: one shared instance spent MaxIterations across all of a
        // session's activations, and from then on the team was activated with no member speaking.
        // The workflow calls this factory on each run.
        var workflow = AgentWorkflowBuilder
            .CreateGroupChatBuilderWith(_ => BuildManager(team, participants))
            .AddParticipants(participants)
            .Build();

        var teamAgent = workflow.AsAIAgent(
            id: $"team-{team.Attribute.Name.ToLowerInvariant().Replace(" ", "-")}",
            name: team.Attribute.Name,
            description: team.Attribute.Description,
            includeExceptionDetails: _options.IncludeWorkflowExceptionDetails);

        var handoffNote = team.Attribute.HandoffTo.Length > 0
            ? $" After approval, hand off to: {string.Join(", ", team.Attribute.HandoffTo)}."
            : string.Empty;

        // Tool name must be snake_case — spaces cause rejections with most AI providers.
        // We derive it from the team name just like MentorDiscoveryService.ToSnakeCase().
        var teamToolName = MentorUtils.ToSnakeCase(team.Attribute.Name);

        // Capture for use inside the lambda
        var stateService = _stateService;
        var logger = _logger;

        var teamTool = AIFunctionFactory.Create(
            async (
                [System.ComponentModel.Description("The task to analyze/evaluate")]
                string task,
                CancellationToken ct) =>
            {
                logger.LogInformation("[MentorAgent] Team '{Name}' activated: {Task}",
                    team.Attribute.Name, task[..Math.Min(task.Length, 100)]);

                // ── Stream the team discussion ─────────────────────────────────
                // RunStreamingAsync lets us observe each member's turn and fire
                // OnTeamMemberSpeaking so the UI can show "💬 DataAnalyst is analyzing…"
                var sb = new System.Text.StringBuilder();
                string? lastSpeaker = null;

                await foreach (var update in teamAgent.RunStreamingAsync(task, cancellationToken: ct))
                {
                    // Fire event when a new team member starts speaking
                    if (update.AuthorName is { Length: > 0 } speaker && speaker != lastSpeaker)
                    {
                        lastSpeaker = speaker;
                        stateService.NotifyTeamMemberSpeaking(team.Attribute.Name, speaker);
                        logger.LogDebug("[MentorAgent] Team '{Team}': member '{Member}' speaking.",
                            team.Attribute.Name, speaker);
                    }

                    if (update.Text is { Length: > 0 } chunk)
                        sb.Append(chunk);
                }

                var resultText = sb.ToString();

                // ── Strip internal sentinel tokens before returning to the coordinator ─
                // [TEAM:APPROVED] and [TEAM:REJECTED] are coordination signals only.
                // They must not appear in the user-visible response.
                var cleanText = resultText
                    .Replace("[TEAM:APPROVED]", string.Empty, StringComparison.OrdinalIgnoreCase)
                    .Replace("[TEAM:REJECTED]", string.Empty, StringComparison.OrdinalIgnoreCase)
                    .Trim();

                // ── Handoff after structured approval ─────────────────────────
                // We check for the structured sentinel [TEAM:APPROVED] instead of
                // a bare word like "APPROVED" to avoid false positives from phrases
                // such as "NOT APPROVED" or "the plan has not been approved yet".
                if (team.Attribute.HandoffTo.Length > 0)
                {
                    var isApproved = resultText.Contains(
                        ApprovalSentinel, StringComparison.OrdinalIgnoreCase);

                    if (isApproved)
                    {
                        // Conditional on purpose: the actions run through route_to_specialist, and an action
                        // may ask the user first. Told only "now execute", a coordinator whose action was then
                        // declined reported a problem that never happened (BUG-144).
                        return $"{cleanText}\n\n" +
                               $"The team has reviewed this and is ready to proceed. " +
                               $"Now execute the approved actions. An action may ask the user to confirm first: " +
                               $"if the user declines, it did not run, and that is the user's decision, not a " +
                               $"problem — say only that you did not proceed.";
                    }
                }

                return cleanText;
            },
            name: teamToolName,
            description: team.Attribute.Description + handoffNote);

        return teamTool;
    }

    // -- Member Agent ----------------------------------------------------------

    private ChatClientAgent BuildMemberAgent(TeamMemberInfo member, IChatClient chatClient)
    {
        var instructions = _promptBuilder.BuildTeamMemberInstructions(member);
        var tools = BuildMemberTools(member);

        if (tools.Count > 0)
        {
            _logger.LogDebug("[MentorAgent] TeamMember '{Role}': {N} tools",
                member.Attribute.Role, tools.Count);

            return new ChatClientAgent(
                chatClient,
                new ChatClientAgentOptions
                {
                    Name = member.Attribute.Role,
                    ChatOptions = new ChatOptions
                    {
                        Tools = tools,
                        Instructions = instructions,
                        ToolMode = ChatToolMode.Auto,
                    }
                });
        }

        return new ChatClientAgent(
            chatClient,
            instructions,
            member.Attribute.Role,
            $"Team member: {member.Attribute.Role}");
    }

    private List<AITool> BuildMemberTools(TeamMemberInfo member)
    {
        var tools = new List<AITool>();

        foreach (var toolMethod in member.Tools)
        {
            try
            {
                var instance = _sp.GetRequiredService(toolMethod.DeclaringType);
                AITool tool = AIFunctionFactory.Create(
                    method: toolMethod.Method,
                    target: instance,
                    name: toolMethod.ToolName,
                    description: toolMethod.ToolDescription);

                // Team members run inside the group-chat workflow, out of reach of the coordinator's
                // middleware. Wrapping the function carries the gate (RequiredRoles,
                // RequiresConfirmation, feedback, metrics) with it. Team tools are meant to be
                // read-only, so this is usually a pass-through — but it is no longer a hole.
                tools.Add(Middleware.GatedAIFunction.Wrap(tool, _gate));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[MentorAgent] Tool '{Tool}' for TeamMember '{Role}' not found in DI.",
                    toolMethod.ToolName, member.Attribute.Role);
            }
        }

        return tools;
    }

    // -- Manager ---------------------------------------------------------------

    private RoundRobinGroupChatManager BuildManager(
        TeamDiscoveryInfo team,
        IReadOnlyList<ChatClientAgent> participants)
    {
        if (team.TerminationMethod is not null)
        {
            return new DelegateTerminationManager(
                participants,
                team.Attribute.MaxIterations,
                team.TerminationMethod,
                team.Type,
                _sp);
        }

        return new RoundRobinGroupChatManager(participants)
        {
            MaximumIterationCount = team.Attribute.MaxIterations
        };
    }

}

// -- Manager with delegated termination ----------------------------------------

internal sealed class DelegateTerminationManager : RoundRobinGroupChatManager
{
    private readonly MethodInfo _terminationMethod;
    private readonly object? _teamInstance;

    public DelegateTerminationManager(
        IReadOnlyList<AIAgent> agents,
        int maxIterations,
        MethodInfo terminationMethod,
        Type teamType,
        IServiceProvider sp)
        : base(agents)
    {
        MaximumIterationCount = maxIterations;
        _terminationMethod = terminationMethod;

        if (terminationMethod.IsStatic)
        {
            _teamInstance = null;
        }
        else
        {
            // FIX: prefer the DI-registered instance so constructor dependencies
            // are properly injected. Fall back to Activator.CreateInstance only
            // for team classes that are not registered in the container (e.g. a
            // plain POCO with no dependencies).
            _teamInstance = sp.GetService(teamType) ?? Activator.CreateInstance(teamType);
        }
    }

    /// <summary>
    /// Custom termination, <b>on top of</b> the iteration cap rather than instead of it.
    ///
    /// <para>
    /// The base implementation is what enforces <c>MaximumIterationCount</c> — the documentation is
    /// explicit that it decides "based on the provided chat history <i>and iteration count</i>".
    /// Overriding it without consulting it removed the cap altogether, so a
    /// <c>[TeamTerminationCondition]</c> that never returned <see langword="true"/> — a bug in the
    /// host's predicate, or simply a model that never emits the token the predicate looks for —
    /// left the group chat running with no bound at all, one provider call per turn, forever. The
    /// user sees a widget that never finishes; the bill sees something worse.
    /// </para>
    /// </summary>
    protected override async ValueTask<bool> ShouldTerminateAsync(
        IReadOnlyList<ChatMessage> history,
        CancellationToken ct = default)
    {
        if (await base.ShouldTerminateAsync(history, ct).ConfigureAwait(false))
            return true;

        var last = history.LastOrDefault();
        if (last is null) return false;

        try
        {
            var lastMessage = last.Text ?? string.Empty;
            var lastSpeaker = last.AuthorName ?? string.Empty;
            var result = _terminationMethod.Invoke(_teamInstance, [lastMessage, lastSpeaker]);
            return result is bool b && b;
        }
        catch
        {
            // The predicate is the host's own code, invoked reflectively on every message. Letting
            // it abort a turn the user is watching would be worse than ignoring its opinion — and
            // the iteration cap above still bounds the discussion either way.
            return false;
        }
    }
}
