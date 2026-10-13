using Microsoft.Extensions.Logging;

namespace Novior.MentorAgent.Core;

/// <summary>
/// Demotes build-time <see cref="LogLevel.Information"/> to <see cref="LogLevel.Debug"/> for every
/// coordinator after the first, and passes everything else through untouched.
///
/// <para>
/// The first version of this used <c>NullLogger</c>, which was two mistakes in one. It threw the
/// line away instead of lowering it — so an operator who turns Debug on to investigate a specific
/// circuit could not see what that circuit was built with — and it would have swallowed a
/// <b>warning</b> the day someone wrote <c>BuildLog.LogWarning</c>, which is exactly the line a
/// degraded session most needs. Filtering by level makes both impossible by construction.
/// </para>
/// </summary>
internal sealed class QuietBuildLogger(ILogger inner) : ILogger
{
    private readonly ILogger _inner = inner;

    /// <summary>Wraps <paramref name="inner"/>, or returns it unchanged when nothing is quiet.</summary>
    internal static ILogger Wrap(ILogger inner, bool quiet)
        => quiet ? new QuietBuildLogger(inner) : inner;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        => _inner.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel)
        => _inner.IsEnabled(Demote(logLevel));

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
        => _inner.Log(Demote(logLevel), eventId, state, exception, formatter);

    /// <summary>Information becomes Debug; Warning and above are untouched, and so is Debug itself.</summary>
    private static LogLevel Demote(LogLevel level)
        => level == LogLevel.Information ? LogLevel.Debug : level;
}
