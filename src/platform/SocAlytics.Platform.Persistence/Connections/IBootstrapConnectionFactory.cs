using Npgsql;

namespace SocAlytics.Platform.Persistence.Connections;

/// <summary>
/// Creates the privileged bootstrap connection used only by migration orchestration. This
/// contract is internal to the persistence boundary and is never registered for, or resolvable
/// by, module application services.
/// </summary>
internal interface IBootstrapConnectionFactory
{
    NpgsqlConnection CreateConnection();
}
