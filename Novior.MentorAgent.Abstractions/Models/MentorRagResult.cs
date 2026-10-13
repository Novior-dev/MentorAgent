namespace Novior.MentorAgent.Abstractions.Models;

/// <summary>
/// A single result from a RAG (Retrieval-Augmented Generation) search.
/// Used both for injecting context into the AI prompt and for rendering
/// citation chips in the <c>RagSourcePanel</c> component.
/// </summary>
/// <param name="Content">The retrieved document content injected into the system prompt.</param>
/// <param name="SourceUrl">Optional URL of the source document (used as link in citation chips).</param>
/// <param name="Title">Optional display title for the citation chip (falls back to SourceUrl).</param>
/// <param name="Score">Relevance score (higher = more relevant). Informational only.</param>
public sealed record MentorRagResult(
    string Content,
    string? SourceUrl,
    string? Title,
    float Score)
{
    /// <summary>
    /// Facts about the document that the model should weigh with its content — an author, a date, a
    /// version, the product it applies to. Shown to the model next to the document's title, one
    /// <c>key: value</c> per entry, so "the 2025 price list" can be told from the 2026 one.
    /// <see langword="null"/> or empty: nothing is added.
    /// <para>
    /// An init property rather than a constructor parameter, so every source written for the four
    /// positional values keeps compiling. Like the content, it also travels with the citation chips to the
    /// client.
    /// </para>
    /// <code>
    /// new MentorRagResult(text, url, title, score)
    /// {
    ///     Metadata = new Dictionary&lt;string, string&gt; { ["valid from"] = "2026-01-01" },
    /// }
    /// </code>
    /// </summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}
