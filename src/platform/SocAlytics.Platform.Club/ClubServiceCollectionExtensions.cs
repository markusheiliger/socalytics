using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SocAlytics.Platform.Persistence;

namespace SocAlytics.Platform.Club;

public static class ClubServiceCollectionExtensions
{
    public static IServiceCollection AddClubModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (services.Any(descriptor => descriptor.ServiceType == typeof(ClubModuleMarker)))
        {
            return services;
        }

        services.AddSingleton<ClubModuleMarker>();
        services.AddModuleMigrations(new ClubMigrationContributor());
        services.AddModulePersistence(PersistenceModuleKey.Club);

        return services;
    }
}

internal sealed class ClubModuleMarker;