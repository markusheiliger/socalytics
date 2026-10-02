using Microsoft.Extensions.Diagnostics.HealthChecks;
using SocAlytics.Platform.AgentOrchestration;
using SocAlytics.Platform.Analysis;
using SocAlytics.Platform.Club;
using SocAlytics.Platform.IdentityAccess;
using SocAlytics.Platform.Persistence;
using SocAlytics.Platform.Recordings;
using SocAlytics.Platform.Registry;

namespace SocAlytics.Platform.Api;

internal static class PlatformApiComposition
{
	internal const string ConnectionName = "platform";
	internal const string ReadinessCheckName = "migrations";

	internal static WebApplicationBuilder AddPlatformApi(this WebApplicationBuilder builder)
	{
		builder.AddServiceDefaults();

		var connectionString = builder.Configuration.GetConnectionString(ConnectionName);
		var persistenceConfigured = !string.IsNullOrWhiteSpace(connectionString);
		if (persistenceConfigured)
		{
			builder.Services.AddPlatformPersistence(options => options.BootstrapConnectionString = connectionString);
		}

		builder.Services.AddAgentOrchestrationModule();
		builder.Services.AddAnalysisModule();
		builder.Services.AddClubModule();
		builder.Services.AddIdentityAccessModule();
		builder.Services.AddRecordingsModule();
		builder.Services.AddRegistryModule();

		builder.Services.AddSingleton<MigrationReadiness>();
		builder.Services.AddSingleton(new PersistenceConfiguration(persistenceConfigured));
		builder.Services.AddHostedService<MigrationStartupService>();
		builder.Services.AddHealthChecks().AddCheck<MigrationReadinessCheck>(ReadinessCheckName);

		builder.Services.AddOpenApi("v1", options =>
		{
			options.AddDocumentTransformer((document, _, _) =>
			{
				document.Info.Version = "v1";
				return Task.CompletedTask;
			});
		});

		return builder;
	}

	internal static WebApplication MapPlatformApi(this WebApplication app)
	{
		app.MapDefaultEndpoints();
		app.MapOpenApi();
		return app;
	}
}

internal sealed record PersistenceConfiguration(bool IsConfigured);

internal sealed class MigrationReadiness
{
	private volatile string? _failure = "Database migrations have not completed.";

	internal string? Failure => _failure;

	internal void Succeeded() => _failure = null;

	internal void Failed(string sanitizedMessage) => _failure = sanitizedMessage;
}

// Runs once during host startup, before the server accepts traffic; failure keeps readiness unhealthy.
internal sealed class MigrationStartupService(
	IServiceProvider services,
	PersistenceConfiguration configuration,
	MigrationReadiness readiness,
	ILogger<MigrationStartupService> logger) : IHostedService
{
	public async Task StartAsync(CancellationToken cancellationToken)
	{
		if (!configuration.IsConfigured)
		{
			const string notConfigured = "Database connection is not configured.";
			logger.LogError(notConfigured);
			readiness.Failed(notConfigured);
			return;
		}

		try
		{
			var runner = services.GetRequiredService<IMigrationRunner>();
			await runner.MigrateAsync(cancellationToken);
			readiness.Succeeded();
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (MigrationFailedException exception)
		{
			logger.LogError("Database migration failed: {Message}", exception.Message);
			readiness.Failed(exception.Message);
		}
		catch (Exception)
		{
			const string message = "Database migration failed unexpectedly.";
			logger.LogError(message);
			readiness.Failed(message);
		}
	}

	public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class MigrationReadinessCheck(MigrationReadiness readiness) : IHealthCheck
{
	public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
		Task.FromResult(readiness.Failure is { } failure
			? HealthCheckResult.Unhealthy(failure)
			: HealthCheckResult.Healthy());
}
