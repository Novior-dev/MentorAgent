using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Novior.MentorAgent.Core;

namespace Novior.MentorAgent.Middleware;

/// <summary>
/// The instructions of a turn in the language of its page (story 536). An agent's instructions are built once, in
/// <c>MentorOptions.Language</c>; a turn in another language is sent, in their place, the instructions the prompt
/// builder writes for that language, built the first time they are needed and then kept for the agent.
///
/// <para>
/// The whole variant, not the language line alone: since BUG-221 the rules the model can copy into a reply are in the
/// language too, and a list here of what to replace would forget, in silence, the next text that follows the language.
/// The builder is the one source.
/// </para>
///
/// <para>
/// The variant replaces the built instructions where the request's instructions start with them, and what follows is
/// kept: the per-request context, the documents, the notices of the filters. So this client sits outside the role
/// filters, which rewrite a line inside the built instructions. When the request's instructions do not start with them
/// the turn still gets its language, as one line at the end, and the log says so once: something before this client
/// changed the instructions. An agent whose instructions are the host's own (no variant to send) gets that
/// line without the warning: a host that writes its prompt owns it, and only the language of the turn is added.
/// </para>
///
/// <para>
/// In the language the instructions were built in, the request is passed on untouched, so the prompt and its cached
/// prefix are the ones of before.
/// </para>
/// </summary>
internal sealed class TurnLanguageChatClient : DelegatingChatClient
{
    private readonly MentorTurnLanguage _turn;
    private readonly MentorLanguage _built;
    private readonly string _builtInstructions;
    private readonly Func<MentorLanguage, string>? _variant;
    private readonly ConcurrentDictionary<MentorLanguage, string> _variants = new();
    private readonly string _agent;
    private readonly ILogger _logger;
    private int _warned;

    internal TurnLanguageChatClient(
        IChatClient inner,
        MentorTurnLanguage turn,
        MentorLanguage built,
        string builtInstructions,
        Func<MentorLanguage, string>? variant,
        string agent,
        ILogger logger)
        : base(inner)
    {
        _turn = turn;
        _built = built;
        _builtInstructions = builtInstructions;
        _variant = variant;
        _agent = agent;
        _logger = logger;
    }

    /// <summary>The line a turn gets when there is no variant to send: for instructions MentorAgent did not write.</summary>
    internal static string FallbackLine(MentorLanguage language)
        => $"ALWAYS respond in **{language.ToFullName()}** for this turn.";

    /// <summary>The agent's client, with the swap when the scope has a language of the turn.</summary>
    internal static IChatClient For(
        IChatClient inner, MentorTurnLanguage? turn, MentorLanguage built, string builtInstructions,
        Func<MentorLanguage, string>? variant, string agent, ILogger logger)
        => turn is null ? inner : new TurnLanguageChatClient(inner, turn, built, builtInstructions, variant, agent, logger);

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => base.GetResponseAsync(messages, ForTurn(options), cancellationToken);

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in base.GetStreamingResponseAsync(messages, ForTurn(options), cancellationToken))
            yield return update;
    }

    /// <summary>A copy of the request's options with the instructions of the turn's language, or the options as they are.</summary>
    private ChatOptions? ForTurn(ChatOptions? options)
    {
        var language = _turn.Current;
        if (language == _built) return options;

        var instructions = options?.Instructions;
        var copy = options?.Clone() ?? new ChatOptions();

        if (_variant is { } variant && instructions is not null
            && instructions.StartsWith(_builtInstructions, StringComparison.Ordinal))
        {
            copy.Instructions = _variants.GetOrAdd(language, variant) + instructions[_builtInstructions.Length..];
            return copy;
        }

        if (_variant is not null && Interlocked.Exchange(ref _warned, 1) == 0)
            _logger.LogWarning(
                "[MentorAgent] The instructions sent to '{Agent}' do not start with the ones MentorAgent built, so a turn in " +
                "{Language} could not be sent the {Language} instructions: it was sent the built ones, with a line asking for " +
                "{Language}. Something before MentorAgent's client changed the start of the instructions.",
                _agent, language, language, language);

        copy.Instructions = string.IsNullOrEmpty(instructions)
            ? FallbackLine(language)
            : instructions + "\n" + FallbackLine(language);
        return copy;
    }
}
