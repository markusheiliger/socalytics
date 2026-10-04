using Npgsql;

namespace SocAlytics.Platform.Persistence;

internal sealed class ModuleConnectionFactory(PersistenceModuleKey module, NpgsqlDataSource runtimeDataSource)
    : IModuleConnectionFactory
{
    public PersistenceModuleKey Module { get; } = module;

    public async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = await runtimeDataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            // The role name derives from the fixed module key; Npgsql resets the session when the connection returns to the pool.
            await using var command = connection.CreateCommand();
            command.CommandText = $"SET ROLE \"{Module.RuntimeRole}\"";
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
