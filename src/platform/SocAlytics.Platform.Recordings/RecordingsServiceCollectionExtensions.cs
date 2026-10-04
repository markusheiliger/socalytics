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
        services.AddModulePersistence(new RecordingsMigrationContributor());

        return services;
    }
}

internal sealed class RecordingsModuleMarker;