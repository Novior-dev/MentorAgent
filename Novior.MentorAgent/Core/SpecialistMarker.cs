namespace Novior.MentorAgent.Core;

/// <summary>
/// The line <c>route_to_specialist</c> puts in front of a delegated request when the coordinator
/// named a specialist: <c>[Specialist requested: NAME]</c>.
///
/// <para>
/// It exists for one reader — the router at the entry of <b>this</b> host's handoff workflow — because
/// a model asked for "the user's request" tidies it and the name is the first thing to go (BUG-076).
/// It is not part of the request, and it must not leave the process: sent to a remote A2A peer it
/// reads as an order to <i>that</i> host's router, and a peer that has a remote agent of the same name
/// obeys it (BUG-080 — two hosts that peer each other passed one question back and forth until a
/// server was killed). One place writes the marker and one place removes it, so the two cannot drift.
/// </para>
/// </summary>
internal static class SpecialistMarker
{
    private const string Prefix = "[Specialist requested: ";

    /// <summary>The form the router's instructions quote (see MentorPromptBuilder).</summary>
    internal const string PromptForm = "[Specialist requested: NAME]";

    /// <summary>The request as the local router receives it.</summary>
    public static string Apply(string request, string? specialist)
        => string.IsNullOrWhiteSpace(specialist)
            ? request
            // The name is written by the model: kept on one line, so the marker is exactly one line.
            : $"{Prefix}{specialist.Trim().ReplaceLineEndings(" ")}]\n{request}";

    /// <summary>The request as anyone <b>outside</b> this process receives it: without the marker.</summary>
    public static string Strip(string text)
    {
        if (!text.StartsWith(Prefix, StringComparison.Ordinal)) return text;

        // The marker is the whole first line, not "up to the first bracket": a name such as
        // "Agent[EU]" would otherwise leave its tail in front of the request.
        var eol = text.IndexOf('\n');
        var line = (eol < 0 ? text : text[..eol]).TrimEnd('\r');
        if (!line.EndsWith(']')) return text;

        return eol < 0 ? string.Empty : text[(eol + 1)..];
    }
}
