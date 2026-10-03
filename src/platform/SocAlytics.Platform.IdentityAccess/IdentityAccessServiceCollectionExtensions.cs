using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SocAlytics.Platform.Persistence;

namespace SocAlytics.Platform.IdentityAccess;

public static class IdentityAccessServiceCollectionExtensions
{
    public static IServiceCollection AddIdentityAccessModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IdentityAccessModuleMarker>();
        services.AddModulePersistence<IdentityAccessModuleMarker>(new IdentityAccessMigrationContributor());

        return services;
    }
}

internal sealed class IdentityAccessModuleMarker;