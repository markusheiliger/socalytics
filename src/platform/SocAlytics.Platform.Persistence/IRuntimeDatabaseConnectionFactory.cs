using Npgsql;

namespace SocAlytics.Platform.Persistence;

public interface IRuntimeDatabaseConnectionFactory
{
    ValueTask<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default);
}
