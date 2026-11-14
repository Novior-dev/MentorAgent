using System.Runtime.CompilerServices;
using Novior.MentorAgent.Core;

namespace Novior.MentorAgent.Middleware;

/// <summary>
/// The model of an agent under the coordinator — a specialist, a team member, an agent of an
/// <see cref="IMentorAgentSource"/> — is offered only the tools this turn's user may run, by the rule of the
/// coordinator's <see cref="RoleFilterChatClient"/>, the gate's and the MCP server's: a tool that needs a role is
/// offered to a signed-in user who holds one of its roles (BUG-205), and one the application's
/// <see cref="IMentorActionPolicy"/> instances do not offer is not offered (BUG-233). The orchestrator reads the user
/// once, as the turn starts (<see cref="MentorTurnContext.DecideOffer"/>), and every agent of the turn is offered its
/// tools by that reading.
///
/// <para>
/// Up to 1.1.0 those agents were offered every tool they hold, and a team member's instructions listed them with
/// their descriptions: the model proposed, or called, an action the gate then refused. One client per agent, made
/// when the agent is built, under its function-invoking client: that one still holds every tool, so a call to one
/// left out reaches the gate and is refused there — for its roles, as before, and, for one the policies did not offer,
/// as not offered, without asking them about the call. A request that leaves a tool out ends its instructions with the
/// coordinator's notice (<see cref="RoleFilterChatClient.Notice"/>), for this request only, as the coordinator's does.
/// </para>
///
/// <para>
/// BUG-233: up to 1.3.0 the policies were asked about these agents' tools only at the call. A policy that decides
/// whether the user may use an action when it is offered, and looks at the record alone at the call — as the README's
/// example did — let the user run, through a specialist or a team, an action it did not offer. They are asked at the
/// agent's first request of the turn, once for each tool, with the agent's kind and name.
/// </para>
/// </summary>
internal sealed class AgentRoleFilterChatClient : DelegatingChatClient
{
    private readonly MentorAgentUIMiddleware _gate;
    private readonly MentorTurnContext _turn;
    private readonly MentorAgentKind _agentKind;
    private readonly string? _agentName;
    private readonly Func<Func<string, MethodInfo?, bool>, (string Built, string ForUser)?>? _instructions;

    // BUG-233 — what the policies answered about this agent's tools in the turn _offerTurn: asked once for each tool.
    private readonly SemaphoreSlim _offerLock = new(1, 1);
    private readonly Dictionary<string, bool> _offered = new(StringComparer.Ordinal);
    private string? _offerTurn;

    private AgentRoleFilterChatClient(
        IChatClient innerClient,
        MentorAgentUIMiddleware gate,
        MentorTurnContext turn,
        MentorAgentKind agentKind,
        string? agentName,
        Func<Func<string, MethodInfo?, bool>, (string Built, string ForUser)?>? instructions)
        : base(innerClient)
    {
        _gate = gate;
        _turn = turn;
        _agentKind = agentKind;
        _agentName = agentName;
        _instructions = instructions;
    }

    /// <summary>
    /// The chat client for an agent that holds <paramref name="tools"/>: <paramref name="chatClient"/> itself when
    /// none of them needs a role and the application registers no action policy, so the agent's requests stay as they
    /// are and nobody is read for them. <paramref name="agentKind"/> and <paramref name="agentName"/> are the agent's,
    /// as its calls carry them to the gate. <paramref name="instructions"/>, for an agent whose instructions describe
    /// its tools, gives the text as it was built and as it reads with only the tools the user may run.
    /// </summary>
    internal static IChatClient For(
        IChatClient chatClient,
        IEnumerable<AITool> tools,
        MentorAgentUIMiddleware gate,
        MentorAgentKind agentKind,
        string? agentName,
        Func<Func<string, MethodInfo?, bool>, (string Built, string ForUser)?>? instructions = null)
    {
        if (gate.Turn is not { } turn) return chatClient;

        var roleGated = tools.OfType<AIFunction>().Any(f => gate.GetRequiredRoles(f.Name, f.UnderlyingMethod).Length > 0);
        if (!roleGated && gate.Policies is not { Any: true }) return chatClient;

        if (roleGated) turn.AgentsHoldRoleGatedTools = true;
        return new AgentRoleFilterChatClient(chatClient, gate, turn, agentKind, agentName, instructions);
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => await base.GetResponseAsync(messages, await FilterAsync(options, cancellationToken), cancellationToken);

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var filtered = await FilterAsync(options, cancellationToken);
        await foreach (var update in base.GetStreamingResponseAsync(messages, filtered, cancellationToken))
            yield return update;
    }

