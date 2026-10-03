using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SocAlytics.Platform.Persistence;

namespace SocAlytics.Platform.Club;

public static class ClubServiceCollectionExtensions
{
    public static IServiceCollection AddClubModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ClubModuleMarker>();
        services.AddModulePersistence(PersistenceModule.Club, new ClubMigrationContributor());

        return services;
    }
}

internal sealed class ClubModuleMarker;