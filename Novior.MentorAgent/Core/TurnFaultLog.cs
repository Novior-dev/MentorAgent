namespace Novior.MentorAgent.Core;

/// <summary>
/// One stack trace per cause, per turn.
///
/// <para>
/// A single provider failure — an expired key, a rate limit, a network blip — is met by every
/// component in the turn in sequence: RAG retrieval, the safety classifier, the tool-filter
/// embeddings, the router, the hosted-tool gate, and finally the turn itself. Each one degrades
/// correctly and each one logs the exception it caught, so one 401 produced <b>five stack traces</b>
/// of the same exception and the reader had to work out that they were one event.
/// </para>
///
/// <para>
/// The first failure of a turn is logged in full. A later one carrying the same type and message is
/// logged as a line naming the cause; a genuinely different failure keeps its own trace, because two
/// causes are two things to fix.
/// </para>
/// </summary>
internal sealed class TurnFaultLog
{
    private string? _first;

    /// <summary>Called at the start of every turn: causes do not carry across turns.</summary>
    internal void Reset() => _first = null;

    internal void Warn(ILogger logger, Exception ex, string message, params object?[] args)
        => Write(logger, LogLevel.Warning, ex, message, args);

    internal void Error(ILogger logger, Exception ex, string message, params object?[] args)
        => Write(logger, LogLevel.Error, ex, message, args);

    private void Write(ILogger logger, LogLevel level, Exception ex, string message, object?[] args)
    {
        var cause = $"{ex.GetType().Name}: {ex.Message}";

        if (_first is null)
        {
            _first = cause;
            logger.Log(level, ex, message, args);
            return;
        }

        if (_first == cause)
        {
            logger.Log(level, message + " (same cause as the first failure in this turn: {Cause})",
                [.. args, cause]);
            return;
        }

        logger.Log(level, ex, message, args);
    }
}
