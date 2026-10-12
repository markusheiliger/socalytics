using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using SocAlytics.Platform.Application.Abstractions;

namespace SocAlytics.Platform.Infrastructure.Workers;

internal sealed class WorkerLoop(
    string name,
    TimeProvider timeProvider,
    ILogger logger,
    TimeSpan interval,
    TimeSpan backoffBase,
    TimeSpan backoffMax)
{
    public static string Categorize(Exception ex) => ex switch
    {
        MessageTransportException mte => mte.FailureCategory,
        Npgsql.NpgsqlException => "database-unavailable",
        _ => "worker-failure",
    };

    public async Task RunAsync(Func<CancellationToken, Task> iteration, CancellationToken stoppingToken)
    {
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                await iteration(stoppingToken).ConfigureAwait(false);
                failures = 0;
                delay = interval;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                failures++;
                var seconds = Math.Min(backoffMax.TotalSeconds, backoffBase.TotalSeconds * Math.Pow(2, Math.Min(failures - 1, 30)));
                delay = TimeSpan.FromSeconds(seconds);
                logger.LogWarning("Worker {Worker} iteration failed: {Category}; retrying in {DelaySeconds}s", name, Categorize(ex), delay.TotalSeconds);
            }

            try
            {
                await Task.Delay(delay, timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public static async Task WaitForDatabaseAsync(HealthCheckService health, TimeProvider timeProvider, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var report = await health.CheckHealthAsync(r => r.Name == "database", stoppingToken).ConfigureAwait(false);
                if (report.Status == HealthStatus.Healthy)
                {
                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(1), timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
            }
        }
    }
}
