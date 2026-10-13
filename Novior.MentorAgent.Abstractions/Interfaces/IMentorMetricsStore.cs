using Novior.MentorAgent.Abstractions.Models;

namespace Novior.MentorAgent.Abstractions.Interfaces;

/// <summary>
/// Optional persistence/source for the admin token &amp; cost dashboard (roadmap #9). By default
/// MentorAgent keeps metrics only in RAM (they reset when the process restarts). Register a custom
/// implementation <b>before</b> <c>AddMentorAgent()</c> to make the dashboard survive restarts or to
/// read aggregated data from an external backend:
///
/// <code>
/// // Local durability — persist a JSON/DB snapshot; the dashboard keeps reading live RAM,
/// // seeded from the last save on startup.
/// builder.Services.AddSingleton&lt;IMentorMetricsStore, FileMetricsStore&gt;();
///
/// // External source — the dashboard reads the aggregate the #8 OpenTelemetry export already
/// // pushed to Prometheus / Azure Monitor (multi-instance, historical).
/// builder.Services.AddSingleton&lt;IMentorMetricsStore, PrometheusMetricsStore&gt;();
///
/// builder.Services.AddMentorAgent(options => { ... });
/// </code>
///
/// The two scenarios use different halves of this contract, so each implementation overrides only
/// the methods it needs (the others keep their no-op defaults):
/// <list type="bullet">
/// <item><b>Local store</b> (file/DB): implements <see cref="LoadForSeedAsync"/> (restore totals at
/// startup) and <see cref="SaveAsync"/> (persist periodically + on shutdown). <see cref="QueryAsync"/>
/// returns <see langword="null"/> so the dashboard serves the live in-RAM snapshot.</item>
/// <item><b>External source</b> (Prometheus/Azure Monitor): implements <see cref="QueryAsync"/> to
/// query the backend the #8 export already writes to. <see cref="SaveAsync"/> is a no-op (OpenTelemetry
/// already exports) and there is nothing to seed.</item>
/// </list>
/// </summary>
public interface IMentorMetricsStore
{
    /// <summary>
    /// Called once at startup so a durable store can restore the previous totals into the in-RAM
    /// counters. Return <see langword="null"/> to start fresh (external sources return
    /// <see langword="null"/> — they serve live via <see cref="QueryAsync"/> instead).
    /// </summary>
    Task<MentorMetricsSnapshot?> LoadForSeedAsync(CancellationToken ct = default)
        => Task.FromResult<MentorMetricsSnapshot?>(null);

    /// <summary>
    /// Called periodically and on graceful shutdown with the current snapshot. Durable stores persist
    /// it (file/DB); external sources leave the default no-op because #8 OpenTelemetry already exports.
    /// </summary>
    Task SaveAsync(MentorMetricsSnapshot snapshot, CancellationToken ct = default)
        => Task.CompletedTask;

    /// <summary>
    /// Called on every dashboard/endpoint read. External sources query the backend and return the
    /// authoritative (possibly multi-instance, historical) aggregate. Local/null stores return
    /// <see langword="null"/>, and the caller serves the live in-RAM snapshot instead.
    /// </summary>
    Task<MentorMetricsSnapshot?> QueryAsync(CancellationToken ct = default)
        => Task.FromResult<MentorMetricsSnapshot?>(null);
}
