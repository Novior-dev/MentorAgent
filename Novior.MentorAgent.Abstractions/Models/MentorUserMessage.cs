using System.Text.Json.Serialization;
using Novior.MentorAgent.Abstractions.Core;

namespace Novior.MentorAgent.Abstractions.Models;

/// <summary>
/// A user turn produced by the chat input: the typed text plus any images the user attached
/// (roadmap #13 — multimodal input). Used as the payload of <c>ChatInput.OnSend</c> so text-only
/// and multimodal turns travel through the same callback.
/// </summary>
/// <param name="Text">The typed message. Never null; may be whitespace only when images are attached.</param>
/// <param name="Attachments">Attached images, or <see langword="null"/> for a text-only turn.</param>
public sealed record MentorUserMessage(string Text, IReadOnlyList<MentorAttachment>? Attachments = null)
{
    /// <summary>True when at least one image is attached.</summary>
    public bool HasAttachments => Attachments is { Count: > 0 };

    /// <summary>
    /// The language of this turn: what the assistant answers in, and the language of the texts the server
    /// writes for it. <see langword="null"/> means <c>MentorOptions.Language</c>, read again on every turn.
    /// <c>ChatWidget</c> sets it from its <c>Language</c> parameter.
    /// <para>
    /// On the wire it is the name (<c>"Italian"</c>) or the ISO code (<c>"it"</c>), in any case. Any other
    /// value — a number, a name that is not a <see cref="MentorLanguage"/>, an empty string — is read as
    /// <see langword="null"/>.
    /// </para>
    /// </summary>
    [JsonConverter(typeof(MentorLanguageWireConverter))]
    public MentorLanguage? Language { get; init; }
}
