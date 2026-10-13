using Novior.MentorAgent.Abstractions.Models;

namespace Novior.MentorAgent.Abstractions.Interfaces;

/// <summary>
/// Process-wide sink for operational usage metrics powering the admin token &amp; cost dashboard
/// (roadmap #9). Recording methods are called by the engine; <see cref="GetSnapshot"/> is read by
/// the dashboard component and the admin endpoint. This is an operator/admin instrument and must
/// never be surfaced in the end-user widget.
/// </summary>
public interface IMentorMetrics
{
    /// <summary>A user message (turn) was received.</summary>
    void RecordTurn();

    /// <summary>A turn completed without an error or guardrail block (deflection).</summary>
    void RecordServed();

    /// <summary>A turn ended in an error.</summary>
    void RecordError();

    /// <summary>A message was blocked by the input guardrail.</summary>
    void RecordGuardrailBlock();

    /// <summary>A tool/action was invoked (feeds the "top actions" list).</summary>
    void RecordToolCall(string toolName);

    /// <summary>Builds a point-in-time snapshot (cost included only if pricing is configured).</summary>
    MentorMetricsSnapshot GetSnapshot();

    /// <summary>Resets all counters (e.g. to start a fresh measurement window).</summary>
    void Reset();
}
