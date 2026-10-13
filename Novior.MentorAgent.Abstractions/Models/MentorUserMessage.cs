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
}
