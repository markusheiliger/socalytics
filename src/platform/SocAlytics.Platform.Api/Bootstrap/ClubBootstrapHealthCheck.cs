using Microsoft.Extensions.Diagnostics.HealthChecks;
using SocAlytics.Platform.Application.Club;

namespace SocAlytics.Platform.Api.Bootstrap;

public sealed class ClubBootstrapHealthCheck(
	IServiceScopeFactory scopeFactory,
	IConfiguration configuration,
	ClubBootstrapState state) : IHealthCheck
{
	public const string Name = "club-bootstrap";

	public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
	{
		if (state.LastOutcome == BootstrapOutcome.Conflict)
		{
			return HealthCheckResult.Unhealthy("bootstrap-conflict");
		}

		if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("socalytics")))
		{
			return HealthCheckResult.Unhealthy("club-not-established");
		}

		try
		{
			await using var scope = scopeFactory.CreateAsyncScope();
			var club = await scope.ServiceProvider.GetRequiredService<IClubHierarchyStore>().GetClubAsync(cancellationToken);
			return club is null ? HealthCheckResult.Unhealthy("club-not-established") : HealthCheckResult.Healthy();
		}
		catch (Exception) when (!cancellationToken.IsCancellationRequested)
		{
			return HealthCheckResult.Unhealthy("club-not-established");
		}
	}
}
