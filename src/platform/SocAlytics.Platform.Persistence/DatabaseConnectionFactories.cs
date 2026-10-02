using Npgsql;

namespace SocAlytics.Platform.Persistence;

internal interface IBootstrapDatabaseConnectionFactory
{
    ValueTask<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default);
}

internal sealed class RuntimeDatabaseConnectionFactory : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;

    public RuntimeDatabaseConnectionFactory(string connectionString)
    {
        _dataSource = NpgsqlDataSource.Create(connectionString);
    }

    public IRuntimeDatabaseConnectionFactory ForModule(PersistenceModuleIdentity module)
    {
        return new ModuleRuntimeDatabaseConnectionFactory(_dataSource, module);
    }

    public ValueTask DisposeAsync()
    {
        return _dataSource.DisposeAsync();
    }
}

internal sealed class ModuleRuntimeDatabaseConnectionFactory(
    NpgsqlDataSource dataSource,
    PersistenceModuleIdentity module) : IRuntimeDatabaseConnectionFactory
{
    public async ValueTask<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            var role = new NpgsqlCommandBuilder().QuoteIdentifier(DatabaseRoleNames.Runtime(module));
            await using var command = new NpgsqlCommand(
                $"SET ROLE {role}",
                connection);
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

internal sealed class BootstrapDatabaseConnectionFactory : IBootstrapDatabaseConnectionFactory, IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;

    public BootstrapDatabaseConnectionFactory(string connectionString)
    {
        _dataSource = NpgsqlDataSource.Create(connectionString);
    }

    public ValueTask<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        return _dataSource.OpenConnectionAsync(cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        return _dataSource.DisposeAsync();
    }
}
