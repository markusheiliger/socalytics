using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Analysis.Commands;

namespace SocAlytics.Platform.Infrastructure.Workers;

// Needs no NATS: requeued work only enqueues outbox rows.
internal sealed class LeaseExpiryWorker(
    IServiceProvider services,
    IOptions<AnalysisLeaseOptions> options,
    IOptions<AnalysisWorkerOptions> workerOptions,
    TimeProvider timeProvider,
    ILogger<LeaseExpiryWorker> logger) : BackgroundService
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
            var interval = TimeSpan.FromSeconds(Math.Max(1, options.Value.MonitorIntervalSeconds));
            var loop = new WorkerLoop("lease-expiry", timeProvider, logger, interval, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(60));
            await loop.RunAsync(async ct =>
            {
                await using var scope = services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ExpireStaleAttemptsHandler>().HandleAsync(ct).ConfigureAwait(false);
            }, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogError("Lease expiry worker stopped: {Category}", WorkerLoop.Categorize(ex));
        }
    }
}
