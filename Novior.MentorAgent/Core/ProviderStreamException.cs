using Microsoft.Extensions.AI;

namespace Novior.MentorAgent.Core;

/// <summary>
/// A provider failure that arrived inside the stream as an <see cref="ErrorContent"/> instead of being
/// thrown — a 429 on the Responses API, a hosted tool that failed — on a run that produced no text (BUG-117).
/// Raised so that the turn takes the path of any other provider error: the message carries the provider's
/// own wording and code, which <see cref="ProviderErrorClassifier"/> reads and never forwards to the user.
/// </summary>
internal sealed class ProviderStreamException(ErrorContent error)
    : Exception($"{error.Message} (code {error.ErrorCode ?? "-"})")
{
    /// <summary>The provider's error as it arrived in the stream.</summary>
    internal ErrorContent Error { get; } = error;
}
