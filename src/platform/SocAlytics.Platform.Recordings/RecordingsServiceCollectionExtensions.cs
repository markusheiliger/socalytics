using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SocAlytics.Platform.Recordings;

public static class RecordingsServiceCollectionExtensions
{
    public static IServiceCollection AddRecordingsModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<RecordingsModuleMarker>();

        return services;
    }
}

internal sealed class RecordingsModuleMarker;