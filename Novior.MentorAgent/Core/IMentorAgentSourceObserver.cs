namespace Novior.MentorAgent.Core;

/// <summary>
/// Receives, every time a coordinator is built, what each <see cref="IMentorAgentSource"/> loaded and what it left
/// out: a definition that did not load, a tool taken away from an agent. Register it with
/// <c>AddMentorAgentSourceObserver&lt;T&gt;()</c>; it is a singleton, and every observer registered is called.
/// <para>
/// It is called on the path that builds the session, before the first answer: return quickly, and hand anything slow
/// — a call to another service — to a queue of your own. An observer that throws is logged with the exception's type
/// and does not stop the session.
/// </para>
/// </summary>
public interface IMentorAgentSourceObserver
{
    /// <summary>One report per source, every time a coordinator is built — also when the source loaded nothing.</summary>
    void OnAgentsLoaded(MentorAgentSourceReport report);
}

/// <summary>What one <see cref="IMentorAgentSource"/> loaded and left out for one coordinator.</summary>
public sealed record MentorAgentSourceReport
{
    /// <summary>The full type name of the source.</summary>
    public required string Source { get; init; }

    /// <summary>The session the coordinator was built for, as <see cref="MentorAgentSourceContext.SessionId"/>.</summary>
    public string? SessionId { get; init; }

    /// <summary>The names of the agents that joined the coordinator, in the order the source gave them.</summary>
    public required IReadOnlyList<string> Loaded { get; init; }

    /// <summary>What was left out, and why. Empty when everything loaded as written.</summary>
    public required IReadOnlyList<MentorAgentRejection> Rejected { get; init; }

    /// <summary>When the source answered.</summary>
    public required DateTimeOffset Timestamp { get; init; }
}
