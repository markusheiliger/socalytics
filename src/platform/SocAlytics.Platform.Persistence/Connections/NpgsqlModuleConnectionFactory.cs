using Npgsql;

namespace SocAlytics.Platform.Persistence.Connections;

internal sealed class NpgsqlModuleConnectionFactory(PersistenceOptions options) : IModuleConnectionFactory
{
    public NpgsqlConnection CreateConnection(ModuleKey moduleKey)
    {
        var connectionStringBuilder = new NpgsqlConnectionStringBuilder(options.RuntimeConnectionString)
        {
            SearchPath = moduleKey.ToSchemaName()
        };

        return new NpgsqlConnection(connectionStringBuilder.ConnectionString);
    }
}
