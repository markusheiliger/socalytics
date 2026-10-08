using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Infrastructure.Persistence.Readiness;

namespace SocAlytics.Platform.Infrastructure.Persistence;

internal static class PersistenceRegistration
{
    public static IServiceCollection AddPersistence(IServiceCollection services)
    {
        services.TryAddSingleton<PlatformDataSource>();
        services.TryAddSingleton<UnknownAppliedMigrationsReporter>();
        services.TryAddScoped<DbSession>();
        services.TryAddScoped<IUnitOfWork>(sp => sp.GetRequiredService<DbSession>());
        services.TryAddScoped<IDbSession>(sp => sp.GetRequiredService<DbSession>());
        services.AddHealthChecks().AddCheck<DatabaseReadinessHealthCheck>(
            "database", HealthStatus.Unhealthy, tags: [], timeout: TimeSpan.FromSeconds(5));
        Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;

        return services;
    }
}
