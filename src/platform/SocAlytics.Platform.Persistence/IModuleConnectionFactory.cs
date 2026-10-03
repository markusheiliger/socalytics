using Npgsql;

namespace SocAlytics.Platform.Persistence;

/// <summary>
/// Opens runtime connections bound to one fixed module. Sessions assume the module's runtime role,
/// so peer-module schemas are denied by the database.
/// </summary>
public interface IModuleConnectionFactory
{
    PersistenceModule Module { get; }

    Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default);
}
