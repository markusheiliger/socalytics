using Microsoft.Extensions.Diagnostics.HealthChecks;
using SocAlytics.Platform.Persistence;

namespace SocAlytics.Platform.Api;

internal static class PersistenceMigrationReadinessExtensions
{
    public static IServiceCollection AddPersistenceMigrationReadiness(
        this IServiceCollection services,
        string connectionString)
    {
        services.AddPlatformPersistence(options =>
        {
            options.BootstrapConnectionString = connectionString;
            options.RuntimeConnectionString = connectionString;
        });
        services.AddSingleton<MigrationReadinessState>();
        services.AddHostedService<PersistenceMigrationStartupService>();
        services.AddHealthChecks().AddCheck<PersistenceMigrationReadinessHealthCheck>("database-migrations");

        return services;
    }
}

internal sealed class MigrationReadinessState
{
    private readonly object sync = new();
    private HealthCheckResult result = HealthCheckResult.Unhealthy("Database migrations have not completed.");

    public HealthCheckResult Result
    {
        get
        {
            lock (sync)
            {
                return result;
            }
        }
    }

    public void MarkHealthy()
    {
        lock (sync)
        {
            result = HealthCheckResult.Healthy();
        }
    }

    public void MarkUnhealthy(string description)
    {
        lock (sync)
        {
            result = HealthCheckResult.Unhealthy(description);
        }
    }
}

internal sealed class PersistenceMigrationStartupService(
    MigrationOrchestrator orchestrator,
    MigrationReadinessState readiness,
    ILogger<PersistenceMigrationStartupService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            orchestrator.Run();
            readiness.MarkHealthy();
        }
        catch (MigrationConflictException exception)
        {
            readiness.MarkUnhealthy(exception.Message);
            logger.LogError("Database migration conflict: {Diagnostic}", exception.Message);
        }
        catch (Exception)
        {
            const string diagnostic = "Database migration failed.";
            readiness.MarkUnhealthy(diagnostic);
            logger.LogError("{Diagnostic}", diagnostic);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class PersistenceMigrationReadinessHealthCheck(MigrationReadinessState readiness) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(readiness.Result);
}
