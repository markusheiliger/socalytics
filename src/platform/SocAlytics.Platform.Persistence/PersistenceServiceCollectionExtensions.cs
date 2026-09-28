using Microsoft.Extensions.DependencyInjection;
using Npgsql;

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

        services.AddSingleton<IModuleConnectionFactory>(
            _ => new RuntimeConnectionFactory(NpgsqlDataSource.Create(runtimeConnectionString)));
        services.AddSingleton<MigrationDescriptorRegistry>();

        return services;
    }
}

internal interface IModuleConnectionFactory
{
    ValueTask<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default);
}

internal sealed class RuntimeConnectionFactory(NpgsqlDataSource dataSource) : IModuleConnectionFactory, IAsyncDisposable
{
    public ValueTask<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default) =>
        dataSource.OpenConnectionAsync(cancellationToken);

    public ValueTask DisposeAsync() => dataSource.DisposeAsync();
}

internal sealed class BootstrapConnectionFactory(string connectionString)
{
    internal async ValueTask<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = new NpgsqlConnection(connectionString);

        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}

internal sealed class MigrationDescriptorRegistry
{
    private readonly List<MigrationDescriptor> descriptors = [];

    internal IReadOnlyList<MigrationDescriptor> Descriptors => descriptors;

    internal void Register(MigrationDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (descriptors.Any(existing =>
                existing.Module == descriptor.Module &&
                string.Equals(existing.Identity, descriptor.Identity, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                $"Migration identity '{descriptor.Identity}' is already registered for module '{ModuleIdentityCatalog.GetKey(descriptor.Module)}'.",
                nameof(descriptor));
        }

        if (descriptors.Any(existing =>
                existing.Module == descriptor.Module &&
                existing.Sequence == descriptor.Sequence))
        {
            throw new ArgumentException(
                $"Migration sequence '{descriptor.Sequence}' is already registered for module '{ModuleIdentityCatalog.GetKey(descriptor.Module)}'.",
                nameof(descriptor));
        }

        descriptors.Add(descriptor);
    }
}
