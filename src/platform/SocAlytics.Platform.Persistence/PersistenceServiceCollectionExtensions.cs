using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace SocAlytics.Platform.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    /// <summary>Registers the shared persistence boundary. Called once by the host.</summary>
    public static IServiceCollection AddPlatformPersistence(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.TryAddSingleton(_ => new BootstrapConnectionSource(NpgsqlDataSource.Create(connectionString), connectionString));
        services.TryAddSingleton(sp => new MigrationOrchestrator(
            sp.GetRequiredService<MigrationCatalog>(), sp.GetRequiredService<BootstrapConnectionSource>()));
        services.TryAddSingleton(new RuntimeDataSource(connectionString));
        services.TryAddSingleton(sp => new MigrationCatalog(sp.GetServices<IMigrationContributor>()));

        return services;
    }

    /// <summary>Registers a module's fixed identity, migrations, and module-scoped connection factory.</summary>
    public static IServiceCollection AddModulePersistence(
        this IServiceCollection services, IMigrationContributor contributor)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(contributor);

        services.AddSingleton(contributor);
        services.AddKeyedSingleton<IModuleConnectionFactory>(
            contributor.Module.Key,
            (sp, _) => new ModuleConnectionFactory(
                contributor.Module, sp.GetRequiredService<RuntimeDataSource>().DataSource));

        return services;
    }
}

internal sealed class RuntimeDataSource(string connectionString) : IDisposable
{
    private readonly Lazy<NpgsqlDataSource> _dataSource = new(() => NpgsqlDataSource.Create(connectionString));

    public NpgsqlDataSource DataSource => _dataSource.Value;

    public void Dispose()
    {
        if (_dataSource.IsValueCreated)
        {
            _dataSource.Value.Dispose();
        }
    }
}
