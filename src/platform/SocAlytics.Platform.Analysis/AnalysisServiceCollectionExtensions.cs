using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SocAlytics.Platform.Persistence;

namespace SocAlytics.Platform.Analysis;

public static class AnalysisServiceCollectionExtensions
{
    public static IServiceCollection AddAnalysisModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<AnalysisModuleMarker>();
        services.AddModulePersistence<AnalysisModuleMarker>(new AnalysisMigrationContributor());

        return services;
    }
}

internal sealed class AnalysisModuleMarker;