using System.Runtime.CompilerServices;
using Novior.MentorAgent.Core;

namespace Novior.MentorAgent.Middleware;

/// <summary>
/// The coordinator's model is offered only the actions this turn's user may run (BUG-200, BUG-201). The orchestrator
/// decides which ones at the start of every turn, by the gate's rule; this client leaves the others out of every
/// request of the turn, writes the prompt's count of the direct actions again with those the user may run, and adds
/// a notice that some actions are not available.
///
/// <para>
/// Outside the semantic tool filter, so the filter ranks, keeps and requires only actions this user may run: an
/// action that needs a role the user lacks was announced, then refused by the gate (BUG-200), and with
/// <c>RequireActionForChangeRequests</c> the first call could be required to be one (BUG-201). The function-invoking
/// client still holds every tool, so a call to one left out, from an earlier turn or made up, reaches the gate and is
/// refused there, as before.
/// </para>
///
/// <para>
/// The notice is why taking an action away is safe. Without the tool the model can still claim the action — "Stock
/// aggiornato" with no tool called (BUG-118), or an invented result for a withheld hosted tool — and a static rule in
/// the prompt does not stop it. It goes at the end of <see cref="ChatOptions.Instructions"/>, for this request only:
/// as a message, a service-managed history would store it and carry it into later turns. It names no action and no
/// role, as the MCP server tells nobody what it withholds.
/// </para>
///
/// <para>
/// It is written in the language of the turn (BUG-224). English in every language, it held in English and not in
/// Italian: asked by an employee to approve a claim, the model offered the approval, and after "Sì" it submitted a
/// copy of the claim or wrote that it had approved it.
/// </para>
/// </summary>
internal sealed class RoleFilterChatClient(IChatClient innerClient) : DelegatingChatClient(innerClient)
{
    private IReadOnlySet<string> _leftOut = new HashSet<string>();
    private (string Built, string ForUser)? _countLine;
    private string? _notice;

    /// <summary>
    /// Starts a turn: <paramref name="leftOut"/> are the actions its user may not run, by name;
    /// <paramref name="countLine"/> the prompt's sentence about the direct actions as it was built and as it reads
    /// for this user, or null when it does not change; <paramref name="notice"/> what the instructions end with when
    /// an action was left out (<see cref="Notice"/>).
    /// </summary>
    internal void BeginUserTurn(IReadOnlySet<string> leftOut, (string Built, string ForUser)? countLine, string? notice)
    {
        _leftOut = leftOut;
        _countLine = countLine;
        _notice = notice;
    }

    /// <summary>
    /// The notice's heading, English in every language as the other headings of the prompts. It ends with the line
    /// break of this file, as it did when the notice was one literal: the English notice is the one of before, byte
    /// for byte.
    /// </summary>
    private const string Heading = """
        ## Action status for THIS request — MANDATORY, overrides the capability list above

        """;

    /// <summary>
    /// The notice of a turn that left at least one action out, in <paramref name="language"/>, the turn's
    /// (<see cref="MentorPromptTexts.ActionsNotAvailable"/>). <paramref name="signInMayHelp"/>: an action is left out
    /// because nobody is signed in, where a person can sign in — the sentence the gate's refusal used to carry ("sign
    /// in"), without naming what signing in would open, and only for an action the model was not given. It ends, in
    /// every language, with <see cref="MentorPromptTexts.NotAvailableFollowUp"/>.
    /// </summary>
    internal static string Notice(bool signInMayHelp, MentorLanguage language)
    {
        var texts = MentorPromptTexts.For(language);
        var notice = Heading + texts.ActionsNotAvailable;
        if (signInMayHelp) notice += "\n" + texts.SignInMayHelp;
        return notice + "\n" + texts.NotAvailableFollowUp;
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
    /// The request's options without the actions left out, with the count rewritten and the notice at the end: a
    /// copy, since the function-invoking client keeps its own. Unchanged when the user may run every action, so the
    /// cached prefix of the prompt stays as it is.
    /// </summary>
    private ChatOptions? Filter(ChatOptions? options)
    {
        if (_leftOut.Count == 0) return options;

        var leftOut = _leftOut;
        var copy = options?.Clone() ?? new ChatOptions();
        if (copy.Tools is { Count: > 0 } tools)
            copy.Tools = [.. tools.Where(t => t is not AIFunction f || !leftOut.Contains(f.Name))];

        // The first occurrence only: the coordinator's prompt comes first, and what the context providers add after it
        // (memory, documents) is not ours to change.
        if (_countLine is (var built, var forUser) && copy.Instructions is { } text
            && text.IndexOf(built, StringComparison.Ordinal) is var at and >= 0)
            copy.Instructions = string.Concat(text.AsSpan(0, at), forUser, text.AsSpan(at + built.Length));

        if (_notice is { } notice)
            copy.Instructions = string.IsNullOrEmpty(copy.Instructions) ? notice : copy.Instructions + "\n\n" + notice;

        return copy;
    }
}
