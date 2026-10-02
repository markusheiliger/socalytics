using Npgsql;

namespace SocAlytics.Platform.Persistence;

internal interface IBootstrapDatabaseConnectionFactory
{
    ValueTask<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default);
}

internal sealed class RuntimeDatabaseConnectionFactory : IRuntimeDatabaseConnectionFactory, IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;

    public RuntimeDatabaseConnectionFactory(string connectionString)
    {
        _dataSource = NpgsqlDataSource.Create(connectionString);
    }

    public IModuleDatabaseConnectionFactory ForModule(PersistenceModuleIdentity module)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (!PersistenceModuleIdentity.All.Contains(module))
        {
            throw new ArgumentException("The module identity is not adopted.", nameof(module));
        }

        return new ModuleDatabaseConnectionFactory(_dataSource, module);
    }

    public ValueTask DisposeAsync()
    {
        return _dataSource.DisposeAsync();
    }
}

internal sealed class ModuleDatabaseConnectionFactory(NpgsqlDataSource dataSource, PersistenceModuleIdentity module)
    : IModuleDatabaseConnectionFactory
{
    private readonly string _setRole = $"SET ROLE {ModuleRoles.QuoteIdentifier(ModuleRoles.Runtime(module))}";

    public PersistenceModuleIdentity Module => module;

    public async ValueTask<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = new NpgsqlCommand(_setRole, connection);
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
