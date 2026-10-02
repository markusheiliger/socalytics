using Microsoft.Extensions.DependencyInjection;

namespace SocAlytics.Platform.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddPlatformPersistence(
        this IServiceCollection services,
        string runtimeConnectionString,
        string bootstrapConnectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeConnectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(bootstrapConnectionString);

        services.AddSingleton<IRuntimeDatabaseConnectionFactory>(
            _ => new RuntimeDatabaseConnectionFactory(runtimeConnectionString));
        services.AddSingleton<IBootstrapDatabaseConnectionFactory>(
            _ => new BootstrapDatabaseConnectionFactory(bootstrapConnectionString));

        return services;
    }
}
