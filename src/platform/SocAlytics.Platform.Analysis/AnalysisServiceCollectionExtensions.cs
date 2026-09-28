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
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IModuleMigrationContributor, AnalysisMigrationContributor>());

        return services;
    }
}

internal sealed class AnalysisModuleMarker;

internal sealed class AnalysisMigrationContributor : IModuleMigrationContributor
{
    public ModuleKey ModuleKey => ModuleKey.Analysis;

    public IReadOnlyList<MigrationDescriptor> GetMigrations() =>
    [
        MigrationDescriptor.FromEmbeddedResource(
            typeof(AnalysisMigrationContributor).Assembly,
            "SocAlytics.Platform.Analysis.Migrations.0001_initial.sql",
            ModuleKey,
            1,
            "0001_initial")
    ];
}