using Npgsql;

namespace SocAlytics.Platform.Persistence.Connections;

internal sealed class NpgsqlBootstrapConnectionFactory(PersistenceOptions options) : IBootstrapConnectionFactory
{
    public NpgsqlConnection CreateConnection() => new(options.BootstrapConnectionString);
}
