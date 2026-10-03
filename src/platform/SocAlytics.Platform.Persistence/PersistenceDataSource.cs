using Npgsql;

namespace SocAlytics.Platform.Persistence;

/// <summary>
/// The privileged bootstrap connection. Internal so that module services cannot resolve it;
/// only migration orchestration and role-scoped session creation use it.
/// </summary>
internal sealed class PersistenceDataSource(PersistenceOptions options) : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource = Create(options);

    public NpgsqlDataSource DataSource => _dataSource;

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();

    private static NpgsqlDataSource Create(PersistenceOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            throw new InvalidOperationException("The persistence connection string is not configured.");
        }

        return new NpgsqlDataSourceBuilder(options.ConnectionString).Build();
    }
}
