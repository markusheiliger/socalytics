using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SocAlytics.Platform.IdentityAccess;

public static class IdentityAccessServiceCollectionExtensions
{
    public static IServiceCollection AddIdentityAccessModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IdentityAccessModuleMarker>();

        return services;
    }
}

internal sealed class IdentityAccessModuleMarker;