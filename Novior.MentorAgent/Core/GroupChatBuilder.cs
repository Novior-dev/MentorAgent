
using Novior.MentorAgent.Options;

namespace Novior.MentorAgent.Core;

/// <summary>
/// Builds the Group Chat workflow (Level 3) from [MentorTeam] classes.
///
/// Each [TeamMember] can have read-only tools for analyzing real data.
/// When the team approves (<see cref="TeamDiscussion.IsApproved"/>), it can hand off
/// to L2 agents via HandoffTo.
/// </summary>
internal sealed class GroupChatBuilder
{
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

        var toolDescription = TeamToolDescription(team.Attribute.Description, team.Attribute.HandoffTo);

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
                var discussion = new TeamDiscussion();
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

                    discussion.Add(update);
                }

                // ── Strip internal sentinel tokens before returning to the coordinator ─
                // [TEAM:APPROVED] and [TEAM:REJECTED] are coordination signals only.
                // They must not appear in the user-visible response.
                var cleanText = discussion.TextWithoutVerdicts;

                // ── Handoff after structured approval ─────────────────────────
                // The structured sentinel, not a bare word like "APPROVED", so that "NOT APPROVED" or
                // "the plan has not been approved yet" is no consent; and read message by message, not
                // anywhere in the text (BUG-175).
                if (team.Attribute.HandoffTo.Length > 0)
                {
                    if (discussion.IsApproved)
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
            description: toolDescription);

        return teamTool;
    }

    /// <summary>
    /// The team tool's description: the team's own, then where it hands off after approval. A description
    /// written without a full stop ran into the note — "… ready to be closed After approval, hand off to: …" —
    /// so one is added before it when missing (BUG-181).
    /// </summary>
    internal static string TeamToolDescription(string description, string[] handoffTo)
    {
        if (handoffTo.Length == 0) return description;

        var note = $"After approval, hand off to: {string.Join(", ", handoffTo)}.";
        var own = description.TrimEnd();
        if (own.Length == 0) return note;

        return ".!?".Contains(own[^1]) ? $"{own} {note}" : $"{own}. {note}";
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

        // No condition of the host's: the first verdict ends the discussion, so the message carrying it
        // is the final one the approval is read from (BUG-175). A host's condition chooses who decides,
        // and is left to decide alone.
        return new VerdictTerminationManager(participants)
        {
            MaximumIterationCount = team.Attribute.MaxIterations
        };
    }

}

// -- The discussion, message by message ----------------------------------------

/// <summary>
/// A team's discussion as it streams, kept message by message with each message's author, and the
/// verdict read from it.
///
/// <para>
/// Up to 1.0.0 the team was approved when <see cref="Approved"/> appeared anywhere in the discussion:
/// a member who agreed halfway and a last one who said no still sent the coordinator "Now execute the
/// approved actions" (BUG-175). The protocol asks for the verdict in the final message only.
/// </para>
/// </summary>
internal sealed class TeamDiscussion
{
    /// <summary>
    /// Structured token a member includes in the final decision message to signal approval. A bracketed
    /// tag is far less likely to appear by accident in normal discussion than a bare word like "APPROVED".
    /// </summary>
    internal const string Approved = "[TEAM:APPROVED]";

    /// <summary>A member's definitive no. A request for changes is written without a token.</summary>
    internal const string Rejected = "[TEAM:REJECTED]";

    private readonly List<(string? Author, StringBuilder Text)> _messages = [];
    private string? _messageId;

    /// <summary>
    /// Adds one streamed update. A new message starts when the author changes, or when the message id
    /// changes where the provider sets one: without ids, a member who speaks twice in a row stays one
    /// message.
    /// </summary>
    internal void Add(AgentResponseUpdate update)
    {
        var author = update.AuthorName is { Length: > 0 } a ? a : null;
        var id = update.MessageId is { Length: > 0 } i ? i : null;

        if (_messages.Count == 0
            || (author is not null && author != _messages[^1].Author)
            || (id is not null && _messageId is not null && id != _messageId))
        {
            _messages.Add((author ?? (_messages.Count > 0 ? _messages[^1].Author : null), new StringBuilder()));
            _messageId = id;
        }
        else
        {
            _messageId ??= id;
        }

        if (update.Text is { Length: > 0 } text)
            _messages[^1].Text.Append(text);
    }

