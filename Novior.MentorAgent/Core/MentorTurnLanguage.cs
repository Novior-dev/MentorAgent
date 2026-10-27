using Microsoft.Extensions.Options;

namespace Novior.MentorAgent.Core;

/// <summary>
/// The language of this scope's turn. Scoped: one per circuit, connection or request, like <see cref="MentorTurnOrigin"/>.
///
/// <para>
/// A page can ask for a language of its own: <c>ChatWidget.Language</c> travels with every turn, in-process, over the
/// hub and over SSE, as <see cref="MentorUserMessage.Language"/>. The orchestrator sets <see cref="Requested"/> at
/// the start of each turn, and everything that writes for the turn reads <see cref="Current"/>: the instructions, the
/// tool results, the errors, the documents' citation rule and the confirmation's context.
/// </para>
///
/// <para>
/// Without one the turn is in <c>MentorOptions.Language</c>, read again on every turn, so a change made while the
/// application runs applies to the next turn of every session.
/// </para>
/// </summary>
internal sealed class MentorTurnLanguage(IOptions<MentorOptions> options)
{
    private static readonly MentorLocalizer[] Localizers =
        [.. Enum.GetValues<MentorLanguage>().Select(l => new MentorLocalizer(l))];

    private readonly MentorOptions _options = options.Value;

    /// <summary>The language this turn asked for, or <see langword="null"/> for the application's.</summary>
    public MentorLanguage? Requested { get; set; }

    /// <summary>The language of this turn.</summary>
    public MentorLanguage Current => Requested ?? _options.Language;

    /// <summary>The widget's texts in the language of this turn: the errors and the results the server writes.</summary>
    public MentorLocalizer Localizer => Localizers[(int)Current];

    /// <summary>
    /// A language from the wire: the name (<c>"Italian"</c>) or the ISO code (<c>"it"</c>), in any case. Anything else
    /// is <see langword="null"/>, the application's language: a number too, which <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/>
    /// alone would accept ("5" is Portuguese).
    /// </summary>
    public static MentorLanguage? Parse(string? value)
        => MentorLanguageWire.TryParse(value, out var language) ? language : null;
}
