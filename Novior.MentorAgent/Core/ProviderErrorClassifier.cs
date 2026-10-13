using System.Text.RegularExpressions;
using Novior.MentorAgent.Abstractions.Core;

namespace Novior.MentorAgent.Core;

/// <summary>
/// Turns a provider exception into something the user can act on — ROADMAP #33.
///
/// <para>
/// Before this, every failure that was not an Azure content filter produced the same sentence:
/// <i>"An error occurred while processing the response."</i> Part D watched a user paste an
/// intranet image URL, the provider answer <c>Unable to download image from
/// http://localhost:5224/favicon.png</c>, and that sentence appear — while the log held both the
/// cause and the exact URL. The person who could have fixed it in two seconds was told nothing.
/// </para>
///
/// <para><b>The rule that keeps this safe.</b> A classification is only made when <b>the user can
/// act on it</b>. An expired key, a missing deployment, a firewall rule are operator problems: they
/// stay generic, because naming them neither helps the reader nor is something to tell whoever is
/// probing the application. Nothing here ever forwards the provider's own text —
/// <c>IncludeWorkflowExceptionDetails</c> keeps its meaning and its default, and the full exception
/// keeps going to the log either way.</para>
///
/// <para>
/// Matching is on the provider's message because that is all there is: Azure OpenAI, OpenAI and
/// Ollama report these conditions with different status codes and different exception types but
/// very stable wording. Each rule is anchored on a phrase the provider owns, and an unmatched
/// message falls through to the generic answer rather than to a guess.
/// </para>
/// </summary>
public static class ProviderErrorClassifier
{
    /// <summary>The URL out of "Unable to download image from &lt;url&gt;". The user supplied it.</summary>
    private static readonly Regex ImageUrl = new(
        @"[Uu]nable to (?:download|fetch|retrieve) image from\s+(?<url>\S+?)[.,\s]*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// A user-facing sentence for <paramref name="ex"/>, in the localizer's language.
    /// Never contains provider text, an identifier, or a token count.
    /// </summary>
    public static string Describe(Exception ex, MentorLocalizer localizer)
    {
        var message = ex?.Message ?? string.Empty;

        // ── Actionable, in the order a real failure is most likely to be one of them ──────────

        // The user pasted a link the provider cannot reach. The URL is theirs, so it goes back.
        var image = ImageUrl.Match(message);
        if (image.Success)
            return string.Format(localizer.Get("error.imageUnreachable"), image.Groups["url"].Value.TrimEnd('.'));

        // Same condition, no URL to quote (some providers omit it).
        if (Contains(message, "unable to download image", "invalid image url", "image_url is not accessible"))
            return localizer.Get("error.imageUnreachableNoUrl");

        // The same failure as above, worded by the Responses API instead of Chat Completions
        // (BUG-061). It says only:
        //
        //     HTTP 400 (invalid_request_error: invalid_value)
        //     Parameter: url
        //     Error while downloading file.
        //
        // so there is no URL to quote back and the no-URL variant is the best answer available.
        // The PAIR is what makes this safe: "error while downloading file" on its own also covers
        // a vector-store document or another operator-supplied file, which is not the reader's to
        // fix. Only when the provider names the `url` parameter is the failing input the one the
        // user handed us.
        if (Contains(message, "error while downloading file") && Contains(message, "parameter: url"))
            return localizer.Get("error.imageUnreachableNoUrl");

        // The conversation outgrew the window. The remedy is the Reset button already on screen.
        if (Contains(message, "context_length_exceeded", "maximum context length", "context length exceeded",
                     "reduce the length of the messages"))
            return localizer.Get("error.contextTooLong");

        // Provider-side throttling — distinct from MentorAgent's own rate limiter, which refuses
        // before any call and has its own message with a countdown.
        if (Contains(message, "rate limit", "rate_limit_exceeded", "429", "too many requests",
                     "exceeded token rate limit"))
            return localizer.Get("error.providerBusy");

        // The provider's own safety system. Kept on the existing guardrail wording so a user sees
        // one voice whether the block came from ours or theirs.
        if (Contains(message, "content_filter", "content management policy", "responsible ai"))
            return localizer.Get("guardrail.blocked");

        // The image was accepted and then rejected on size/format by the provider rather than by
        // MentorAgent's own validation — still the user's file, still fixable by them.
        if (Contains(message, "image is too large", "unsupported image", "invalid image format"))
            return localizer.Get("error.imageRejected");

        if (Contains(message, "timed out", "timeout", "operation was canceled by the server"))
            return localizer.Get("error.timeout");

        // ── Everything else, operator problems included, is deliberately indistinguishable ─────
        //
        // 401/403, a missing deployment, a firewall rule, a quota exhaustion: all real, none of
        // them the reader's to fix, and each one names something — a key fragment, a deployment,
        // a network policy — that does not belong in a chat bubble.
        return localizer.Get("error.generic");
    }

    private static bool Contains(string haystack, params string[] needles)
    {
        foreach (var n in needles)
            if (haystack.Contains(n, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