    /// <summary>
    /// The whole discussion as one text, as the coordinator receives it: one paragraph per message, headed by
    /// its author when the provider names one. Joined as they streamed, "… is solved." and "I agree" read
    /// "… is solved.I agree", and the coordinator could not tell who said what (BUG-181).
    /// </summary>
    internal string Text => Join(said => said);

    /// <summary>
    /// <see cref="Text"/> without the verdict tokens, which are coordination signals and never shown. Removed
    /// message by message, so a message that carried only a token leaves no paragraph and no author behind.
    /// </summary>
    internal string TextWithoutVerdicts => Join(said => said
        .Replace(Approved, string.Empty, StringComparison.OrdinalIgnoreCase)
        .Replace(Rejected, string.Empty, StringComparison.OrdinalIgnoreCase));

    private string Join(Func<string, string> shape) => string.Join("\n\n", _messages
        .Select(m => (m.Author, Said: shape(m.Text.ToString()).Trim()))
        .Where(m => m.Said.Length > 0)
        .Select(m => m.Author is null ? m.Said : $"{m.Author}: {m.Said}"));

    /// <summary>
    /// The team's decision under the rule chosen for 1.1.0: approved only if the final message carries
    /// <see cref="Approved"/> and no message carries <see cref="Rejected"/>. A "yes" followed by a "no"
    /// is no; a "no" is a veto even if a later member agrees; and a discussion that ran out of rounds
    /// without a verdict in its final message decided nothing.
    /// </summary>
    internal bool IsApproved
    {
        get
        {
            var spoken = _messages
                .Select(m => m.Text.ToString())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToList();

            return spoken.Count > 0
                && spoken[^1].Contains(Approved, StringComparison.OrdinalIgnoreCase)
                && !spoken.Any(t => t.Contains(Rejected, StringComparison.OrdinalIgnoreCase));
        }
    }
}

// -- Default manager: the first verdict ends the discussion ----------------------

/// <summary>
/// The round robin a team without a <c>[TeamTerminationCondition]</c> runs: it stops at the first
/// member message carrying <see cref="TeamDiscussion.Approved"/> or <see cref="TeamDiscussion.Rejected"/>,
/// or at the iteration cap.
///
/// <para>
/// Up to 1.0.0 the plain round robin ran every round (<c>MaxIterations</c>, 10 by default) whatever the
/// members said. With the verdict read from the final message (BUG-175), a "yes" at the third message
/// would have been followed by seven more and lost. A host's own condition is not combined with this:
/// it is how a host chooses which member decides (the README's example stops on the approver only).
/// </para>
/// </summary>
internal sealed class VerdictTerminationManager : RoundRobinGroupChatManager
{
    public VerdictTerminationManager(IReadOnlyList<AIAgent> agents) : base(agents) { }

    protected override async ValueTask<bool> ShouldTerminateAsync(
        IReadOnlyList<ChatMessage> history,
        CancellationToken ct = default)
    {
        if (await base.ShouldTerminateAsync(history, ct).ConfigureAwait(false))
            return true;

        // A member's message only: the task the team was given is in the history too, and a user who
        // typed a token must not end the discussion before anyone has spoken.
        return history.LastOrDefault() is { Role: var role, Text: var text }
            && role == ChatRole.Assistant
            && (text.Contains(TeamDiscussion.Approved, StringComparison.OrdinalIgnoreCase)
                || text.Contains(TeamDiscussion.Rejected, StringComparison.OrdinalIgnoreCase));
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
