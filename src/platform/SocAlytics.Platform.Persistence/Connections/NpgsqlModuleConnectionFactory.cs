using Npgsql;

namespace SocAlytics.Platform.Persistence.Connections;

internal sealed class NpgsqlModuleConnectionFactory(PersistenceOptions options) : IModuleConnectionFactory
{
    public NpgsqlConnection CreateConnection(ModuleKey moduleKey)
    {
        // The module runtime role is applied as a session startup option, so every session,
        // including pooled sessions after reset, runs with only that module's privileges.
        var connectionStringBuilder = new NpgsqlConnectionStringBuilder(options.RuntimeConnectionString)
        {
            SearchPath = moduleKey.ToSchemaName(),
            Options = $"-c role={ModuleRoles.RuntimeRoleName(moduleKey)}"
        };

        return new NpgsqlConnection(connectionStringBuilder.ConnectionString);
    }
}
