using System.Text.Json;

namespace Novior.MentorAgent.Utility;

/// <summary>
/// Internal utility helpers shared across the MentorAgent library.
///
/// Covers two concerns:
/// <list type="bullet">
///   <item><b>String transformations</b> — name conventions used throughout discovery,
///   prompt building, and tool registration.</item>
///   <item><b>Tool result JSON</b> — consistent, properly-escaped JSON payloads
///   returned by built-in AI tools back to the LLM.</item>
/// </list>
/// </summary>
internal static class MentorUtils
{
    // ── String Transformations ────────────────────────────────────────────────

    /// <summary>
    /// Converts a PascalCase, space-separated, or dash-separated name to snake_case.
    /// Also strips the "Async" suffix when present.
    ///
    /// Examples:
    /// <list type="bullet">
    ///   <item><c>"CreateOrderAsync"</c> → <c>"create_order"</c></item>
    ///   <item><c>"Business Analysis Team"</c> → <c>"business_analysis_team"</c></item>
    ///   <item><c>"expense-report"</c> → <c>"expense_report"</c></item>
    /// </list>
    /// </summary>
    public static string ToSnakeCase(string name)
    {
        if (name.EndsWith("Async", StringComparison.Ordinal))
            name = name[..^5];

        // Normalise spaces and dashes to underscores before the PascalCase scan
        name = name.Replace(' ', '_').Replace('-', '_');

        return string.Concat(name.Select((c, i) =>
            i > 0 && char.IsUpper(c) && name[i - 1] != '_'
                ? "_" + char.ToLowerInvariant(c)
                : char.ToLowerInvariant(c).ToString()));
    }

    /// <summary>
    /// Converts a snake_case tool name to a human-readable Title Case display name.
    /// Used in confirmation dialogs and action-feedback UI.
    ///
    /// Example: <c>"cancel_order"</c> → <c>"Cancel Order"</c>
    /// </summary>
    public static string ToFriendlyName(string toolName)
        => System.Globalization.CultureInfo.CurrentCulture.TextInfo
               .ToTitleCase(toolName.Replace('_', ' '));

    // ── Tool Result JSON ──────────────────────────────────────────────────────
    //
    // All helpers return a compact JSON string consumed by the LLM as tool output.
    // String values are properly escaped so that quotes or backslashes in error
    // messages or tool names never break the JSON structure.

    /// <summary>Returns <c>{"success":true}</c>.</summary>
    public static string ToolSuccess()
        => "{\"success\":true}";

    /// <summary>Returns <c>{"success":true,"message":"…"}</c>.</summary>
    public static string ToolSuccess(string message)
        => $"{{\"success\":true,\"message\":\"{EscapeJson(message)}\"}}";

    /// <summary>Returns <c>{"success":true,"action":"…"}</c>.</summary>
    public static string ToolSuccessAction(string actionName)
        => $"{{\"success\":true,\"action\":\"{EscapeJson(actionName)}\"}}";

    /// <summary>Returns <c>{"success":true,"navigated_to":"…","page":"…"}</c>.</summary>
    public static string ToolSuccessNavigation(string url, string pageName)
        => $"{{\"success\":true,\"navigated_to\":\"{EscapeJson(url)}\",\"page\":\"{EscapeJson(pageName)}\"}}";

    /// <summary>Returns <c>{"success":false,"error":"…"}</c>.</summary>
    public static string ToolError(string error)
        => $"{{\"success\":false,\"error\":\"{EscapeJson(error)}\"}}";

    /// <summary>
    /// Returns <c>{"success":false,"declined_by_user":true,…}</c> — the action did not run because
    /// the user said no.
    ///
    /// <para>
    /// Deliberately <b>not</b> a <see cref="ToolError(string)"/>. A refusal used to be reported with
    /// the same <c>{"success":false,"error":"…"}</c> shape as a tool that threw, and the coordinator
    /// prompt gives those two cases opposite instructions — <i>"when an action fails … suggest an
    /// alternative"</i> against <i>"when the user cancels … do NOT suggest alternatives"</i>. The
    /// only thing telling them apart was a localised sentence, different in each of the ten
    /// supported languages, so the model had to infer intent from prose and reliably picked the
    /// wrong rule: on a live run it acknowledged the cancellation and then offered three ways to
    /// retry it (BUG-031).
    /// </para>
    ///
    /// <para>
    /// The instruction rides in the result for the same reason the card substitution notice does:
    /// a rule stated where the case actually occurs beats one sitting in a prompt block next to its
    /// own contradiction.
    /// </para>
    /// </summary>
    public static string ToolDeclined(string message)
        => $"{{\"success\":false,{DeclinedMarker},\"message\":\"{EscapeJson(message)}\"," +
           "\"instruction\":\"The user chose not to run this action in the confirmation dialog, so it did not run: " +
           "nothing was checked and nothing changed. " + DeclinedRule + "\"}";

    /// <summary>What marks a <see cref="ToolDeclined"/> result, for a caller that only sees it as text.</summary>
    internal const string DeclinedMarker = "\"declined_by_user\":true";

