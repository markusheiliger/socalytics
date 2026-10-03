using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace SocAlytics.Platform.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    /// <summary>Registers the shared persistence infrastructure once for the host.</summary>
    public static IServiceCollection AddPlatformPersistence(
        this IServiceCollection services, Action<PersistenceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new PersistenceOptions();
        configure(options);

        services.TryAddSingleton(options);
        services.TryAddSingleton<PersistenceDataSource>();
        services.TryAddSingleton(sp =>
            MigrationCatalog.Create(sp.GetServices<IModuleMigrationContributor>()));

        services.TryAddSingleton<MigrationRunner>();

        return services;
    }

    /// <summary>
    /// Runs migration orchestration once during host startup and reports readiness on the
    /// health-check pipeline only after it succeeded.
    /// </summary>
    public static IServiceCollection AddPlatformPersistenceStartup(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<MigrationStartupState>();
        services.AddHostedService<MigrationStartupService>();
        services.AddHealthChecks().AddCheck<MigrationReadinessHealthCheck>(
            "migrations", HealthStatus.Unhealthy, tags: ["ready"]);

        return services;
    }

    /// <summary>
    /// Binds a module marker to one fixed module identity, registers its migrations,
    /// and exposes only its role-scoped connection factory.
    /// </summary>
    public static IServiceCollection AddModulePersistence<TModule>(
        this IServiceCollection services, IModuleMigrationContributor contributor)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(contributor);

        var module = contributor.Module;
        services.AddSingleton(contributor);
        services.TryAddSingleton<IModuleConnectionFactory<TModule>>(sp =>
            new ModuleConnectionFactory<TModule>(module, sp.GetRequiredService<PersistenceDataSource>()));

        return services;
    }
}
