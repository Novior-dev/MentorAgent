using System.Runtime.CompilerServices;
using Novior.MentorAgent.Core;

namespace Novior.MentorAgent.Middleware;

/// <summary>
/// The model of an agent under the coordinator — a specialist, a team member, an agent of an
/// <see cref="IMentorAgentSource"/> — is offered only the tools this turn's user may run, by the rule of the
/// coordinator's <see cref="RoleFilterChatClient"/>, the gate's and the MCP server's: a tool that needs a role is
/// offered to a signed-in user who holds one of its roles (BUG-205). The orchestrator reads the user once, as the
/// turn starts (<see cref="MentorTurnContext.DecideOffer"/>), and every agent of the turn is offered its tools by
/// that reading.
///
/// <para>
/// Up to 1.1.0 those agents were offered every tool they hold, and a team member's instructions listed them with
/// their descriptions: the model proposed, or called, an action the gate then refused. One client per agent, made
/// when the agent is built, under its function-invoking client: that one still holds every tool, so a call to one
/// left out reaches the gate and is refused there, as before. A request that leaves a tool out ends its
/// instructions with the coordinator's notice (<see cref="RoleFilterChatClient.Notice"/>), for this request only,
/// as the coordinator's does.
/// </para>
/// </summary>
internal sealed class AgentRoleFilterChatClient : DelegatingChatClient
{
    private readonly MentorAgentUIMiddleware _gate;
    private readonly MentorTurnContext _turn;
    private readonly Func<Func<string, MethodInfo?, bool>, (string Built, string ForUser)?>? _instructions;

    private AgentRoleFilterChatClient(
        IChatClient innerClient,
        MentorAgentUIMiddleware gate,
        MentorTurnContext turn,
        Func<Func<string, MethodInfo?, bool>, (string Built, string ForUser)?>? instructions)
        : base(innerClient)
    {
        _gate = gate;
        _turn = turn;
        _instructions = instructions;
    }

    /// <summary>
    /// The chat client for an agent that holds <paramref name="tools"/>: <paramref name="chatClient"/> itself when
    /// none of them needs a role, so the agent's requests stay as they are and nobody is read for them.
    /// <paramref name="instructions"/>, for an agent whose instructions describe its tools, gives the text as it was
    /// built and as it reads with only the tools the user may run.
    /// </summary>
    internal static IChatClient For(
        IChatClient chatClient,
        IEnumerable<AITool> tools,
        MentorAgentUIMiddleware gate,
        Func<Func<string, MethodInfo?, bool>, (string Built, string ForUser)?>? instructions = null)
    {
        if (gate.Turn is not { } turn
            || !tools.OfType<AIFunction>().Any(f => gate.GetRequiredRoles(f.Name, f.UnderlyingMethod).Length > 0))
            return chatClient;

        turn.AgentsHoldRoleGatedTools = true;
        return new AgentRoleFilterChatClient(chatClient, gate, turn, instructions);
    }

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => base.GetResponseAsync(messages, Filter(options), cancellationToken);

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in base.GetStreamingResponseAsync(messages, Filter(options), cancellationToken))
            yield return update;
    }

    /// <summary>
    /// The request's options without the tools this turn's user may not run, with the instructions written for
    /// those left and the notice at the end: a copy, since the function-invoking client keeps its own. Unchanged
    /// when the user may run every tool of the request.
    /// </summary>
    private ChatOptions? Filter(ChatOptions? options)
    {
        if (!_turn.OfferDecided || options?.Tools is not { Count: > 0 } tools) return options;

        var user = _turn.OfferedTo;
        bool MayRun(string name, MethodInfo? method)
            => _gate.GetRequiredRoles(name, method) is not { Length: > 0 } roles
               || user is not null && roles.Any(user.IsInRole);

        List<AITool> offered = [.. tools.Where(t => t is not AIFunction f || MayRun(f.Name, f.UnderlyingMethod))];
        if (offered.Count == tools.Count) return options;

        var copy = options.Clone();
        copy.Tools = offered;

        if (_instructions?.Invoke(MayRun) is (var built, var forUser) && copy.Instructions is { } text
            && text.IndexOf(built, StringComparison.Ordinal) is var at and >= 0)
            copy.Instructions = string.Concat(text.AsSpan(0, at), forUser, text.AsSpan(at + built.Length));

        // Every tool left out was left out for its roles: signing in may help whoever can sign in.
        var notice = RoleFilterChatClient.Notice(signInMayHelp: user is null && _turn.CanSignIn, _turn.Language);
        copy.Instructions = string.IsNullOrEmpty(copy.Instructions) ? notice : copy.Instructions + "\n\n" + notice;
        return copy;
    }
}
