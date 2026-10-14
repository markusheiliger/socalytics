using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Registry.QueueCredentials;
using SocAlytics.Platform.Infrastructure.Messaging.Nats;

namespace SocAlytics.Platform.Infrastructure.Workers;

/// <summary>Retries pending queue revocation pushes with bounded backoff, including after a restart.</summary>
internal sealed class QueueRevocationWorker(
    IServiceProvider services,
    NatsTrust trust,
    IQueueAccessRevoker revoker,
    IOptions<AnalysisWorkerOptions> workerOptions,
    TimeProvider timeProvider,
    ILogger<QueueRevocationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!workerOptions.Value.Enabled || !trust.IsJwt)
        {
            return;
        }

        try
        {
            await Task.Yield();
            var health = services.GetRequiredService<HealthCheckService>();
            await WorkerLoop.WaitForDatabaseAsync(health, timeProvider, stoppingToken).ConfigureAwait(false);
            var loop = new WorkerLoop("queue-revocation", timeProvider, logger, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
            await loop.RunAsync(async ct => await revoker.PushPendingAsync(ct).ConfigureAwait(false), stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogError("Queue revocation worker stopped: {Category}", WorkerLoop.Categorize(ex));
        }
    }
}
