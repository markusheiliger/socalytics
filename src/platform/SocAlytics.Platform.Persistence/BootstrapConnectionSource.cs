using Npgsql;

namespace SocAlytics.Platform.Persistence;

/// <summary>
/// Privileged connection for migration orchestration only. Internal and never registered under a
/// public service type, so module services cannot request it.
/// </summary>
internal sealed class BootstrapConnectionSource(NpgsqlDataSource dataSource)
{
    public Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default) =>
        dataSource.OpenConnectionAsync(cancellationToken).AsTask();
}
