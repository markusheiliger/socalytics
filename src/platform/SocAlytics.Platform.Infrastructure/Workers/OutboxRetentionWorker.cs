using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Messaging;

namespace SocAlytics.Platform.Infrastructure.Workers;

// Database-only: runs without NATS.
internal sealed class OutboxRetentionWorker(
    IServiceProvider services,
    IOptions<OutboxPublicationOptions> options,
    IOptions<AnalysisWorkerOptions> workerOptions,
    TimeProvider timeProvider,
    ILogger<OutboxRetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!workerOptions.Value.Enabled)
        {
            return;
        }

        try
        {
            await Task.Yield();
            var health = services.GetRequiredService<HealthCheckService>();
            await WorkerLoop.WaitForDatabaseAsync(health, timeProvider, stoppingToken).ConfigureAwait(false);
            var settings = options.Value;
            var loop = new WorkerLoop(
                "outbox-retention",
                timeProvider,
                logger,
                TimeSpan.FromMinutes(Math.Max(1, settings.RetentionIntervalMinutes)),
                TimeSpan.FromSeconds(settings.BackoffBaseSeconds),
                TimeSpan.FromSeconds(settings.BackoffMaxSeconds));
            await loop.RunAsync(async ct =>
            {
                await using var scope = services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<PurgeOutboxMessagesHandler>().HandleAsync(ct).ConfigureAwait(false);
            }, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogError("Outbox retention worker stopped: {Category}", WorkerLoop.Categorize(ex));
        }
    }
}
