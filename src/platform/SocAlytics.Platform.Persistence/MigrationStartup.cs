using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SocAlytics.Platform.Persistence;

/// <summary>Holds the outcome of startup migrations; the diagnostic never contains connection details.</summary>
internal sealed class MigrationStartupState
{
    private volatile string? _failure = "Migrations have not completed.";

    public bool Succeeded => _failure is null;

    public string? Diagnostic => _failure;

    public void MarkSucceeded() => _failure = null;

    public void MarkFailed(string diagnostic) => _failure = diagnostic;
}

internal sealed class MigrationStartupService(
    IServiceProvider services, MigrationStartupState state, ILogger<MigrationStartupService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var runner = (MigrationRunner)services.GetService(typeof(MigrationRunner))!;
            await runner.RunAsync(cancellationToken).ConfigureAwait(false);
            state.MarkSucceeded();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (MigrationException exception)
        {
            logger.LogError("Startup migrations failed: {Diagnostic}", exception.Message);
            state.MarkFailed(exception.Message);
        }
        catch (Exception exception)
        {
            // Only the exception type is reported; messages may carry connection details.
            logger.LogError("Startup migrations failed ({ExceptionType}).", exception.GetType().Name);
            state.MarkFailed("Migrations failed or the database is unavailable.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class MigrationReadinessHealthCheck(MigrationStartupState state) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(state.Succeeded
            ? HealthCheckResult.Healthy("Migrations completed.")
            : HealthCheckResult.Unhealthy(state.Diagnostic));
}
