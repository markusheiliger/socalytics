using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace SocAlytics.Platform.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    /// <summary>Registers the shared persistence boundary. Called by the host, not by modules.</summary>
    public static IServiceCollection AddPlatformPersistence(
        this IServiceCollection services,
        Action<PersistenceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new PersistenceOptions();
        configure(options);
        if (string.IsNullOrWhiteSpace(options.BootstrapConnectionString))
        {
            throw new ArgumentException("A bootstrap connection string is required.", nameof(configure));
        }

        var runtimeConnectionString = options.RuntimeConnectionString ?? options.BootstrapConnectionString;

        // The internal key keeps the privileged source unresolvable outside this assembly.
        services.TryAddKeyedSingleton(
            BootstrapKey.Instance,
            (_, _) => new BootstrapConnectionSource(NpgsqlDataSource.Create(options.BootstrapConnectionString)));
        services.TryAddSingleton(_ => new RuntimeDataSourceHolder(NpgsqlDataSource.Create(runtimeConnectionString)));
        services.TryAddSingleton<MigrationCatalogProvider>();

        return services;
    }

    /// <summary>Binds a fixed adopted module identity, its runtime connection factory, and its migrations.</summary>
    public static IServiceCollection AddModulePersistence<TContributor>(
        this IServiceCollection services,
        PersistenceModuleKey module)
        where TContributor : class, IMigrationContributor
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(module);

        services.TryAddKeyedSingleton<IModuleConnectionFactory>(
            module,
            (provider, _) => new ModuleConnectionFactory(
                module,
                provider.GetRequiredService<RuntimeDataSourceHolder>().DataSource));
        services.AddSingleton<IMigrationContributor, TContributor>();

        return services;
    }
}

internal sealed class BootstrapKey
{
    public static BootstrapKey Instance { get; } = new();

    private BootstrapKey()
    {
    }
}

internal sealed class RuntimeDataSourceHolder(NpgsqlDataSource dataSource) : IAsyncDisposable
{
    public NpgsqlDataSource DataSource { get; } = dataSource;

    public ValueTask DisposeAsync() => DataSource.DisposeAsync();
}

internal sealed class MigrationCatalogProvider(IEnumerable<IMigrationContributor> contributors)
{
    public MigrationCatalog Catalog { get; } = MigrationCatalog.Create(contributors);
}
