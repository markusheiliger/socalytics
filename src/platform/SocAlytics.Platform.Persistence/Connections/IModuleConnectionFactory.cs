using Npgsql;

namespace SocAlytics.Platform.Persistence.Connections;

/// <summary>
/// Creates module-scoped connections for normal capability-module persistence access. This is
/// the only connection factory type exported publicly by the persistence boundary.
/// </summary>
public interface IModuleConnectionFactory
{
    NpgsqlConnection CreateConnection(ModuleKey moduleKey);
}
