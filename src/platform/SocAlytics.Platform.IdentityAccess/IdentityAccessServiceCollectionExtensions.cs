using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SocAlytics.Platform.Persistence;

namespace SocAlytics.Platform.IdentityAccess;

public static class IdentityAccessServiceCollectionExtensions
{
    public static IServiceCollection AddIdentityAccessModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IdentityAccessModuleMarker>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IModuleMigrationContributor, IdentityAccessMigrationContributor>());

        return services;
    }
}

internal sealed class IdentityAccessModuleMarker;

internal sealed class IdentityAccessMigrationContributor : IModuleMigrationContributor
{
    public ModuleKey ModuleKey => ModuleKey.IdentityAccess;

    public IReadOnlyList<MigrationDescriptor> GetMigrations() =>
    [
        MigrationDescriptor.FromEmbeddedResource(
            typeof(IdentityAccessMigrationContributor).Assembly,
            "SocAlytics.Platform.IdentityAccess.Migrations.0001_initial.sql",
            ModuleKey,
            1,
            "0001_initial")
    ];
}