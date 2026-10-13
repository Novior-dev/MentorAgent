namespace Novior.MentorAgent.Core;

/// <summary>
/// <see cref="MentorOptions.OnToolResult"/>, in one place: the chat middleware and the MCP server apply
/// the hook through this, so a host's redaction runs on every surface that returns a tool's result, and
/// receives the same value on each (BUG-107).
/// </summary>
internal static class MentorToolResult
{
    /// <summary>The result after the host's hook, or the result itself when no hook is set.</summary>
    internal static object? Apply(MentorOptions options, string toolName, object? result)
        => options.OnToolResult is { } transform ? transform(toolName, result) : result;
}
