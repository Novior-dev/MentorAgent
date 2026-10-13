using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Novior.MentorAgent.Core;

/// <summary>
/// Drives durable persistence of the admin dashboard metrics (roadmap #9) when a custom
/// <see cref="IMentorMetricsStore"/> is registered. On startup it seeds the in-RAM counters from the
/// store's last snapshot; then it flushes the current snapshot on a timer and once more on graceful
/// shutdown. External-source stores (Prometheus/Azure Monitor) leave <c>LoadForSeed</c>/<c>Save</c> as
/// no-ops, so for them this service is effectively idle. Registered only when a non-default store is
/// present, so there is no timer overhead in the default (RAM-only) configuration.
/// </summary>
internal sealed class MentorMetricsPersistenceService : IHostedService, IDisposable
{
    private readonly MentorMetrics _metrics;
    private readonly IMentorMetricsStore _store;
    private readonly MentorOptions _options;
    private readonly ILogger<MentorMetricsPersistenceService> _logger;
    private Timer? _timer;

    public MentorMetricsPersistenceService(
        MentorMetrics metrics,
        IMentorMetricsStore store,
        IOptions<MentorOptions> options,
        ILogger<MentorMetricsPersistenceService> logger)
    {
        _metrics = metrics;
        _store = store;
        _options = options.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Seed RAM from the last persisted snapshot so totals continue across a restart.
        try
        {
            if (await _store.LoadForSeedAsync(cancellationToken).ConfigureAwait(false) is { } seed)
            {
                _metrics.Seed(seed);
                _logger.LogInformation(
                    "[MentorAgent] Metrics restored from store (tokens {In}+{Out}, {Turns} turns).",
                    seed.TotalInputTokens, seed.TotalOutputTokens, seed.TurnCount);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MentorAgent] Metrics seed from store failed — starting fresh.");
        }

        var interval = _options.MetricsPersistenceInterval > TimeSpan.Zero
            ? _options.MetricsPersistenceInterval : TimeSpan.FromSeconds(30);
        _timer = new Timer(_ => Flush(), null, interval, interval);
    }

    private void Flush()
    {
        try
        {
            _store.SaveAsync(_metrics.GetSnapshot()).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MentorAgent] Metrics flush to store failed.");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        // Final flush so the very latest counts are not lost on shutdown.
        try
        {
            await _store.SaveAsync(_metrics.GetSnapshot(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MentorAgent] Final metrics flush to store failed.");
        }
    }

    public void Dispose() => _timer?.Dispose();
}
