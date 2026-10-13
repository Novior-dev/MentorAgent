using Microsoft.Extensions.Logging;

namespace Novior.MentorAgent.Tests.Harness;

/// <summary>
/// Adapts the plain <see cref="ILogger"/> that <see cref="TestLoggerProvider"/> hands out to the
/// <see cref="ILogger{TCategoryName}"/> most of MentorAgent's services ask for in their constructor.
///
/// <para>
/// Every suite that news a service up directly — rather than resolving it from
/// <see cref="MentorHostFixture"/> — needs this, because the log is where half the documented
/// behaviour lives ("warn and degrade"). It started as a private copy in one file; this is the
/// third, so it lives here now.
/// </para>
/// </summary>
internal sealed class TypedLogger<T>(ILogger inner) : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);
    public bool IsEnabled(LogLevel level) => inner.IsEnabled(level);

    public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex,
                            Func<TState, Exception?, string> formatter)
        => inner.Log(level, id, state, ex, formatter);
}
