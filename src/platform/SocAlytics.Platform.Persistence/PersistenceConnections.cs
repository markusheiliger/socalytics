using Npgsql;

namespace SocAlytics.Platform.Persistence;

/// <summary>Opens connections whose session role is the module's runtime role.</summary>
public interface IModuleConnectionFactory
{
    PersistenceModuleKey Module { get; }

    Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken = default);
}

public sealed class PersistenceOptions
{
    /// <summary>Privileged connection string, used only by migration orchestration.</summary>
    public string? BootstrapConnectionString { get; set; }

    /// <summary>Connection string for module sessions; falls back to the bootstrap string when unset.</summary>
    public string? RuntimeConnectionString { get; set; }
}

/// <summary>Privileged connection source. Internal so module services cannot obtain it.</summary>
internal sealed class BootstrapConnectionSource(string connectionString, NpgsqlDataSource dataSource) : IAsyncDisposable
{
    public string ConnectionString { get; } = connectionString;

    public async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken) =>
        await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

    public ValueTask DisposeAsync() => dataSource.DisposeAsync();
}

internal sealed class ModuleConnectionFactory(PersistenceModuleKey module, NpgsqlDataSource dataSource)
    : IModuleConnectionFactory
{
    public PersistenceModuleKey Module { get; } = module;

    public async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SET ROLE \"{Module.RuntimeRole}\"";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
