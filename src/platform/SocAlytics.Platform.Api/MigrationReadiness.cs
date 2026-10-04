using Microsoft.Extensions.Diagnostics.HealthChecks;
using SocAlytics.Platform.Persistence;

namespace SocAlytics.Platform.Api;

internal sealed class MigrationReadinessState
{
	private volatile string? _failure = "Database migrations have not completed.";

	public bool IsReady => _failure is null;

	public string? Failure => _failure;

	public void MarkReady() => _failure = null;

	public void MarkFailed(string diagnostic) => _failure = diagnostic;
}

internal sealed class MigrationReadinessHealthCheck(MigrationReadinessState state) : IHealthCheck
{
	public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
		Task.FromResult(state.IsReady
			? HealthCheckResult.Healthy("Database migrations completed.")
			: HealthCheckResult.Unhealthy(state.Failure));
}

/// <summary>Runs migration orchestration once at startup and records only a sanitized outcome.</summary>
internal sealed class MigrationStartupService(
	IServiceProvider services,
	MigrationReadinessState state,
	ILogger<MigrationStartupService> logger,
	bool persistenceConfigured) : BackgroundService
{
	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		if (!persistenceConfigured)
		{
			state.MarkFailed("Database connection is not configured.");
			return;
		}

		try
		{
			await services.GetRequiredService<MigrationOrchestrator>().MigrateAsync(stoppingToken);
			state.MarkReady();
		}
		catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
		{
		}
		catch (MigrationFailedException ex)
		{
			// MigrationFailedException messages carry only module, script identity, and SQLSTATE.
			logger.LogError("Database migration failed: {Diagnostic}", ex.Message);
			state.MarkFailed(ex.Message);
		}
		catch (Exception ex)
		{
			logger.LogError("Database migration failed ({ExceptionType}).", ex.GetType().Name);
			state.MarkFailed("Database migration failed.");
		}
	}
}
