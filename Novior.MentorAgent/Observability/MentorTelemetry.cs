using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Novior.MentorAgent.Observability;

/// <summary>
/// MentorAgent's OpenTelemetry <see cref="ActivitySource"/> and <see cref="Meter"/> (roadmap #8),
/// created from <c>MentorOptions.ObservabilitySourceName</c>. Registered only when
/// <c>EnableObservability</c> is on; the host subscribes with
/// <c>.AddSource(name).AddMeter(name)</c> and an exporter (App Insights / Aspire / OTLP).
///
/// The chat client and embedding generator emit standard GenAI-convention spans/metrics via
/// <c>UseOpenTelemetry()</c>; these instruments add MentorAgent-level turn/tool/error spans and
/// counters on top.
/// </summary>
public sealed class MentorTelemetry : IDisposable
{
    public ActivitySource ActivitySource { get; }
    public Meter Meter { get; }

    public Counter<long> Turns { get; }
    public Counter<long> ToolCalls { get; }
    public Counter<long> Errors { get; }
    public Counter<long> GuardrailBlocks { get; }

    public MentorTelemetry(string sourceName)
    {
        ActivitySource = new ActivitySource(sourceName);
        Meter = new Meter(sourceName);
        // Not namespaces: public contract names. Dashboards and alerts query these metrics, so they stay
        // as they are when the namespace changes (ContractNameTests).
        Turns           = Meter.CreateCounter<long>("mentoragent.turns", "{turn}", "User turns processed.");
        ToolCalls       = Meter.CreateCounter<long>("mentoragent.tool_calls", "{call}", "Tool/action invocations.");
        Errors          = Meter.CreateCounter<long>("mentoragent.errors", "{error}", "Turn-level errors.");
        GuardrailBlocks = Meter.CreateCounter<long>("mentoragent.guardrail_blocks", "{block}", "Messages/replies blocked by an input or output guardrail.");
    }

    /// <summary>Starts a MentorAgent span, or returns <see langword="null"/> when nobody is listening.</summary>
    public Activity? StartActivity(string name) => ActivitySource.StartActivity(name);

    public void Dispose()
    {
        ActivitySource.Dispose();
        Meter.Dispose();
    }
}
