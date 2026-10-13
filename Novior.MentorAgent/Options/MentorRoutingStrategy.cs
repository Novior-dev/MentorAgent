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
    /// Model cascade / self-escalation. The turn is served by the cheap model first, then a small
    /// classifier judges whether the answer fully resolved the request; if not, the turn is re-run on
    /// the strong model. Best quality/cost trade-off — it only pays for the strong model when the cheap
    /// one actually falls short. On streaming calls it cannot re-run mid-stream, so it pre-decides like
    /// <see cref="Classifier"/> on the input.
    /// </summary>
    Cascade,
}
