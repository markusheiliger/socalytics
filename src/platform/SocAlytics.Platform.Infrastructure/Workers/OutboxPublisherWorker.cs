using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Messaging;
using SocAlytics.Platform.Infrastructure.Messaging.Nats;

namespace SocAlytics.Platform.Infrastructure.Workers;

internal sealed class OutboxPublisherWorker(
    IServiceProvider services,
    NatsConnectionProvider nats,
    IOptions<OutboxPublicationOptions> options,
    IOptions<AnalysisWorkerOptions> workerOptions,
    TimeProvider timeProvider,
    ILogger<OutboxPublisherWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!workerOptions.Value.Enabled)
        {
            return;
        }

        if (!nats.IsConfigured)
        {
            logger.LogInformation("Outbox publisher not started: the transport is not configured.");
            return;
        }

        try
        {
            await Task.Yield();
            var health = services.GetRequiredService<HealthCheckService>();
            await WorkerLoop.WaitForDatabaseAsync(health, timeProvider, stoppingToken).ConfigureAwait(false);
            var settings = options.Value;
            var loop = new WorkerLoop(
                "outbox-publisher",
                timeProvider,
                logger,
                TimeSpan.FromSeconds(settings.PollIntervalSeconds),
                TimeSpan.FromSeconds(settings.BackoffBaseSeconds),
                TimeSpan.FromSeconds(settings.BackoffMaxSeconds));
            await loop.RunAsync(async ct =>
            {
                await using var scope = services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<PublishOutboxBatch>().HandleAsync(ct).ConfigureAwait(false);
            }, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogError("Outbox publisher stopped: {Category}", WorkerLoop.Categorize(ex));
        }
    }
}
