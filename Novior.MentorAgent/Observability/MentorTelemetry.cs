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

    /// <summary>
    /// Knowledge-base searches that failed (story 195): the source threw or answered with no list, and the turn went on
    /// without documents. Tagged with the source (<c>mentoragent.rag.source</c>, its type's full name) and the exception's
    /// type (<c>mentoragent.error.type</c>), never its message. A Stop during the search is not counted.
    /// </summary>
    public Counter<long> RagFailures { get; }

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
        RagFailures     = Meter.CreateCounter<long>("mentoragent.rag_failures", "{failure}", "Knowledge-base searches that failed.");
    }

    /// <summary>The tag that names the RAG source of a measurement or a span: its type's full name (story 195).</summary>
    internal const string RagSourceTag = "mentoragent.rag.source";

    /// <summary>The tag that names the type of an exception, never its message (story 195).</summary>
    internal const string ErrorTypeTag = "mentoragent.error.type";

    /// <summary>Starts a MentorAgent span, or returns <see langword="null"/> when nobody is listening.</summary>
    public Activity? StartActivity(string name) => ActivitySource.StartActivity(name);

    /// <summary>
    /// The tag that names the tenant a measurement belongs to (story 156): on every counter and on the turn's span, only
    /// when the application registered an <c>IMentorTenantResolver</c>, so that with one tenant every series stays as it
    /// was. A public contract name, like the instruments'.
    /// </summary>
    internal const string TenantTag = "mentoragent.tenant";

    /// <summary>The tags of a measurement: the tenant of the turn or the MCP call being served, when there is one.</summary>
    internal static TagList Tags() => WithTenant(new TagList());

    /// <summary>The tags of a measurement: <paramref name="key"/>, and the tenant of the turn or the MCP call being served.</summary>
    internal static TagList Tags(string key, object? value) => WithTenant(new TagList { { key, value } });

    private static TagList WithTenant(TagList tags)
    {
        if (Core.MentorTenantContext.Ambient is { } tenant) tags.Add(TenantTag, tenant);
        return tags;
    }

    public void Dispose()
    {
        ActivitySource.Dispose();
        Meter.Dispose();
    }
}
