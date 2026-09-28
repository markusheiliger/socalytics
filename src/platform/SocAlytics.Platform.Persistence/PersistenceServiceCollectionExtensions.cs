using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SocAlytics.Platform.Persistence.Connections;

namespace SocAlytics.Platform.Persistence;

/// <summary>
/// Module-neutral registration for the shared persistence boundary. This is the only entry
/// point capability modules and the API use to obtain persistence services; it never exposes
/// the restricted bootstrap connection to callers.
/// </summary>
public static class PersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddPlatformPersistence(this IServiceCollection services, Action<PersistenceOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureOptions);

        var options = new PersistenceOptions();
        configureOptions(options);
        options.Validate();

        services.TryAddSingleton(options);
        services.TryAddSingleton<IModuleConnectionFactory, NpgsqlModuleConnectionFactory>();
        services.TryAddSingleton<IBootstrapConnectionFactory, NpgsqlBootstrapConnectionFactory>();
        services.TryAddSingleton<MigrationOrchestrator>();

        return services;
    }

    /// <summary>
    /// Registers an owning module's internal migration contributor. Modules call this from
    /// their existing public composition method; the contributor type itself stays internal.
    /// </summary>
    public static IServiceCollection AddModuleMigrationContributor<TContributor>(this IServiceCollection services)
        where TContributor : class, IModuleMigrationContributor
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IModuleMigrationContributor, TContributor>();

        return services;
    }
}
