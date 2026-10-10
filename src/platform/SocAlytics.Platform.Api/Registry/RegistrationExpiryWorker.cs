using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Registry;
using SocAlytics.Platform.Application.Registry.Expiry;

namespace SocAlytics.Platform.Api.Registry;

public sealed partial class RegistrationExpiryWorker(
	IServiceScopeFactory scopeFactory,
	IOptions<RegistryOptions> options,
	TimeProvider time,
	ILogger<RegistrationExpiryWorker> logger) : BackgroundService
{
	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		await Task.Yield();
		var interval = options.Value.ExpiryWorker.Interval;
		if (interval <= TimeSpan.Zero)
		{
			interval = TimeSpan.FromSeconds(30);
		}

		using var timer = new PeriodicTimer(interval, time);
		do
		{
			try
			{
				await using var scope = scopeFactory.CreateAsyncScope();
				await scope.ServiceProvider.GetRequiredService<ExpireDueRegistrationsHandler>().RunAsync(stoppingToken);
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
			{
				return;
			}
			catch (Exception exception)
			{
				LogRunFailed(exception.GetType().Name);
			}
		}
		while (await WaitAsync(timer, stoppingToken));
	}

	private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken cancellationToken)
	{
		try
		{
			return await timer.WaitForNextTickAsync(cancellationToken);
		}
		catch (OperationCanceledException)
		{
			return false;
		}
	}

	[LoggerMessage(Level = LogLevel.Warning, Message = "Registration expiry run failed ({ExceptionType}).")]
	private partial void LogRunFailed(string exceptionType);
}
