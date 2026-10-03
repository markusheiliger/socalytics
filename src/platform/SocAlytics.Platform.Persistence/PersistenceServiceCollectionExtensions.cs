using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace SocAlytics.Platform.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    /// <summary>
    /// Registers the shared persistence infrastructure. Requires an <see cref="NpgsqlDataSource"/>
    /// to be registered by the host.
    /// </summary>
    public static IServiceCollection AddPlatformPersistence(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(sp => new BootstrapConnectionSource(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton(sp => MigrationCatalog.Create(sp.GetServices<IMigrationContributor>()));

        return services;
    }

    /// <summary>
    /// Binds a fixed adopted module to its runtime connection factory (resolved with the module key as
    /// service key) and its migration contributor.
    /// </summary>
    public static IServiceCollection AddModulePersistence(
        this IServiceCollection services, PersistenceModule module, IMigrationContributor contributor)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(contributor);

        if (contributor.Module != module)
        {
            throw new ArgumentException($"Contributor targets module '{contributor.Module}', not '{module}'.", nameof(contributor));
        }

        services.AddPlatformPersistence();
        services.TryAddKeyedSingleton<IModuleConnectionFactory>(
            module.Key, (sp, _) => new ModuleConnectionFactory(sp.GetRequiredService<NpgsqlDataSource>(), module));
        services.AddSingleton(contributor);

        return services;
    }
}
