using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SocAlytics.Platform.Persistence;

namespace SocAlytics.Platform.Recordings;

public static class RecordingsServiceCollectionExtensions
{
    public static IServiceCollection AddRecordingsModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (services.Any(descriptor => descriptor.ServiceType == typeof(RecordingsModuleMarker)))
        {
            return services;
        }

        services.AddSingleton<RecordingsModuleMarker>();
        services.AddModuleMigrations(new RecordingsMigrationContributor());
        services.AddModulePersistence(PersistenceModuleKey.Recordings);

        return services;
    }
}

internal sealed class RecordingsModuleMarker;