using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SocAlytics.Platform.Persistence;

namespace SocAlytics.Platform.AgentOrchestration;

public static class AgentOrchestrationServiceCollectionExtensions
{
    public static IServiceCollection AddAgentOrchestrationModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (services.Any(descriptor => descriptor.ServiceType == typeof(AgentOrchestrationModuleMarker)))
        {
            return services;
        }

        services.AddSingleton<AgentOrchestrationModuleMarker>();
        services.AddModuleMigrations(new AgentOrchestrationMigrationContributor());
        services.AddModulePersistence(PersistenceModuleKey.AgentOrchestration);

        return services;
    }
}

internal sealed class AgentOrchestrationModuleMarker;