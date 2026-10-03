using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
