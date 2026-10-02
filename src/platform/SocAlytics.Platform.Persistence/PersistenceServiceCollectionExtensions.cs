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
        services.AddSingleton(provider => new MigrationCatalog(provider.GetServices<MigrationDescriptor>()));
        services.AddSingleton(provider => new MigrationOrchestrator(
            provider.GetRequiredService<IBootstrapDatabaseConnectionFactory>(),
            provider.GetRequiredService<MigrationCatalog>(),
            bootstrapConnectionString));

        return services;
    }

    public static async Task MigratePlatformDatabaseAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        try
        {
            await services.GetRequiredService<MigrationOrchestrator>().MigrateAsync(cancellationToken);
        }
        catch (MigrationException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            throw new MigrationException("Migration preparation failed.");
        }
    }
}
