using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SocAlytics.Platform.Persistence;

namespace SocAlytics.Platform.Registry;

public static class RegistryServiceCollectionExtensions
{
    public static IServiceCollection AddRegistryModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (services.Any(descriptor => descriptor.ServiceType == typeof(RegistryModuleMarker)))
        {
            return services;
        }

        services.AddSingleton<RegistryModuleMarker>();
        services.AddModuleMigrations(new RegistryMigrationContributor());
        services.AddModulePersistence(PersistenceModuleKey.Registry);

        return services;
    }
}

internal sealed class RegistryModuleMarker;