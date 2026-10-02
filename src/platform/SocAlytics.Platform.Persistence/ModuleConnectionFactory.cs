using Npgsql;

namespace SocAlytics.Platform.Persistence;

/// <summary>Opens normal-runtime connections bound to one fixed module identity.</summary>
public interface IModuleConnectionFactory
{
    PersistenceModuleKey Module { get; }

    Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken = default);
}

internal sealed class ModuleConnectionFactory(PersistenceModuleKey module, NpgsqlDataSource runtimeDataSource)
    : IModuleConnectionFactory
{
    public PersistenceModuleKey Module { get; } = module;

    public async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = await runtimeDataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            // Role names derive only from the fixed module key, never from caller input.
            await using var command = connection.CreateCommand();
            command.CommandText = $"SET ROLE \"{Module.RuntimeRoleName}\"";
            await command.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}

/// <summary>
/// Privileged connection source reserved for migration orchestration. It is internal and is never
/// exposed through a public service type, so module services cannot depend on it.
/// </summary>
internal sealed class BootstrapConnectionSource(NpgsqlDataSource dataSource)
{
    public ValueTask<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken = default) =>
        dataSource.OpenConnectionAsync(cancellationToken);
}
