using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SocAlytics.Platform.Persistence;

namespace SocAlytics.Platform.IdentityAccess;

public static class IdentityAccessServiceCollectionExtensions
{
    public static IServiceCollection AddIdentityAccessModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (services.Any(descriptor => descriptor.ServiceType == typeof(IdentityAccessModuleMarker)))
        {
            return services;
        }

        services.AddSingleton<IdentityAccessModuleMarker>();
        services.AddModuleMigrations(new IdentityAccessMigrationContributor());
        services.AddModulePersistence(PersistenceModuleKey.IdentityAccess);

        return services;
    }
}

internal sealed class IdentityAccessModuleMarker;