using Npgsql;

namespace SocAlytics.Platform.Persistence;

internal sealed class ModuleConnectionFactory(NpgsqlDataSource dataSource, PersistenceModule module) : IModuleConnectionFactory
{
    public PersistenceModule Module { get; } = module;

    public async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Role names derive only from the closed module set, so the identifier is never caller-supplied.
            await using var command = new NpgsqlCommand($"SET ROLE \"{Module.RuntimeRole}\"", connection);
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
