using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SocAlytics.Platform.Infrastructure.Persistence;

internal static class PersistenceRegistration
{
    public static IServiceCollection AddPersistence(IServiceCollection services)
    {
        services.TryAddSingleton<PlatformDataSource>();
        Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;

        return services;
    }
}
