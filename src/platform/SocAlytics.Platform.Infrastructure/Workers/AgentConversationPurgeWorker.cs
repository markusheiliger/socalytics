using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.AgentOrchestration;
using SocAlytics.Platform.Application.AgentOrchestration.Execution;

namespace SocAlytics.Platform.Infrastructure.Workers;

// Needs no NATS: the purge records are the durable work queue.
internal sealed class AgentConversationPurgeWorker(
    IServiceProvider services,
    IOptionsMonitor<AgentOrchestrationOptions> options,
    IOptions<AnalysisWorkerOptions> workerOptions,
    TimeProvider timeProvider,
    ILogger<AgentConversationPurgeWorker> logger) : BackgroundService
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
            var interval = options.CurrentValue.Purge.PollInterval;
            var loop = new WorkerLoop("agent-conversation-purge", timeProvider, logger, interval, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(60));
            await loop.RunAsync(async ct =>
            {
                await using var scope = services.CreateAsyncScope();
                var completed = await scope.ServiceProvider.GetRequiredService<PurgeAgentConversationHandler>()
                    .HandleAsync(ct).ConfigureAwait(false);
                if (completed > 0)
                {
                    logger.LogInformation("Agent conversation purge completed {Count} purge records", completed);
                }
            }, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogError("Agent conversation purge stopped: {Category}", WorkerLoop.Categorize(ex));
        }
    }
}
