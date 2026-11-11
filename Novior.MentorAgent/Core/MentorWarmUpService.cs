using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Novior.MentorAgent.Core;

/// <summary>
/// Builds one coordinator at start-up, so the <em>first visitor</em> does not pay for it
/// (<see cref="MentorOptions.WarmUpAtStartup"/>).
///
/// <para>
/// What that build does is the point: it embeds the tool catalogue and the routing exemplars into
/// the process-wide cache, opens the shared MCP sessions, and fetches the remote agent cards. All
/// three are shared, so doing them once before anyone is watching removes them from the first
/// message of the first session — measured at 3 seconds on a fresh process and up to 50 with other
/// circuits competing for the same npx start-up.
/// </para>
///
/// <para>
/// Deliberately <b>not</b> blocking: it starts on <see cref="IHostApplicationLifetime.ApplicationStarted"/>,
/// that is after every hosted service has started and a web host is listening, because an
/// application that refuses to boot when a remote MCP server is down would be a worse failure than
/// the slow first message it was trying to avoid. A failure here is logged and the application
/// carries on exactly as if the option were off. An <b>invalid configuration</b> is a different
/// matter: the options are validated when the host starts, and that does stop it.
/// </para>
///
/// <para>
/// Registered whatever the options say, and decided here on the real ones: the registration-time
/// snapshot can be incomplete when the configure callback throws. That exception is reported here,
/// once, as a warning.
/// </para>
/// </summary>
internal sealed class MentorWarmUpService(
    IServiceProvider services,
    IOptions<MentorOptions> options,
    ILogger<MentorWarmUpService> logger,
    IHostApplicationLifetime? lifetime = null,
    MentorRegistrationSnapshot? registration = null) : IHostedService
{
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>Completes when the warm-up has finished (or been skipped) — for tests.</summary>
    internal TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (registration?.ConfigureError is { } error)
            logger.LogWarning(error,
                "[MentorAgent] The AddMentorAgent configuration callback threw while MentorAgent read the " +
                "options it decides at registration time ({Options}); those it had not reached kept their " +
                "defaults. The callback runs twice, once for that snapshot and once for the real options, " +
                "so it must not throw or depend on running once.",
                MentorRegistrationSnapshot.RegistrationTimeOptions);

        // Story 156 — one ChatHistoryProvider instance for the whole application keeps every tenant's conversations, whatever
        // the resolver names: only the provider can key them, and only a factory sees the conversation's scope.
        if (options.Value.ChatHistoryProvider is not null
            && !MentorTenantContext.IsSingleTenant(services.GetService<IMentorTenantResolver>()))
            logger.LogWarning(
                "[MentorAgent] ChatHistoryProvider is one instance for the whole application, and an IMentorTenantResolver " +
                "is registered: that provider serves every tenant's conversations unless it keys them itself. Use " +
                "ChatHistoryProviderFactory, which runs in each conversation's scope, and read IMentorTenant there.");

        if (!options.Value.WarmUpAtStartup)
        {
            // Story 173 — the chat model is warmed by the warm-up, which is off.
            if (options.Value.WarmUpChatModel)
                logger.LogWarning(
                    "[MentorAgent] WarmUpChatModel has no effect without WarmUpAtStartup: the chat model is warmed up by " +
                    "the start-up warm-up, which is off. Turn WarmUpAtStartup on as well.");
            Completed.TrySetResult();
            return Task.CompletedTask;
        }

        // Without a host lifetime (constructed by hand) there is nothing to wait for.
        if (lifetime is null)
            _ = Task.Run(WarmAsync, CancellationToken.None);
        else
            lifetime.ApplicationStarted.Register(() => _ = Task.Run(WarmAsync, CancellationToken.None));
        return Task.CompletedTask;
    }

    private async Task WarmAsync()
    {
        try
        {
            var started = System.Diagnostics.Stopwatch.StartNew();

            await using var scope = services.CreateAsyncScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<MentorOrchestrator>();
            await orchestrator.WarmUpAsync(_stopping.Token);

            logger.LogInformation(
                "[MentorAgent] Warm-up finished in {Elapsed:0} ms — the first visitor starts warm.",
                started.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            // Never a boot failure: the application works without this, it is just slower once.
            logger.LogWarning(ex, "[MentorAgent] Warm-up failed — the first session will pay for it instead.");
        }
        finally
        {
            Completed.TrySetResult();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping.Cancel();
        return Task.CompletedTask;
    }
}
