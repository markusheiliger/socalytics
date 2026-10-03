using Npgsql;

namespace SocAlytics.Platform.Persistence;

/// <summary>Opens sessions that run as one module's runtime role.</summary>
public interface IModuleConnectionFactory<TModule>
{
    ModuleKey Module { get; }

    Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken = default);
}

internal sealed class ModuleConnectionFactory<TModule>(ModuleKey module, PersistenceDataSource source)
    : IModuleConnectionFactory<TModule>
{
    public ModuleKey Module { get; } = module;

    public async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = await source.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Role names derive only from the fixed module identity. Pool reset discards the role on return.
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
