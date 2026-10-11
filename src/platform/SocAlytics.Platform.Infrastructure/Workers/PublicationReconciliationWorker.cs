using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Analysis.Commands;
using SocAlytics.Platform.Infrastructure.Messaging.Nats;

namespace SocAlytics.Platform.Infrastructure.Workers;

internal sealed class PublicationReconciliationWorker(
    IServiceProvider services,
    NatsConnectionProvider nats,
    NatsTopology topology,
    TransportReconnectSignal signal,
    IOptions<AnalysisReconciliationOptions> options,
    IOptions<AnalysisWorkerOptions> workerOptions,
    TimeProvider timeProvider,
    ILogger<PublicationReconciliationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!workerOptions.Value.Enabled)
        {
            return;
        }

        if (!nats.IsConfigured)
        {
            logger.LogInformation("Publication reconciliation not started: the transport is not configured.");
            return;
        }

        try
        {
            await Task.Yield();
            var health = services.GetRequiredService<HealthCheckService>();
            await WorkerLoop.WaitForDatabaseAsync(health, timeProvider, stoppingToken).ConfigureAwait(false);
            var settings = options.Value;
            var startupPending = settings.OnStartup;
            long handled = settings.OnStartup ? -1 : signal.Count;
            var loop = new WorkerLoop(
                "publication-reconciliation",
                timeProvider,
                logger,
                TimeSpan.FromMilliseconds(500),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(30));
            await loop.RunAsync(async ct =>
            {
                var current = signal.Count;
                var due = startupPending || (settings.OnReconnect && current != handled);
                if (!due)
                {
                    return;
                }

                if (!nats.IsConnected)
                {
                    throw new MessageTransportException(TransportFailureCategories.TransportUnavailable);
                }

                await topology.EnsureAsync(ct).ConfigureAwait(false);
                await using var scope = services.CreateAsyncScope();
                var result = await scope.ServiceProvider.GetRequiredService<ReconcileAnalysisPublicationHandler>()
                    .HandleAsync(ct).ConfigureAwait(false);
                logger.LogInformation(
                    "Publication reconciliation republished {Jobs} ready jobs and {Notifications} notifications",
                    result.ReadyJobsRepublished, result.NotificationsRepublished);
                startupPending = false;
                handled = current;
            }, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogError("Publication reconciliation stopped: {Category}", WorkerLoop.Categorize(ex));
        }
    }
}
