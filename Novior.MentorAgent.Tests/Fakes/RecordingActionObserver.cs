using System.Collections.Concurrent;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;

namespace Novior.MentorAgent.Tests.Fakes;

/// <summary>An <see cref="IMentorActionObserver"/> that keeps every decision it receives, in order.</summary>
internal sealed class RecordingActionObserver : IMentorActionObserver
{
    private readonly ConcurrentQueue<MentorActionDecision> _seen = new();

    internal IReadOnlyList<MentorActionDecision> Seen => [.. _seen];

    /// <summary>The decisions about <paramref name="tool"/>, as "Outcome/reason".</summary>
    internal IReadOnlyList<string> Steps(string tool)
        => [.. Seen.Where(d => d.ToolName == tool).Select(d => $"{d.Outcome}/{d.ReasonCode}")];

    public ValueTask OnDecisionAsync(MentorActionDecision decision, CancellationToken cancellationToken)
    {
        _seen.Enqueue(decision);
        return ValueTask.CompletedTask;
    }
}
