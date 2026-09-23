using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SocAlytics.Platform.Registry;

public static class RegistryServiceCollectionExtensions
{
    public static IServiceCollection AddRegistryModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<RegistryModuleMarker>();

        return services;
    }
}

internal sealed class RegistryModuleMarker;