using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace SocAlytics.Platform.Infrastructure.Messaging.Nats;

internal sealed class NatsHealthCheck(NatsConnectionProvider provider) : IHealthCheck
{
    public const string Name = "nats";

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!provider.IsConfigured)
        {
            return HealthCheckResult.Degraded("transport-unavailable");
        }

        provider.EnsureStarted();
        if (!provider.EverOpened)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!provider.IsConnected && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }

        return provider.IsConnected ? HealthCheckResult.Healthy() : HealthCheckResult.Degraded("transport-unavailable");
    }
}
