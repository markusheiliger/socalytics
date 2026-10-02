using Npgsql;

namespace SocAlytics.Platform.Persistence;

public interface IModuleDatabaseConnectionFactory
{
    PersistenceModuleIdentity Module { get; }

    ValueTask<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default);
}
