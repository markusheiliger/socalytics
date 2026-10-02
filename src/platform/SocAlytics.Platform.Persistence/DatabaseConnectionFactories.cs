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

    public ValueTask<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        return _dataSource.OpenConnectionAsync(cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        return _dataSource.DisposeAsync();
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
