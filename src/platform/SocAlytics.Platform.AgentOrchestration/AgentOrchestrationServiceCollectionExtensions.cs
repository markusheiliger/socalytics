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
        services.AddModulePersistence(new AgentOrchestrationMigrationContributor());

        return services;
    }
}

internal sealed class AgentOrchestrationModuleMarker;