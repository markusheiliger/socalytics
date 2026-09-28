using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SocAlytics.Platform.Persistence;

namespace SocAlytics.Platform.AgentOrchestration;

public static class AgentOrchestrationServiceCollectionExtensions
{
    public static IServiceCollection AddAgentOrchestrationModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<AgentOrchestrationModuleMarker>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IModuleMigrationContributor, AgentOrchestrationMigrationContributor>());

        return services;
    }
}

internal sealed class AgentOrchestrationModuleMarker;

internal sealed class AgentOrchestrationMigrationContributor : IModuleMigrationContributor
{
    public ModuleKey ModuleKey => ModuleKey.AgentOrchestration;

    public IReadOnlyList<MigrationDescriptor> GetMigrations() =>
    [
        MigrationDescriptor.FromEmbeddedResource(
            typeof(AgentOrchestrationMigrationContributor).Assembly,
            "SocAlytics.Platform.AgentOrchestration.Migrations.0001_initial.sql",
            ModuleKey,
            1,
            "0001_initial")
    ];
}