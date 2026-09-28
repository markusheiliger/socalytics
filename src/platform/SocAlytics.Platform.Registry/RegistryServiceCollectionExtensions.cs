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
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IModuleMigrationContributor, RegistryMigrationContributor>());

        return services;
    }
}

internal sealed class RegistryModuleMarker;

internal sealed class RegistryMigrationContributor : IModuleMigrationContributor
{
    public ModuleKey ModuleKey => ModuleKey.Registry;

    public IReadOnlyList<MigrationDescriptor> GetMigrations() =>
    [
        MigrationDescriptor.FromEmbeddedResource(
            typeof(RegistryMigrationContributor).Assembly,
            "SocAlytics.Platform.Registry.Migrations.0001_initial.sql",
            ModuleKey,
            1,
            "0001_initial")
    ];
}