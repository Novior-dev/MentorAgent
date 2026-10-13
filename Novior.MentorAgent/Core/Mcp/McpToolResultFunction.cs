using ModelContextProtocol.Protocol;

namespace Novior.MentorAgent.Core.Mcp;

/// <summary>
/// An application action as the MCP server invokes it: the action, then
/// <see cref="MentorOptions.OnToolResult"/> (BUG-107).
///
/// <para>
/// The hook runs on the function's own result — before the SDK turns it into content blocks — so it
/// receives exactly what it receives in a chat turn, and a redaction written against that matches here
/// too. Applying it to the finished <see cref="CallToolResult"/> instead would hand the hook a different
/// type, and a hook that matched nothing would leak silently.
/// </para>
///
/// <para>
/// A hook that throws never lets the unredacted result out: the caller gets a result marked
/// <c>isError</c>, and the host a Warning. Nothing is thrown — the SDK logs any exception out of a tool
/// as a crash (BUG-101), and the tool itself did run.
/// </para>
/// </summary>
internal sealed class McpToolResultFunction(AIFunction inner, MentorOptions options, ILogger logger)
    : DelegatingAIFunction(inner)
{
    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var result = await base.InvokeCoreAsync(arguments, cancellationToken);

        try
        {
            return MentorToolResult.Apply(options, Name, result);
        }
        catch (Exception ex)
        {
            // The type only, as on the chat path: the hook's message can quote the result it was redacting.
            logger.LogWarning(
                "[MentorAgent:MCP] OnToolResult threw for '{Tool}' ({Type}) — the result was withheld from the MCP caller.",
                Name, ex.GetType().Name);
            logger.LogDebug(ex, "[MentorAgent:MCP] OnToolResult threw for '{Tool}'.", Name);

            return new CallToolResult
            {
                IsError = true,
                Content =
                [
                    new TextContentBlock
                    {
                        Text = options.OnException?.Invoke(ex) is { Length: > 0 } custom
                            ? custom
                            : "The tool ran, but its result could not be returned.",
                    },
                ],
            };
        }
    }
}
