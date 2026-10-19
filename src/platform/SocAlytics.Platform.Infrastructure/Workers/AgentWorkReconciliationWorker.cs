using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.AgentOrchestration;
using SocAlytics.Platform.Application.AgentOrchestration.Execution;

namespace SocAlytics.Platform.Infrastructure.Workers;

// Needs no NATS: republishing only enqueues outbox rows.
internal sealed class AgentWorkReconciliationWorker(
    IServiceProvider services,
    IOptionsMonitor<AgentOrchestrationOptions> options,
    IOptions<AnalysisWorkerOptions> workerOptions,
    TimeProvider timeProvider,
    ILogger<AgentWorkReconciliationWorker> logger) : BackgroundService
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
            var interval = options.CurrentValue.Monitor.ReconciliationInterval;
            var loop = new WorkerLoop("agent-work-reconciliation", timeProvider, logger, interval, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(60));
            await loop.RunAsync(async ct =>
            {
                await using var scope = services.CreateAsyncScope();
                var republished = await scope.ServiceProvider.GetRequiredService<ReconcileAgentWorkHandler>()
                    .HandleAsync(ct).ConfigureAwait(false);
                if (republished > 0)
                {
                    logger.LogInformation("Agent work reconciliation republished {Count} ready notifications", republished);
                }
            }, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogError("Agent work reconciliation stopped: {Category}", WorkerLoop.Categorize(ex));
        }
    }
}
