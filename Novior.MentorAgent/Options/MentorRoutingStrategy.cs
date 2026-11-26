namespace Novior.MentorAgent.Options;

/// <summary>
/// How MentorAgent decides, per turn, whether to serve from the cheap <see cref="MentorOptions.ChatClient"/>
/// or escalate to the <see cref="MentorOptions.StrongChatClient"/> (roadmap #11). Routing is active only
/// when a <see cref="MentorOptions.StrongChatClient"/> is configured; otherwise every turn uses the cheap
/// client. All built-in strategies avoid naive keyword matching on user text.
/// </summary>
public enum MentorRoutingStrategy
{
    /// <summary>
    /// Developer-supplied predicate. Uses <see cref="MentorOptions.UseStrongModelAsync"/> (preferred,
    /// async, sees the whole conversation) or the legacy sync <see cref="MentorOptions.UseStrongModel"/>.
    /// Full control — the escape hatch. This is the default for backward compatibility.
    /// </summary>
    Custom = 0,

    /// <summary>
    /// Embedding-based semantic routing. The latest user message is embedded and compared by cosine
    /// similarity against <see cref="MentorOptions.RoutingComplexExemplars"/> (a small set of
    /// "complex/hard" example turns); it escalates to the strong model when the best score reaches
    /// <see cref="MentorOptions.RoutingThreshold"/>. Multilingual, ~free (one embedding), no keywords.
    /// Requires <see cref="MentorOptions.EmbeddingGenerator"/>; without one, routing is disabled (cheap only).
    /// </summary>
    Semantic,

    /// <summary>
    /// LLM-as-router. A tiny, constrained classification call (defaults to the cheap client, or
    /// <see cref="MentorOptions.RoutingClassifierClient"/>) labels the turn <c>SIMPLE</c> or <c>COMPLEX</c>
    /// and escalates on <c>COMPLEX</c>. Most precise; adds one small LLM call per turn.
    /// </summary>
    Classifier,

    /// <summary>
    /// Model cascade / self-escalation, for calls that do not stream: the cheap model answers first, a
    /// small classifier judges whether the answer fully resolved the request, and if not the call is re-run
    /// on the strong model. A chat turn always streams (widget, hub, SSE), and a stream cannot be taken
    /// back: in a turn it decides before the answer, on the input, as <see cref="Classifier"/> does — one
    /// small call, no cheap answer judged and dropped.
    /// </summary>
    Cascade,
}
