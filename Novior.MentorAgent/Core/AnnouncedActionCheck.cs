namespace Novior.MentorAgent.Core;

/// <summary>What a reply that called no tool says about an action (story 526).</summary>
internal enum AnnouncedAction
{
    /// <summary>Nothing to correct: an answer, a question to the user, an offer that waits for a yes, a refusal.</summary>
    None,

    /// <summary>The reply says an action is being done, or is about to be, and none was (BUG-220).</summary>
    Announced,

    /// <summary>The reply says an action was done, and none ran in the turn.</summary>
    Claimed,
}

/// <summary>
/// The question MentorAgent asks after a turn in which the coordinator called no tool, with
/// <see cref="MentorOptions.CheckAnnouncedActions"/>: whether its reply announces or reports an action that did not run.
/// A model answers it, never words searched for in the reply: a prompt rule against announcing moved the miss into an
/// invented "done", twice (BUG-220).
/// </summary>
internal static class AnnouncedActionCheck
{
    internal const string AnnouncedLabel = "ANNOUNCED";
    internal const string ClaimedLabel   = "CLAIMED";
    internal const string NoneLabel      = "NONE";

    /// <summary>
    /// The classifier's instructions. English, whatever the language of the conversation. The label comes first, and with
    /// ANNOUNCED or CLAIMED the name of the tool that does the action, when one of the listed tools does it: the second
    /// request then tells the model that this tool is there to call. Told only "call it, or say that it cannot be done",
    /// beside the notice of an action left out, the model said it could not (the live A/B of the first version).
    /// </summary>
    internal const string Question = """
        You check one reply of an assistant that can act in a software application only by calling its tools. In this
        turn the assistant called no tool, so nothing was done in the application.
        Read the user's message, the assistant's reply and the names of the assistant's tools, and answer with one label:
        ANNOUNCED - the reply says the assistant is doing an action in the application, or is about to do it now,
        without waiting for an answer from the user.
        CLAIMED - the reply says an action in the application has been done.
        NONE - anything else: an answer or an explanation, a question to the user, an offer or a plan that waits for the
        user's yes, a refusal, or a statement that something cannot be done.
        After ANNOUNCED or CLAIMED, write the name of the tool that does that action, exactly as it is listed, when one of
        the tools does it; write nothing after the label when none does. Write nothing after NONE.
        """;

    /// <summary>
    /// The user's message, the reply and the names of the tools the model was sent in the turn, as the classifier reads
    /// them: one line each, bounded, so a long reply costs no more than a few hundred tokens.
    /// </summary>
    internal static string Input(string? userMessage, string reply, IReadOnlyCollection<string> tools)
        => $"User: {RequestClassifier.OneLine(userMessage, 600)}\nAssistant: {RequestClassifier.OneLine(reply, 1200)}" +
           $"\nTools: {RequestClassifier.OneLine(tools.Count == 0 ? "none" : string.Join(", ", tools), 1200)}";

    /// <summary>
    /// The label of <paramref name="answer"/>, or <see langword="null"/> when it starts with none: read from the first
    /// word, by position, never searched for in the text (BUG-105).
    /// </summary>
    internal static AnnouncedAction? Read(string? answer)
    {
        var first = (answer ?? string.Empty).TrimStart().Split([' ', '\n', '\r', '\t', '.', ',', ':', ';', '!', '-'], 2)[0]
            .Trim('*', '"', '\'', '`').ToUpperInvariant();
        return first switch
        {
            AnnouncedLabel => AnnouncedAction.Announced,
            ClaimedLabel   => AnnouncedAction.Claimed,
            NoneLabel      => AnnouncedAction.None,
            _              => null,
        };
    }

    /// <summary>
    /// The tool <paramref name="answer"/> names after its label, when it is one of <paramref name="tools"/>: the first word
    /// after the label, by position, as the tool is named in the list — a dash between them is no word, a dash inside a
    /// name (an MCP server's) is part of it. <see langword="null"/> for none, or for a name not offered.
    /// </summary>
    internal static string? Tool(string? answer, IReadOnlyCollection<string> tools)
    {
        var named = (answer ?? string.Empty)
            .Split([' ', '\n', '\r', '\t', ',', ':', ';', '!'], StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Select(w => w.Trim('*', '"', '\'', '`', '.', '(', ')', '-'))
            .FirstOrDefault(w => w.Length > 0);
        return named is null ? null : tools.FirstOrDefault(t => string.Equals(t, named, StringComparison.OrdinalIgnoreCase));
    }
}
