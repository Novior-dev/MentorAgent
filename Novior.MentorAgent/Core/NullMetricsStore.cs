using Novior.MentorAgent.Abstractions.Interfaces;

namespace Novior.MentorAgent.Core;

/// <summary>
/// Default <see cref="IMentorMetricsStore"/> — does nothing. Metrics live only in RAM and reset when
/// the process restarts (the historical behaviour). Registered via <c>TryAddSingleton</c>, so a custom
/// store registered before <c>AddMentorAgent()</c> replaces it. All members use the interface's no-op
/// defaults.
/// </summary>
public sealed class NullMetricsStore : IMentorMetricsStore
{
}
