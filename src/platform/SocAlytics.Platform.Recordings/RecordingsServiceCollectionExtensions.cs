using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SocAlytics.Platform.Persistence;

namespace SocAlytics.Platform.Recordings;

public static class RecordingsServiceCollectionExtensions
{
    public static IServiceCollection AddRecordingsModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<RecordingsModuleMarker>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IModuleMigrationContributor, RecordingsMigrationContributor>());

        return services;
    }
}

internal sealed class RecordingsModuleMarker;

internal sealed class RecordingsMigrationContributor : IModuleMigrationContributor
{
    public ModuleKey ModuleKey => ModuleKey.Recordings;

    public IReadOnlyList<MigrationDescriptor> GetMigrations() =>
    [
        MigrationDescriptor.FromEmbeddedResource(
            typeof(RecordingsMigrationContributor).Assembly,
            "SocAlytics.Platform.Recordings.Migrations.0001_initial.sql",
            ModuleKey,
            1,
            "0001_initial")
    ];
}