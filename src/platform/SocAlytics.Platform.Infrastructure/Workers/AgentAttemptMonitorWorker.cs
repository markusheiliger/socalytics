using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.AgentOrchestration;
using SocAlytics.Platform.Application.AgentOrchestration.Execution;

namespace SocAlytics.Platform.Infrastructure.Workers;

// Needs no NATS: requeued work only enqueues outbox rows.
internal sealed class AgentAttemptMonitorWorker(
    IServiceProvider services,
    IOptionsMonitor<AgentOrchestrationOptions> options,
    IOptions<AnalysisWorkerOptions> workerOptions,
    TimeProvider timeProvider,
    ILogger<AgentAttemptMonitorWorker> logger) : BackgroundService
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
            var interval = options.CurrentValue.Monitor.MonitorInterval;
            var loop = new WorkerLoop("agent-attempt-monitor", timeProvider, logger, interval, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(60));
            await loop.RunAsync(async ct =>
            {
                await using var scope = services.CreateAsyncScope();
                var result = await scope.ServiceProvider.GetRequiredService<MonitorAgentAttemptsHandler>()
                    .HandleAsync(ct).ConfigureAwait(false);
                if (result.Expired > 0)
                {
                    logger.LogInformation(
                        "Agent attempt monitor expired {Expired} attempts ({Requeued} requeued, {Exhausted} exhausted)",
                        result.Expired, result.Requeued, result.Exhausted);
                }
            }, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogError("Agent attempt monitor stopped: {Category}", WorkerLoop.Categorize(ex));
        }
    }
}
