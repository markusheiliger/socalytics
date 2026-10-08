using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Club;

namespace SocAlytics.Platform.Api.Bootstrap;

public sealed partial class ClubBootstrapHostedService(
	IServiceScopeFactory scopeFactory,
	IConfiguration configuration,
	IOptions<ClubBootstrapOptions> options,
	ClubBootstrapState state,
	ILogger<ClubBootstrapHostedService> logger) : BackgroundService
{
	private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(1);
	private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(5);

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		await Task.Yield();

		if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("socalytics")))
		{
			LogMissingConnectionString();
			return;
		}

		var settings = options.Value;
		var command = new BootstrapClubCommand(
			settings.ClubDisplayName,
			settings.FirstClubAdmin.AccountName,
			settings.FirstClubAdmin.InitialPassword);
		var delay = InitialDelay;

		while (!stoppingToken.IsCancellationRequested)
		{
			try
			{
				await using var scope = scopeFactory.CreateAsyncScope();
				var handler = scope.ServiceProvider.GetRequiredService<BootstrapClubHandler>();
				var result = await handler.HandleAsync(command, stoppingToken);
				if (result.IsSuccess)
				{
					state.Record(result.Value);
					LogOutcome(result.Value);
				}
				else
				{
					LogFailure(result.Failure.Kind);
				}

				return;
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
			{
				return;
			}
			catch (Exception exception)
			{
				LogRetry(exception.GetType().Name, delay.TotalSeconds);
			}

			try
			{
				await Task.Delay(delay, stoppingToken);
			}
			catch (OperationCanceledException)
			{
				return;
			}

			delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, MaxDelay.TotalSeconds));
		}
	}

	[LoggerMessage(EventId = 3000, Level = LogLevel.Error, Message = "Club bootstrap skipped: configuration key ConnectionStrings:socalytics is missing or empty.")]
	private partial void LogMissingConnectionString();

	[LoggerMessage(EventId = 3001, Level = LogLevel.Information, Message = "Club bootstrap finished with outcome {Outcome}.")]
	private partial void LogOutcome(BootstrapOutcome outcome);

	[LoggerMessage(EventId = 3002, Level = LogLevel.Error, Message = "Club bootstrap failed with category {FailureKind}.")]
	private partial void LogFailure(SocAlytics.Platform.Application.Abstractions.OperationFailureKind failureKind);

	[LoggerMessage(EventId = 3003, Level = LogLevel.Warning, Message = "Club bootstrap attempt failed with {ExceptionType}; retrying in {DelaySeconds} s.")]
	private partial void LogRetry(string exceptionType, double delaySeconds);
}
