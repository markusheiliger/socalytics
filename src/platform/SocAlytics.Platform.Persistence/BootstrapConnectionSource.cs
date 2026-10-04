using Npgsql;

namespace SocAlytics.Platform.Persistence;

/// <summary>
/// Privileged bootstrap and migration connections. Internal and registered only for startup
/// orchestration, so module services can neither name nor resolve it.
/// </summary>
internal sealed class BootstrapConnectionSource(NpgsqlDataSource dataSource, string connectionString)
{
    public string ConnectionString { get; } = connectionString;

    public ValueTask<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default) =>
        dataSource.OpenConnectionAsync(cancellationToken);
}
