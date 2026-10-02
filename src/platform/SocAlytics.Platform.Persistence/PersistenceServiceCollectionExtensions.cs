using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace SocAlytics.Platform.Persistence;

public sealed class PlatformPersistenceOptions
{
    /// <summary>Privileged connection string used only by migration orchestration.</summary>
    public string? BootstrapConnectionString { get; set; }

    /// <summary>Connection string for module sessions; defaults to the bootstrap connection string's server.</summary>
    public string? RuntimeConnectionString { get; set; }
}

public static class PersistenceServiceCollectionExtensions
{
    /// <summary>Registers the shared persistence boundary once; repeated calls are ignored.</summary>
    public static IServiceCollection AddPlatformPersistence(
        this IServiceCollection services,
        Action<PlatformPersistenceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        if (services.Any(d => d.ServiceType == typeof(PersistenceMarker)))
        {
            return services;
        }

        var options = new PlatformPersistenceOptions();
        configure(options);
        if (string.IsNullOrWhiteSpace(options.BootstrapConnectionString))
        {
            throw new ArgumentException("A bootstrap connection string is required.", nameof(configure));
        }

        var bootstrapString = options.BootstrapConnectionString;
        var runtimeString = string.IsNullOrWhiteSpace(options.RuntimeConnectionString)
            ? bootstrapString
            : options.RuntimeConnectionString;

        services.AddSingleton<PersistenceMarker>();
        services.AddSingleton(_ => new BootstrapConnectionSource(NpgsqlDataSource.Create(bootstrapString)));
        services.AddSingleton(new RuntimeDataSourceHolder(runtimeString));
        services.AddSingleton(sp => MigrationCatalog.Create(sp.GetServices<IMigrationContributor>()));

        return services;
    }

    /// <summary>Registers a module's migration contributor against its fixed identity.</summary>
    public static IServiceCollection AddModuleMigrations(
        this IServiceCollection services,
        IMigrationContributor contributor)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(contributor);

        services.AddSingleton(contributor);
        return services;
    }

    /// <summary>Registers the runtime connection factory for exactly one module, retrievable by its key.</summary>
    public static IServiceCollection AddModulePersistence(
        this IServiceCollection services,
        PersistenceModuleKey module)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(module);

        services.TryAddKeyedSingleton<IModuleConnectionFactory>(
            module,
            (sp, _) => new ModuleConnectionFactory(module, sp.GetRequiredService<RuntimeDataSourceHolder>().DataSource));
        return services;
    }

    private sealed class PersistenceMarker;
}

internal sealed class RuntimeDataSourceHolder(string connectionString) : IDisposable
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
