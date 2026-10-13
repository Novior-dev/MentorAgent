using System.Text.RegularExpressions;

namespace Novior.MentorAgent.Core;

/// <summary>
/// The question <see cref="MentorOptions.RefuseOutOfScope"/> asks: should the assistant answer this message
/// at all? (BUG-105)
///
/// <para>
/// It used to be the hosted-tool cost gate's question, verbatim — "IN only when the request is about the
/// application … OUT when the subject is anything else, even if one of those capabilities could technically
/// produce it" (BUG-050). There a false OUT withholds a web search and the question is still answered from
/// the application's own tools; here a false OUT ends the turn. And the classifier was shown the
/// application's name, description and page NAMES, never what it holds, so "Quanto costa il Dell XPS 15?"
/// read as general knowledge: 4 of 4 catalogue price questions refused on the published rc.13, 3 of 4 even
/// against the plain scope.
/// </para>
///
/// <para>
/// So the refusal asks a question of its own, whose error costs point the other way: it defaults to
/// answering, tells the classifier that a name it does not recognise may be one of the application's own
/// records, and refuses only a subject that plainly has nothing to do with the application. LLM-only, as
/// every in/out decision in this library: an embedding threshold cannot make it (cosine is not calibrated)
/// and a keyword list would only decline wordings.
/// </para>
/// </summary>
internal static class OutOfScopeRefusal
{
    /// <summary>
    /// What the refusal judges against: <see cref="MentorOptions.HostedToolDomainScope"/> when the host
    /// wrote one, otherwise the application's name and description and its pages WITH their descriptions —
    /// never the hosted-tool capability rule, which belongs to the cost gate.
    /// </summary>
    internal static string BuildScope(MentorOptions options, DiscoveryResult? discovery)
    {
        if (!string.IsNullOrWhiteSpace(options.HostedToolDomainScope))
            return options.HostedToolDomainScope!;

        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(options.AppDescription))
            parts.Add($"{options.AppName}: {options.AppDescription}");
        else if (!string.IsNullOrWhiteSpace(options.AppName))
            parts.Add(options.AppName!);

        if (discovery is { Pages.Count: > 0 })
            parts.Add("Sections: " + string.Join(", ", discovery.Pages.Select(p =>
                string.IsNullOrWhiteSpace(p.Description) ? p.Name : $"{p.Name} ({p.Description})")));

        return string.Join(" ", parts);
    }

    /// <summary>
    /// The question itself, without the answer format — the combined safety call and the stand-alone call
    /// each add their own.
    /// </summary>
    /// <param name="scope">From <see cref="BuildScope"/>.</param>
    /// <param name="configured">
    /// What the application is equipped with (MCP servers, image input, hosted tools, memory, skills), or
    /// empty. Using one of those is part of the application's work, but — the BUG-050 lesson — it does not
    /// make an unrelated subject the application's business.
    /// </param>
    internal static string Question(string scope, string configured)
    {
        var equipped = string.IsNullOrWhiteSpace(configured)
            ? string.Empty
            : $"\nThe application is also equipped with: {configured}. Asking the assistant to use one of " +
              "those for the application's own work is ANSWER; the capability alone does not make an " +
              "unrelated subject ANSWER.";

        return
            "Decide whether the assistant should ANSWER the USER MESSAGE, or REFUSE it because it has " +
            "nothing to do with the application below.\n" +
            $"APPLICATION: {scope}{equipped}\n" +
            "The application keeps its own records (products, customers, orders, documents and the like) and " +
            "you cannot see them. A name you do not recognise (a product, a brand, a person, a code) may be " +
            "one of those records: a question about its price, availability, status or details is ANSWER.\n" +
            "Greetings, thanks, short follow-ups and replies that only make sense inside the conversation " +
            "(\"and the second one?\", \"yes, go ahead\"), requests about the conversation itself (repeat, " +
            "summarise, remember or forget what the user said) and questions about what the assistant can do " +
            "are ANSWER.\n" +
            "REFUSE only when the subject plainly has nothing to do with this application, such as creative " +
            "writing, the weather, sport or general knowledge unrelated to its business. If unsure, ANSWER.";
    }
}

/// <summary>
/// Reads a label from a classifier's reply by POSITION — the n-th word — instead of searching the whole
/// reply for it (BUG-105: <c>Contains("OUT")</c> matched "about", "without" and "outside", so a verdict with
/// a word of explanation flipped). This parses the classifier's own constrained output, never user text.
/// </summary>
internal static class TurnClassifierLabels
{
    private static readonly Regex Word = new(@"\p{L}+", RegexOptions.CultureInvariant);

    /// <summary>
    /// <see langword="true"/> when the word at <paramref name="position"/> is <paramref name="yes"/>,
    /// <see langword="false"/> when it is <paramref name="no"/>, <see langword="null"/> otherwise.
    /// </summary>
    internal static bool? Read(string? reply, int position, string yes, string no)
    {
        if (string.IsNullOrWhiteSpace(reply)) return null;

        var words = Word.Matches(reply);
        if (words.Count <= position) return null;

        var word = words[position].Value;
        if (word.Equals(yes, StringComparison.OrdinalIgnoreCase)) return true;
        if (word.Equals(no, StringComparison.OrdinalIgnoreCase)) return false;
        return null;
    }

    /// <summary>The reply as it may appear in a Debug line: trimmed, one line, at most 40 characters.</summary>
    internal static string Shown(string? reply)
    {
        var text = (reply ?? string.Empty).Trim().ReplaceLineEndings(" ");
        return text.Length > 40 ? text[..40] + "…" : text;
    }
}
