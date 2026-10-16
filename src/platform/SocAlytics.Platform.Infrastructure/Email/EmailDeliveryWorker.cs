using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Infrastructure.Workers;

namespace SocAlytics.Platform.Infrastructure.Email;

// Database-only: polls the email outbox and runs without NATS.
internal sealed class EmailDeliveryWorker(
    IServiceProvider services,
    IOptions<EmailOptions> options,
    IOptions<AnalysisWorkerOptions> workerOptions,
    TimeProvider timeProvider,
    ILogger<EmailDeliveryWorker> logger) : BackgroundService
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
            var loop = new WorkerLoop(
                "email-delivery", timeProvider, logger, options.Value.Delivery.PollInterval, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
            await loop.RunAsync(async ct =>
            {
                int claimed;
                do
                {
                    await using var scope = services.CreateAsyncScope();
                    claimed = await scope.ServiceProvider.GetRequiredService<EmailDeliveryProcessor>().ProcessDueAsync(ct).ConfigureAwait(false);
                }
                while (claimed >= EmailDeliveryProcessor.BatchSize && !ct.IsCancellationRequested);
            }, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogError("Email delivery worker stopped: {Category}", WorkerLoop.Categorize(ex));
        }
    }
}
