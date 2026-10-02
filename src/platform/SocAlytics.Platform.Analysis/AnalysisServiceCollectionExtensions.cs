using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SocAlytics.Platform.Persistence;

namespace SocAlytics.Platform.Analysis;

public static class AnalysisServiceCollectionExtensions
{
    public static IServiceCollection AddAnalysisModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (services.Any(descriptor => descriptor.ServiceType == typeof(AnalysisModuleMarker)))
        {
            return services;
        }

        services.AddSingleton<AnalysisModuleMarker>();
        services.AddModuleMigrations(new AnalysisMigrationContributor());
        services.AddModulePersistence(PersistenceModuleKey.Analysis);

        return services;
    }
}

internal sealed class AnalysisModuleMarker;