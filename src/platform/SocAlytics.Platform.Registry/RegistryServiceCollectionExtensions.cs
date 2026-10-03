using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SocAlytics.Platform.Persistence;

namespace SocAlytics.Platform.Registry;

public static class RegistryServiceCollectionExtensions
{
    public static IServiceCollection AddRegistryModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<RegistryModuleMarker>();
        services.AddModulePersistence(PersistenceModule.Registry, new RegistryMigrationContributor());

        return services;
    }
}

internal sealed class RegistryModuleMarker;