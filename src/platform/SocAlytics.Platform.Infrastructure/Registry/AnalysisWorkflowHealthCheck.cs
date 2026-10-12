using Microsoft.Extensions.Diagnostics.HealthChecks;
using SocAlytics.Platform.Application.Registry;

namespace SocAlytics.Platform.Infrastructure.Registry;

internal sealed class AnalysisWorkflowHealthCheck(IWorkflowDefinitionAvailability availability) : IHealthCheck
{
    public const string Name = "analysis-workflow";

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var status = availability.Current;
        return Task.FromResult(status.IsResolvable
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy(status.Category));
    }
}