    /// <summary>
    /// What the model does after a declined confirmation — the same words wherever the refusal reaches it: the
    /// tool result itself, or the result of a delegation in which a specialist's action was declined (BUG-144).
    /// </summary>
    internal const string DeclinedRule =
        "Tell the user in one short sentence that you did not proceed. " +
        "Do not retry, do not give or guess any other reason, do not describe the state of any record, " +
        "and do not suggest alternatives.";

    /// <summary>Whether a tool result is a <see cref="ToolDeclined"/> refusal.</summary>
    internal static bool IsDeclined(object? result)
        => result?.ToString()?.Contains(DeclinedMarker, StringComparison.Ordinal) == true;

    /// <summary>
    /// Whether a tool result is one of ours saying the action did not run — an error, a refusal, a Stop, a role
    /// check, a request left open: they all open with <c>{"success":false</c>.
    /// </summary>
    internal static bool DidNotRun(object? result)
        => result?.ToString()?.StartsWith("{\"success\":false", StringComparison.Ordinal) == true;

    /// <summary>
    /// Returns <c>{"success":false,"stopped_by_user":true,…}</c> — the user pressed Stop while the
    /// action was waiting. Structurally distinct from both a failure and a refusal for the same
    /// reason as <see cref="ToolDeclined"/>: the user stopped the turn, they did not reject this
    /// particular action, and nothing here needs commenting on.
    /// </summary>
    public static string ToolStopped(string message)
        => $"{{\"success\":false,\"stopped_by_user\":true,\"message\":\"{EscapeJson(message)}\"," +
           "\"instruction\":\"The user stopped the request. Do not continue, retry, or comment.\"}";

    /// <summary>
    /// The action ran, but its result cannot be handed on: <c>OnToolResult</c> threw, and a result the
    /// redaction hook did not see never leaves the gate — as on the MCP server (BUG-147). It opens with
    /// <c>{"success":true</c> on purpose: a result read as a failure gets an action that already ran run
    /// again.
    /// </summary>
    internal static string ToolRanResultWithheld(string message)
        => $"{{\"success\":true,\"result_withheld\":true,\"message\":\"{EscapeJson(message)}\"," +
           "\"instruction\":\"The action ran, but its result cannot be shown. Do not run it again: " +
           "tell the user it was done and that its details are not available.\"}";

    /// <summary>
    /// The answer to an approval request an earlier turn left open in the conversation and that never ran
    /// (BUG-116): Stop while it waited for confirmation, the user declined it but the request then failed, or
    /// the approval-round cap. Sent at the start of the next turn so the conversation is whole again; the model
    /// is told the action never ran and that the message to answer is the new one. Marked <c>not_run</c>, not
    /// <c>declined_by_user</c>: the rule for a confirmation the user has just answered does not apply to it.
    /// </summary>
    public static string ToolLeftOpen(bool declined)
        => "{\"success\":false,\"not_run\":true,\"instruction\":\"" +
           (declined
               ? "The user declined this action in an earlier request, and it never ran. "
               : "This action waited for confirmation in an earlier request that was stopped, and it never ran. ") +
           "Do not run it again and do not mention it: answer the user's latest message.\"}";

    /// <summary>
    /// The answer to an approval request the user confirmed in an earlier request that then failed or was
    /// stopped before its result was recorded (BUG-116): the action may have run, so it is neither reported as
    /// done nor run a second time.
    /// </summary>
    public static string ToolOutcomeUnknown()
        => "{\"outcome_unknown\":true,\"instruction\":\"The user confirmed this action in an earlier request, " +
           "but that request was interrupted before its result was recorded, so it may or may not have run. " +
           "Do not run it again. Answer the user's latest message; if they ask about this action, say that its " +
           "outcome is not known and suggest checking it in the application.\"}";

    /// <summary>
    /// Returns <c>{"success":false,"error":"… not found. Available: …"}</c>.
    /// Used when the AI references an action, page, or skill that does not exist.
    /// </summary>
    public static string ToolNotFound(string itemType, string name, string available)
        => $"{{\"success\":false,\"error\":\"{EscapeJson(itemType)} '{EscapeJson(name)}' not found. Available: {EscapeJson(available)}\"}}";

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Escapes a string for use as a JSON string value.
    ///
    /// <para>
    /// Delegated to <see cref="JsonEncodedText"/> rather than hand-rolled. The previous version
    /// replaced backslash, double-quote, carriage return and newline — which covers the cases people
    /// think of, and misses every other control character. RFC 8259 forbids all of U+0000–U+001F
    /// raw inside a string, so a tool result carrying a <b>tab</b> — an exception message that
    /// formats a table, a path echoed with a tab separator — produced JSON the model could not
    /// parse. Nothing throws in-process when that happens: the malformed payload simply degrades
    /// the next reply, which is the hardest possible symptom to trace back here.
    /// </para>
    /// </summary>
    private static string EscapeJson(string value)
        => JsonEncodedText.Encode(value).ToString();
}