    /// <summary>
    /// The request's options without the tools this turn's user may not run, with the instructions written for
    /// those left and the notice at the end: a copy, since the function-invoking client keeps its own. Unchanged
    /// when the user may run every tool of the request.
    /// </summary>
    private async ValueTask<ChatOptions?> FilterAsync(ChatOptions? options, CancellationToken ct)
    {
        if (!_turn.OfferDecided || options?.Tools is not { Count: > 0 } tools) return options;

        var user = _turn.OfferedTo;
        bool ForItsRoles(string name, MethodInfo? method)
            => _gate.GetRequiredRoles(name, method) is { Length: > 0 } roles
               && (user is null || !roles.Any(user.IsInRole));

        var notOffered = await NotOfferedAsync(tools, user, ForItsRoles, ct);
        bool MayRun(string name, MethodInfo? method) => !ForItsRoles(name, method) && !notOffered.Contains(name);

        List<AITool> offered = [.. tools.Where(t => t is not AIFunction f || MayRun(f.Name, f.UnderlyingMethod))];
        if (offered.Count == tools.Count) return options;

        var copy = options.Clone();
        copy.Tools = offered;

        if (_instructions?.Invoke(MayRun) is (var built, var forUser) && copy.Instructions is { } text
            && text.IndexOf(built, StringComparison.Ordinal) is var at and >= 0)
            copy.Instructions = string.Concat(text.AsSpan(0, at), forUser, text.AsSpan(at + built.Length));

        // Signing in may help whoever can sign in only with a tool left out for its roles, not one a policy left out.
        var leftForRoles = tools.OfType<AIFunction>().Any(f => ForItsRoles(f.Name, f.UnderlyingMethod));
        var notice = RoleFilterChatClient.Notice(signInMayHelp: leftForRoles && user is null && _turn.CanSignIn, _turn.Language);
        copy.Instructions = string.IsNullOrEmpty(copy.Instructions) ? notice : copy.Instructions + "\n\n" + notice;
        return copy;
    }

    /// <summary>
    /// BUG-233 — the tools of the request the application's policies do not offer this agent in this turn: each asked
    /// once a turn, at <see cref="MentorActionStage.Offer"/>, and recorded in the turn, so the gate refuses a call to
    /// one. A tool already left out for its roles is not asked about; one whose policy fails is left out.
    /// </summary>
    private async ValueTask<HashSet<string>> NotOfferedAsync(IList<AITool> tools, System.Security.Claims.ClaimsPrincipal? user,
        Func<string, MethodInfo?, bool> forItsRoles, CancellationToken ct)
    {
        var notOffered = new HashSet<string>(StringComparer.Ordinal);
        if (_gate.Policies is not { Any: true } policies) return notOffered;

        await _offerLock.WaitAsync(ct);
        try
        {
            if (!string.Equals(_offerTurn, _turn.TurnId, StringComparison.Ordinal))
            {
                _offered.Clear();
                _offerTurn = _turn.TurnId;
            }

            foreach (var function in tools.OfType<AIFunction>())
            {
                if (forItsRoles(function.Name, function.UnderlyingMethod)
                    || !_gate.IsApplicationAction(function.Name, _agentKind)) continue;

                if (!_offered.TryGetValue(function.Name, out var isOffered))
                {
                    isOffered = await policies.IsOfferedAsync(
                        _gate.OfferRequest(function, user, _agentName, _agentKind), ct);
                    _offered[function.Name] = isOffered;
                    if (!isOffered) _turn.NotOfferedByPolicies(_agentKind, _agentName, function.Name);
                }
                if (!isOffered) notOffered.Add(function.Name);
            }
        }
        finally
        {
            _offerLock.Release();
        }
        return notOffered;
    }
}
