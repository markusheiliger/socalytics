using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SocAlytics.Platform.Infrastructure.Persistence.Readiness;

namespace SocAlytics.Platform.Infrastructure.Persistence;

internal static class PersistenceRegistration
{
    public static IServiceCollection AddPersistence(IServiceCollection services)
    {
        services.TryAddSingleton<PlatformDataSource>();
        services.TryAddSingleton<UnknownAppliedMigrationsReporter>();
        services.AddHealthChecks().AddCheck<DatabaseReadinessHealthCheck>(
            "database", HealthStatus.Unhealthy, tags: [], timeout: TimeSpan.FromSeconds(5));
        Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;

        return services;
    }
}
