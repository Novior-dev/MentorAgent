using Microsoft.Extensions.AI;

namespace Novior.MentorAgent.Abstractions.Models;

/// <summary>
/// A user-supplied image attached to a chat message (roadmap #13 — multimodal input).
/// Serializable so it flows over the SignalR hub, the SSE endpoint and to JS clients.
/// Exactly one source is set: <see cref="DataBase64"/> for an inline upload / paste / drag-drop,
/// or <see cref="Url"/> for a remotely hosted image.
/// </summary>
public sealed record MentorAttachment
{
    /// <summary>MIME type, e.g. <c>image/png</c>, <c>image/jpeg</c>, <c>image/webp</c>. Defaults to PNG.</summary>
    public string MimeType { get; init; } = "image/png";

    /// <summary>Base64-encoded bytes for an inline attachment (no <c>data:</c> prefix). Null when <see cref="Url"/> is used.</summary>
    public string? DataBase64 { get; init; }

    /// <summary>Absolute URL of a remotely hosted image. Null when <see cref="DataBase64"/> is used.</summary>
    public string? Url { get; init; }

    /// <summary>Optional original file name (display only).</summary>
    public string? FileName { get; init; }

    /// <summary>True when this attachment carries inline base64 data (rather than a URL).</summary>
    public bool IsInline => !string.IsNullOrEmpty(DataBase64);

    /// <summary>Approximate decoded byte size of an inline attachment (0 for a URL).</summary>
    public long ApproxBytes => string.IsNullOrEmpty(DataBase64) ? 0 : (long)(DataBase64.Length * 3L / 4);

    /// <summary>
    /// Converts to a Microsoft.Extensions.AI content part for the agent turn — <see cref="UriContent"/>
    /// for a URL, <see cref="DataContent"/> for inline base64. Returns <see langword="null"/> if empty.
    /// </summary>
    public AIContent? ToAIContent()
    {
        if (!string.IsNullOrEmpty(Url))
            return new UriContent(Url, MimeType);
        if (!string.IsNullOrEmpty(DataBase64))
            return new DataContent(Convert.FromBase64String(DataBase64), MimeType);
        return null;
    }

    /// <summary>A browser-renderable <c>src</c> (a <c>data:</c> URI for inline data, or the URL) for thumbnails.</summary>
    public string ToDisplaySrc()
        => !string.IsNullOrEmpty(Url) ? Url! : $"data:{MimeType};base64,{DataBase64}";
}
