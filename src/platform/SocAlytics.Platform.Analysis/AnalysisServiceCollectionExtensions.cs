using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SocAlytics.Platform.Analysis;

public static class AnalysisServiceCollectionExtensions
{
    public static IServiceCollection AddAnalysisModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<AnalysisModuleMarker>();

        return services;
    }
}

internal sealed class AnalysisModuleMarker;