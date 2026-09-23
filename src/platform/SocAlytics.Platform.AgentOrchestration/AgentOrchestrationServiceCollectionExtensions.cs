using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SocAlytics.Platform.AgentOrchestration;

public static class AgentOrchestrationServiceCollectionExtensions
{
    public static IServiceCollection AddAgentOrchestrationModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<AgentOrchestrationModuleMarker>();

        return services;
    }
}

internal sealed class AgentOrchestrationModuleMarker;