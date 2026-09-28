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
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IModuleMigrationContributor, ClubMigrationContributor>());

        return services;
    }
}

internal sealed class ClubModuleMarker;

internal sealed class ClubMigrationContributor : IModuleMigrationContributor
{
    public ModuleKey ModuleKey => ModuleKey.Club;

    public IReadOnlyList<MigrationDescriptor> GetMigrations() =>
    [
        MigrationDescriptor.FromEmbeddedResource(
            typeof(ClubMigrationContributor).Assembly,
            "SocAlytics.Platform.Club.Migrations.0001_initial.sql",
            ModuleKey,
            1,
            "0001_initial")
    ];
}