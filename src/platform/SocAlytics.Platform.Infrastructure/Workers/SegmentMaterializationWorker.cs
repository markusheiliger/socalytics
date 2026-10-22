using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Segments;
using SocAlytics.Platform.Infrastructure.Segments;

namespace SocAlytics.Platform.Infrastructure.Workers;

// Database and object storage only: runs without NATS. Each iteration drains due rows with up to
// MaxConcurrentPerInstance parallel claims, each in its own scope and unit of work.
internal sealed class SegmentMaterializationWorker(
    IServiceProvider services,
    IOptions<SegmentMaterializationOptions> options,
    IOptions<AnalysisWorkerOptions> workerOptions,
    ToolchainProbe toolchainProbe,
    TimeProvider timeProvider,
    ILogger<SegmentMaterializationWorker> logger) : BackgroundService
{
    private readonly string instanceId = Guid.NewGuid().ToString("N");

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
                "segment-materialization", timeProvider, logger, options.Value.PollInterval, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(60));
            await loop.RunAsync(RunOnceAsync, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogError("Segment materialization worker stopped: {Category}", WorkerLoop.Categorize(ex));
        }
    }

    // Probes the toolchain (cached once it succeeds), then drains due rows until none is left.
    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await toolchainProbe.ProbeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SegmentExtractionException ex)
        {
            logger.LogError("Segment toolchain probe failed: {Reason}; no segment is claimed.", ex.Reason);
            throw;
        }

        var parallelism = Math.Max(1, options.Value.MaxConcurrentPerInstance);
        var claimers = Enumerable.Range(0, parallelism)
            .Select(index => DrainAsync(string.Create(CultureInfo.InvariantCulture, $"{instanceId}:{index}"), cancellationToken));
        await Task.WhenAll(claimers).ConfigureAwait(false);
    }

    private async Task DrainAsync(string leaseOwner, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var scope = services.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<MaterializeSegmentHandler>();
            var outcomes = await handler.ProcessDueAsync(leaseOwner, 1, cancellationToken).ConfigureAwait(false);
            if (outcomes.Count == 0)
            {
                return;
            }
        }
    }
}
